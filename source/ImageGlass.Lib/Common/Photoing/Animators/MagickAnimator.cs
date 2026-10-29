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
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Threading;

namespace ImageGlass.Common.Photoing;

/// <summary>
/// An <see cref="AnimatorImpl"/> backed by frames the Magick decoder pre-converted
/// to immutable <see cref="SKBitmap"/>s (animated AVIF and APNG). ImageMagick reads
/// a sequence strictly forward, so a per-frame seek re-decodes the whole track;
/// instead, every frame is decoded once up front and the animator only walks them.
/// </summary>
/// <remarks>
/// Each pre-decoded frame is a fully composed image at the animation canvas size, so
/// this animator performs no sub-rect composition or disposal replay, like
/// <c>NativePluginAnimator</c>. The viewer takes ownership of returned images and
/// disposes them as it rotates frames, so every call surfaces a fresh
/// <c>SKImage.FromBitmap</c>, whose pixel memory is shared with the bitmap and
/// therefore survives as long as the animator does.
/// </remarks>
public sealed class MagickAnimator : AnimatorImpl
{
    /// <summary>
    /// Bounds on the timer poll interval, in milliseconds.
    /// </summary>
    private const int MIN_POLL_INTERVAL_MS = 2;
    private const int MAX_POLL_INTERVAL_MS = 16;

    private readonly SKBitmap?[] _frameBitmaps;
    private readonly Lock _syncLock = new();

    private DispatcherTimer _timer;


    /// <summary>
    /// Initializes a new instance of the <see cref="MagickAnimator"/> class.
    /// </summary>
    /// <param name="frames">Per-frame timings, one per bitmap.</param>
    /// <param name="frameBitmaps">Fully decoded frames; entry may be <c>null</c> when its decode failed.</param>
    /// <param name="loopCount">How many times the animation plays; <c>0</c> loops forever.</param>
    public MagickAnimator(SKCodecFrameInfo[] frames, SKBitmap?[] frameBitmaps, int loopCount = 0)
        : base(frames)
    {
        _frameBitmaps = frameBitmaps;
        _loopCount = Math.Max(0, loopCount);

        // poll the clock; a fast animation needs a finer poll than a slow one
        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher.UIThread);
        _timer.Interval = TimeSpan.FromMilliseconds(GetPollIntervalMs(frames));
        _timer.Tick += Timer_Tick;
    }


    /// <summary>
    /// Picks the poll interval; a fixed 16 ms beats at 16 to 31 ms and caps playback near 48 FPS.
    /// </summary>
    private static int GetPollIntervalMs(SKCodecFrameInfo[] frames)
    {
        var shortestDelayMs = int.MaxValue;
        foreach (var frame in frames)
        {
            if (frame.Duration > 0 && frame.Duration < shortestDelayMs) shortestDelayMs = frame.Duration;
        }

        if (shortestDelayMs == int.MaxValue) return MAX_POLL_INTERVAL_MS;

        return Math.Clamp(shortestDelayMs / 4, MIN_POLL_INTERVAL_MS, MAX_POLL_INTERVAL_MS);
    }


    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    protected override void OnDisposing()
    {
        base.OnDisposing();

        lock (_syncLock)
        {
            StopTimer();
            _timer.Tick -= Timer_Tick;

            for (var i = 0; i < _frameBitmaps.Length; i++)
            {
                _frameBitmaps[i]?.Dispose();
                _frameBitmaps[i] = null;
            }
        }
    }


    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    protected override void StartTimer() => _timer.Start();


    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    protected override void StopTimer() => _timer.Stop();

    private void Timer_Tick(object? sender, EventArgs e) => OnTimerTicked();


    /// <summary>
    /// <inheritdoc/> There is no composition, so the pre-decoded frame is handed out as is.
    /// </summary>
    public override SKImage? GetRenderedFrameBitmap(uint frameIndex)
    {
        lock (_syncLock)
        {
            if (IsDisposed) return null;
            if (frameIndex >= (uint)_frameBitmaps.Length) return null;

            var frameBitmap = _frameBitmaps[frameIndex];
            if (frameBitmap is null) return null;

            // an immutable bitmap shares its pixels with the surfaced image, so a copy
            // never happens; the same frame can surface again after the viewer disposes it
            return SKImage.FromBitmap(frameBitmap);
        }
    }

}
