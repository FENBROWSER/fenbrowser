using System;
using System.Diagnostics;
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
    private int _pendingFrameRequests = 1;
    private long _frameRequestsReceived;
    private long _coalescedFrameRequests;
    private int _logicalWidth;
    private int _logicalHeight;
    private float _dpiScale = 1f;
    private readonly TimeSpan _targetFrameInterval;
    private DateTime _nextEligibleFrameUtc = DateTime.MinValue;

    private SKSurface _outputSurface;
    private SKSizeI _outputPixelSize;
    private SKImage _latestFrame;
    private long _frameSequence;
    private long _renderedFrameCount;
    private double _lastFrameDurationMs;

    public CompositorThread(Compositor compositor, int maxFramesPerSecond = 60)
    {
        _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _targetFrameInterval = ComputeFrameInterval(maxFramesPerSecond);
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
            QueueFrameRequestNoSignal();
        }

        _wakeEvent.Set();
    }

    public void RequestFrame()
    {
        lock (_stateLock)
        {
            QueueFrameRequestNoSignal();
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

    public CompositorThreadTelemetry GetTelemetrySnapshot()
    {
        long frameRequestsReceived;
        long coalescedFrameRequests;
        int pendingFrameRequests;
        double targetFrameIntervalMs;

        lock (_stateLock)
        {
            frameRequestsReceived = _frameRequestsReceived;
            coalescedFrameRequests = _coalescedFrameRequests;
            pendingFrameRequests = _pendingFrameRequests;
            targetFrameIntervalMs = _targetFrameInterval.TotalMilliseconds;
        }

        long frameSequence;
        long renderedFrameCount;
        double lastFrameDurationMs;

        lock (_frameLock)
        {
            frameSequence = _frameSequence;
            renderedFrameCount = _renderedFrameCount;
            lastFrameDurationMs = _lastFrameDurationMs;
        }

        return new CompositorThreadTelemetry(
            frameSequence,
            renderedFrameCount,
            frameRequestsReceived,
            coalescedFrameRequests,
            pendingFrameRequests,
            lastFrameDurationMs,
            targetFrameIntervalMs);
    }

    private void ThreadMain()
    {
        while (true)
        {
            lock (_stateLock)
            {
                if (!_running)
                {
                    break;
                }
            }

            _wakeEvent.WaitOne(ComputeWaitTimeoutMilliseconds());

            if (!TryBeginFrame(out var logicalWidth, out var logicalHeight, out var dpiScale))
            {
                continue;
            }

            EnsureOutputSurface(logicalWidth, logicalHeight, dpiScale);
            if (_outputSurface == null)
            {
                continue;
            }

            var frameStartTicks = Stopwatch.GetTimestamp();
            var canvas = _outputSurface.Canvas;
            canvas.Clear(SKColors.Transparent);

            Widget.WithTreeWriteLock(() =>
            {
                lock (_compositor)
                {
                    _compositor.DpiScale = dpiScale;
                    _compositor.Composite(canvas, new SKSize(logicalWidth, logicalHeight));
                }
            });

            using var snapshot = _outputSurface.Snapshot();
            var rasterImage = snapshot?.ToRasterImage();
            if (rasterImage == null)
            {
                continue;
            }

            var frameDurationMs = Stopwatch.GetElapsedTime(frameStartTicks).TotalMilliseconds;
            lock (_frameLock)
            {
                _latestFrame?.Dispose();
                _latestFrame = rasterImage;
                _frameSequence++;
                _renderedFrameCount++;
                _lastFrameDurationMs = frameDurationMs;
            }

            lock (_stateLock)
            {
                _nextEligibleFrameUtc = DateTime.UtcNow + _targetFrameInterval;
            }
        }
    }

    private int ComputeWaitTimeoutMilliseconds()
    {
        lock (_stateLock)
        {
            if (_pendingFrameRequests <= 0)
            {
                return 250;
            }

            var remaining = _nextEligibleFrameUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return 0;
            }

            return Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds));
        }
    }

    private bool TryBeginFrame(out int logicalWidth, out int logicalHeight, out float dpiScale)
    {
        lock (_stateLock)
        {
            logicalWidth = _logicalWidth;
            logicalHeight = _logicalHeight;
            dpiScale = _dpiScale;

            if (!_running)
            {
                return false;
            }

            if (_pendingFrameRequests <= 0 || logicalWidth <= 0 || logicalHeight <= 0)
            {
                return false;
            }

            if (_nextEligibleFrameUtc > DateTime.UtcNow)
            {
                return false;
            }

            _pendingFrameRequests = 0;
            return true;
        }
    }

    private void QueueFrameRequestNoSignal()
    {
        _frameRequestsReceived++;
        if (_pendingFrameRequests > 0)
        {
            _coalescedFrameRequests++;
        }

        _pendingFrameRequests = 1;
    }

    private static TimeSpan ComputeFrameInterval(int maxFramesPerSecond)
    {
        var clamped = Math.Clamp(maxFramesPerSecond, 1, 240);
        return TimeSpan.FromSeconds(1d / clamped);
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

public readonly record struct CompositorThreadTelemetry(
    long CommittedFrameSequence,
    long RenderedFrameCount,
    long FrameRequestsReceived,
    long CoalescedFrameRequests,
    int PendingFrameRequests,
    double LastFrameDurationMs,
    double TargetFrameIntervalMs);
