using System;
using System.Threading;
using FenBrowser.Host.Widgets;
using SkiaSharp;

namespace FenBrowser.Host;

/// <summary>
/// Dedicated compositor worker thread.
/// Owns the off-screen raster surface and publishes a presentable frame snapshot.
/// </summary>
public sealed class CompositorThread : IDisposable
{
    private readonly Compositor _compositor;
    private readonly object _frameLock = new();
    private readonly object _stateLock = new();
    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly Thread _thread;

    private bool _running;
    private bool _started;
    private bool _frameRequested = true;
    private int _logicalWidth;
    private int _logicalHeight;
    private float _dpiScale = 1f;

    private SKSurface _outputSurface;
    private SKSizeI _outputPixelSize;
    private SKImage _latestFrame;
    private long _frameSequence;

    public CompositorThread(Compositor compositor)
    {
        _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "FenHost-Compositor"
        };
    }

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _running;
            }
        }
    }

    public long LastCommittedFrameSequence
    {
        get
        {
            lock (_frameLock)
            {
                return _frameSequence;
            }
        }
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_running)
            {
                return;
            }

            if (_started)
            {
                return;
            }

            _running = true;
            _started = true;
        }

        _thread.Start();
        RequestFrame();
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
        }

        _wakeEvent.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    public void UpdateViewport(int logicalWidth, int logicalHeight, float dpiScale)
    {
        lock (_stateLock)
        {
            _logicalWidth = Math.Max(0, logicalWidth);
            _logicalHeight = Math.Max(0, logicalHeight);
            _dpiScale = float.IsFinite(dpiScale) && dpiScale > 0f ? dpiScale : 1f;
            _frameRequested = true;
        }

        _wakeEvent.Set();
    }

    public void RequestFrame()
    {
        lock (_stateLock)
        {
            _frameRequested = true;
        }

        _wakeEvent.Set();
    }

    public bool TryDrawLatest(SKCanvas canvas, SKSize logicalSize)
    {
        if (canvas == null)
        {
            return false;
        }

        lock (_frameLock)
        {
            if (_latestFrame == null)
            {
                return false;
            }

            canvas.DrawImage(_latestFrame, new SKRect(0, 0, logicalSize.Width, logicalSize.Height));
            return true;
        }
    }

    private void ThreadMain()
    {
        while (true)
        {
            _wakeEvent.WaitOne(16);

            int logicalWidth;
            int logicalHeight;
            float dpiScale;
            bool shouldRender;

            lock (_stateLock)
            {
                if (!_running)
                {
                    break;
                }

                logicalWidth = _logicalWidth;
                logicalHeight = _logicalHeight;
                dpiScale = _dpiScale;
                shouldRender = _frameRequested;
                _frameRequested = false;
            }

            if (!shouldRender || logicalWidth <= 0 || logicalHeight <= 0)
            {
                continue;
            }

            EnsureOutputSurface(logicalWidth, logicalHeight, dpiScale);
            if (_outputSurface == null)
            {
                continue;
            }

            var canvas = _outputSurface.Canvas;
            canvas.Clear(SKColors.Transparent);

            lock (Widget.TreeSyncRoot)
            {
                lock (_compositor)
                {
                    _compositor.DpiScale = dpiScale;
                    _compositor.Composite(canvas, new SKSize(logicalWidth, logicalHeight));
                }
            }

            using var snapshot = _outputSurface.Snapshot();
            var rasterImage = snapshot?.ToRasterImage();
            if (rasterImage == null)
            {
                continue;
            }

            lock (_frameLock)
            {
                _latestFrame?.Dispose();
                _latestFrame = rasterImage;
                _frameSequence++;
            }
        }
    }

    private void EnsureOutputSurface(int logicalWidth, int logicalHeight, float dpiScale)
    {
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(logicalWidth * dpiScale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(logicalHeight * dpiScale));
        var targetSize = new SKSizeI(pixelWidth, pixelHeight);

        if (_outputSurface != null && _outputPixelSize == targetSize)
        {
            return;
        }

        _outputSurface?.Dispose();
        _outputSurface = null;

        var info = new SKImageInfo(targetSize.Width, targetSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _outputSurface = SKSurface.Create(info);
        _outputPixelSize = targetSize;
    }

    public void Dispose()
    {
        Stop();

        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }

        _outputSurface?.Dispose();
        _outputSurface = null;

        _wakeEvent.Dispose();
    }
}
