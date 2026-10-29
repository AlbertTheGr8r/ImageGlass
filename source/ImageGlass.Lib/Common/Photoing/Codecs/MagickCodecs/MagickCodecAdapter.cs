/*
ImageGlass - A Fast, Seamless Photo Viewer
Copyright (C) 2010 - 2026 DUONG DIEU PHAP
Project homepage: https://imageglass.org

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/
using Avalonia;
using ImageGlass.Common.Extensions;
using ImageGlass.Common.Loggers;
using ImageGlass.Common.Types;
using ImageMagick;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ImageGlass.Common.Photoing;

/// <summary>
/// Provides Magick-based metadata loading and decode fallback.
/// </summary>
public sealed class MagickCodecAdapter : PhDisposable, ICodec
{
    private static readonly string[] _supportedExtensions = [];

    /// <summary>
    /// Pixel bytes a decoded animation may hold in memory before it stops animating and
    /// opens as a still frame instead. Unlike the Skia pipeline, which composites on the
    /// fly and keeps 5 frames, the Magick sequence decoder reads the track strictly
    /// forward, so every frame is pre-decoded up front; without a ceiling, an 8K sequence
    /// could pin more than the whole photo cache budget. 1 GiB fits today's sample
    /// (18 frames at 12.6 MB), several hundred frames of phone-photo-size loops, and
    /// rejects absurd content that would otherwise thrash.
    /// <para>
    /// The gate sizes the FINAL frames only: the read itself transients much higher,
    /// because ImageMagick holds every frame as Q16-HDRI float pixels through the whole
    /// sequence read (measured on Windows: 150 frames of 1080² need 700 MB of finished
    /// frames but peak around 2.2 GB). The freed-early MagickImage in
    /// <see cref="DecodeSequenceFrames__"/> trims the tail of that peak, not the read.
    /// </para>
    /// </summary>
    internal const long MaxAnimationBufferBytes = 1024L * 1024 * 1024;


    /// <inheritdoc/>
    public string CodecId { get; } = "magick.net";

    /// <inheritdoc/>
    public string CodecName { get; } = "Magick.NET";

    /// <inheritdoc/>
    public int MetadataPriority { get; } = 100;

    /// <inheritdoc/>
    public int DecodePriority { get; } = 10;

    /// <inheritdoc/>
    public int EncodePriority { get; } = 10;

    /// <inheritdoc/>
    public IReadOnlyList<string> DecodingExtensions => _supportedExtensions;

    /// <inheritdoc/>
    public bool SupportsEncoding { get; } = true;

    /// <inheritdoc/>
    public IReadOnlyList<string> EncodingExtensions => _supportedExtensions;


    /// <inheritdoc/>
    public bool CanLoadMetadata(string filePath)
    {
        return !string.IsNullOrWhiteSpace(filePath)
            && MagickCodec.CanRead(filePath);
    }


    /// <inheritdoc/>
    public Task<PhotoMetadata> LoadMetadataAsync(string filePath,
        PhotoReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return MagickCodec.LoadMetadataAsync(filePath, options, null, cancellationToken);
    }


    /// <inheritdoc/>
    public bool CanDecode(PhotoMetadata metadata, CodecSelectionContext context)
    {
        return !string.IsNullOrWhiteSpace(metadata?.FilePath)
            && MagickCodec.CanRead(metadata.FilePath);
    }


    /// <inheritdoc/>
    public async Task<CodecDecodeResult> DecodeAsync(PhotoMetadata metadata,
        PhotoReadOptions options,
        CodecSelectionContext context,
        CancellationToken cancellationToken = default)
    {
        // A size box only ever comes from the thumbnail/preview path, which wants one still;
        // decoding the whole animation for it reads the entire canvas for nothing.
        var wantsScaledStill = options.Width > 0 && options.Height > 0;

        // The budget is estimated from the metadata frame dimensions before any pixels are
        // read: HDR frames land at RgbaF32 (16 bytes/px), SDR at Rgba8888 (4 bytes/px).
        var bytesPerPixel = metadata.IsHdr ? 16 : 4;
        var estimatedBytes = (long)Math.Max(0, metadata.Width) * Math.Max(0, metadata.Height)
            * bytesPerPixel * Math.Max(1, metadata.FrameCount);
        var isOverBudget = estimatedBytes > MaxAnimationBufferBytes;

        if (!wantsScaledStill && !isOverBudget
            && MagickCodec.CanAnimateAsSequence(metadata.FileExtension, metadata.FrameCount, metadata.FilePath))
        {
            return await Task.Factory.StartNew(
                () => DecodeSequenceFrames__(metadata, options, cancellationToken),
                cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .Unwrap().ConfigureAwait(false);
        }

        using var output = await MagickCodec.DecodeImageAsync(
            metadata,
            options,
            null,
            null,
            cancellationToken).ConfigureAwait(false);

        // tag what the decode converted to, so the viewer never converts the same pixels twice
        var image = SkiaCodec.FromMagick(output.SingleFrame, MagickCodec.GetDecodedColorSpace(output, metadata),
            metadata.IsHdr, out var decodeScale);

        return new CodecDecodeResult
        {
            CodecId = CodecId,
            ContentKind = image is not null ? CodecContentKind.StaticRaster : CodecContentKind.None,
            Size = new Size(image?.Width ?? 0, image?.Height ?? 0),
            DecodeScale = decodeScale,
            SingleFrame = image,
            IsHdr = metadata.IsHdr,
            HasEmbeddedColorProfile = metadata.SkiaColorSpace is not null || metadata.MagickColorProfile is not null,
            HasBakedDisplayProfile = MagickCodec.HasBakedDisplayProfile(output, metadata),
        };
    }


    /// <summary>
    /// Decodes every frame of a sequence container and builds a <see cref="MagickAnimator"/>.
    /// ImageMagick reads a sequence strictly forward, so per-frame seeks re-decode the whole
    /// track; all frames are decoded once here instead, on this background thread.
    /// </summary>
    private async Task<CodecDecodeResult> DecodeSequenceFrames__(PhotoMetadata metadata,
        PhotoReadOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // read all frames through the channel pipeline that does color conversions per frame;
        // the output owns the frames, so conversion must happen inside its using scope
        using var output = await MagickCodec.DecodeImageAsync(metadata,
            options with { FrameIndex = -1 }, null, null, token).ConfigureAwait(false);

        var imgColl = output.MultiFrames;
        if (imgColl is null || imgColl.Count < 2)
        {
            // the metadata promised an animation but the decode disagrees; one frame still shows
            SKImage? onlyFrame = null;
            var decodeScale = 1d;
            if (imgColl?.Count == 1)
            {
                onlyFrame = SkiaCodec.FromMagick((MagickImage)imgColl[0],
                    MagickCodec.GetDecodedColorSpace(output, metadata), metadata.IsHdr, out decodeScale);
            }

            return new CodecDecodeResult
            {
                CodecId = CodecId,
                ContentKind = onlyFrame is not null ? CodecContentKind.StaticRaster : CodecContentKind.None,
                Size = new Size(onlyFrame?.Width ?? 0, onlyFrame?.Height ?? 0),
                DecodeScale = decodeScale,
                SingleFrame = onlyFrame,
                IsHdr = metadata.IsHdr,
                HasEmbeddedColorProfile = metadata.SkiaColorSpace is not null || metadata.MagickColorProfile is not null,
                HasBakedDisplayProfile = MagickCodec.HasBakedDisplayProfile(output, metadata),
            };
        }

        var frameCount = imgColl.Count;
        var frameInfos = new SKCodecFrameInfo[frameCount];
        var frameBitmaps = new SKBitmap?[frameCount];
        Size frameSize = default;
        var minDecodeScale = double.PositiveInfinity;
        var totalBytes = 0L;

        try
        {
            for (var i = 0; i < frameCount; i++)
            {
                token.ThrowIfCancellationRequested();

                var imgFrameM = (MagickImage)imgColl[i];
                using var imgFrame = SkiaCodec.FromMagick(imgFrameM,
                    MagickCodec.GetDecodedColorSpace(output, metadata), metadata.IsHdr, out var frameScale);
                if (frameScale < minDecodeScale) minDecodeScale = frameScale;

                // frame N owns its own delay; a reader without ticks falls back to the GIF 1/100 s
                var ticksPerSecond = imgFrameM.AnimationTicksPerSecond;
                frameInfos[i] = new SKCodecFrameInfo
                {
                    Duration = ticksPerSecond > 0
                        ? (int)Math.Clamp(Math.Round(imgFrameM.AnimationDelay * 1000d / ticksPerSecond), 0, int.MaxValue)
                        : 0,
                    AlphaType = imgFrame.IsDisposed() ? SKAlphaType.Opaque : imgFrame.AlphaType,

                    // Magick delivers fully composed frames, so no frame ever builds on another
                    RequiredFrame = -1,
                    DisposalMethod = SKCodecAnimationDisposalMethod.Keep,
                };

                if (i == 0 && imgFrame is { } firstFrame)
                {
                    frameSize = new Size(firstFrame.Width, firstFrame.Height);
                    totalBytes = (long)firstFrame.Info.BytesPerPixel * firstFrame.Width * firstFrame.Height * frameCount;
                }

                // FromMagick copies the pixels into its own buffer and the animator keeps only the
                // immutable SKBitmap, so free this decoded Magick frame now instead of at the end of
                // the loop. Otherwise a long sequence holds every frame twice and peak memory doubles.
                imgFrameM.Dispose();

                if (imgFrame.IsDisposed()) continue;

                // the animator surfaces each pre-decoded frame per request; the immutable copy
                // below is its only copy: SKImage.FromBitmap shares the pixels, never re-copies
                var frameBitmap = SKBitmap.FromImage(imgFrame);
                frameBitmap.SetImmutable();
                frameBitmaps[i] = frameBitmap;
            }
        }
        catch
        {
            // a cancelled or failed decode owns the frames copied so far and nothing else will
            foreach (var frameBitmap in frameBitmaps) frameBitmap?.Dispose();
            throw;
        }

        var animator = new MagickAnimator(frameInfos, frameBitmaps,
            (int)Math.Clamp(metadata.AnimationLoop, 0, int.MaxValue));

        PhotoTrace.Mark("decode:animation", metadata.FilePath,
            $"magick frames={frameCount} size={frameSize.Width}x{frameSize.Height} "
            + $"loop={metadata.AnimationLoop} bytes={totalBytes:n0}");

        return new CodecDecodeResult
        {
            CodecId = CodecId,
            ContentKind = CodecContentKind.Animation,
            Size = frameSize,
            DecodeScale = double.IsPositiveInfinity(minDecodeScale) ? 1 : minDecodeScale,
            Animator = animator,
            IsHdr = metadata.IsHdr,
            HasEmbeddedColorProfile = metadata.SkiaColorSpace is not null || metadata.MagickColorProfile is not null,
            HasBakedDisplayProfile = MagickCodec.HasBakedDisplayProfile(output, metadata),
        };
    }


    /// <inheritdoc/>
    public bool CanEncode(string destFilePath, CodecEncodeContext context)
    {
        return !string.IsNullOrWhiteSpace(destFilePath)
            && MagickCodec.CanWrite(destFilePath);
    }


    /// <inheritdoc/>
    public async Task<CodecEncodeResult> EncodeAsync(CodecEncodeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanEncode(request.DestFilePath, CodecEncodeContext.Default))
        {
            return new CodecEncodeResult(false, true);
        }

        try
        {
            // transform is already applied by the caller, so pass null
            await SkiaCodec.SaveAsync(request.Source, request.DestFilePath, null,
                request.Quality, cancellationToken).ConfigureAwait(false);
            return new CodecEncodeResult(true, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new CodecEncodeResult(false, false, ex.Message);
        }
    }


    /// <summary>
    /// Always <c>false</c>: the built-in multi-frame writer is <c>MagickCodec.SaveAsync</c>, which
    /// re-reads the source collection and so preserves palettes, disposal and the GIF coalesce
    /// path far better than a frame-by-frame pixel round trip.
    /// </summary>
    public bool CanEncodeMultiFrame(string destFilePath, CodecEncodeContext context) => false;


    /// <inheritdoc/>
    public Task<CodecEncodeResult> EncodeMultiFrameAsync(CodecMultiFrameEncodeRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new CodecEncodeResult(false, true));
}