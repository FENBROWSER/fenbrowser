using System;
using System.Diagnostics;
using System.Threading;
using FenBrowser.Host.ProcessIsolation.Gpu;
using FenBrowser.Host.Widgets;
using SkiaSharp;

namespace FenBrowser.Host;

/// <summary>
/// Dedicated compositor worker thread.
/// Owns the off-screen raster surface and publishes an immutable presentable frame snapshot.
/// </summary>
public sealed class CompositorThread : IDisposable
{
    private readonly Compositor _compositor;
    private readonly object _frameLock = new();
    private readonly object _stateLock = new();
    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly Thread _thread;
    private readonly ICompositorWorkSubmitter _compositorWorkSubmitter;
    private Action<CoalescedPointerMoveEvent> _pointerMoveDispatcher;

    private bool _running;
    private bool _started;
    private int _pendingFrameRequests = 1;
    private bool _hasPendingPointerMove;
    private CoalescedPointerMoveEvent _pendingPointerMove;
    private long _frameRequestsReceived;
    private long _coalescedFrameRequests;
    private long _pointerMoveEventsReceived;
    private long _coalescedPointerMoveEvents;
    private long _pointerMoveDispatchCount;
    private int _logicalWidth;
    private int _logicalHeight;
    private float _dpiScale = 1f;
    private readonly TimeSpan _targetFrameInterval;
    private readonly long _targetFrameIntervalTicks;
    private long _nextEligibleFrameTimestamp;

    private SKSurface _outputSurface;
    private SKSizeI _outputPixelSize;
    private SKImage _latestFrame;
    private long _frameSequence;
    private long _renderedFrameCount;
    private double _lastFrameDurationMs;
    private long _gpuSubmissionAttemptCount;
    private long _gpuSubmissionSuccessCount;

    public CompositorThread(
        Compositor compositor,
        int maxFramesPerSecond = 60,
        ICompositorWorkSubmitter compositorWorkSubmitter = null)
    {
        _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _targetFrameInterval = ComputeFrameInterval(maxFramesPerSecond);
        _targetFrameIntervalTicks = Math.Max(
            1L,
            (long)Math.Ceiling(_targetFrameInterval.TotalSeconds * Stopwatch.Frequency));
        _compositorWorkSubmitter = compositorWorkSubmitter ?? new GpuCompositorWorkSubmitter();

        // Temporary resilience budget until the remaining recursive paint/layout walks
        // are converted to explicit work stacks. Do not raise this further to solve
        // site-specific failures; excessive logical depth belongs in engine limits.
        const int CompositorStackBytes = 16 * 1024 * 1024;
        _thread = new Thread(ThreadMain, CompositorStackBytes)
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
            if (_running || _started)
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

    public void SetPointerMoveDispatcher(Action<CoalescedPointerMoveEvent> dispatcher)
    {
        lock (_stateLock)
        {
            _pointerMoveDispatcher = dispatcher;
        }
    }

    public void QueuePointerMove(in CoalescedPointerMoveEvent pointerMove)
    {
        lock (_stateLock)
        {
            _pointerMoveEventsReceived++;
            if (_hasPendingPointerMove)
            {
                _coalescedPointerMoveEvents++;
            }

            _pendingPointerMove = pointerMove;
            _hasPendingPointerMove = true;
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

            canvas.DrawImage(
                _latestFrame,
                new SKRect(0, 0, logicalSize.Width, logicalSize.Height),
                SKSamplingOptions.Default);
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

        long pointerMoveEventsReceived;
        long coalescedPointerMoveEvents;
        long pointerMoveDispatchCount;
        lock (_stateLock)
        {
            pointerMoveEventsReceived = _pointerMoveEventsReceived;
            coalescedPointerMoveEvents = _coalescedPointerMoveEvents;
            pointerMoveDispatchCount = _pointerMoveDispatchCount;
        }

        var lastSubmittedGpuSequence = _compositorWorkSubmitter?.LastSubmittedFrameSequence ?? 0;
        var lastAcknowledgedGpuSequence = _compositorWorkSubmitter?.LastAcknowledgedFrameSequence ?? 0;

        return new CompositorThreadTelemetry(
            frameSequence,
            renderedFrameCount,
            frameRequestsReceived,
            coalescedFrameRequests,
            pendingFrameRequests,
            lastFrameDurationMs,
            targetFrameIntervalMs,
            Interlocked.Read(ref _gpuSubmissionAttemptCount),
            Interlocked.Read(ref _gpuSubmissionSuccessCount),
            lastSubmittedGpuSequence,
            lastAcknowledgedGpuSequence,
            pointerMoveEventsReceived,
            coalescedPointerMoveEvents,
            pointerMoveDispatchCount);
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
            DispatchPendingPointerMove();

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
                    _compositor.Composite(
                        canvas,
                        new SKSize(logicalWidth, logicalHeight));
                }
            });

            // _outputSurface is created as a CPU/raster SKSurface. Snapshot() already
            // returns an immutable image backed by that completed raster content.
            // Calling ToRasterImage() here materialized a second full-size frame every
            // commit, adding an O(viewport-pixels) copy/allocation before presentation.
            var committedFrame = _outputSurface.Snapshot();
            if (committedFrame == null)
            {
                continue;
            }

            var frameDurationMs = Stopwatch.GetElapsedTime(frameStartTicks).TotalMilliseconds;
            long committedSequence;
            lock (_frameLock)
            {
                _latestFrame?.Dispose();
                _latestFrame = committedFrame;
                _frameSequence++;
                committedSequence = _frameSequence;
                _renderedFrameCount++;
                _lastFrameDurationMs = frameDurationMs;
            }

            TrySubmitGpuCompositorWork(
                committedSequence,
                logicalWidth,
                logicalHeight,
                _outputPixelSize.Width,
                _outputPixelSize.Height,
                dpiScale,
                frameDurationMs);

            lock (_stateLock)
            {
                _nextEligibleFrameTimestamp = SaturatingAdd(
                    Stopwatch.GetTimestamp(),
                    _targetFrameIntervalTicks);
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

            var remainingTicks = _nextEligibleFrameTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return 0;
            }

            var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            return Math.Max(1, (int)Math.Ceiling(Math.Min(remainingMs, int.MaxValue)));
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

            if (_nextEligibleFrameTimestamp > Stopwatch.GetTimestamp())
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

    private static long SaturatingAdd(long value, long increment)
    {
        if (increment <= 0)
        {
            return value;
        }

        return value > long.MaxValue - increment
            ? long.MaxValue
            : value + increment;
    }

    private void DispatchPendingPointerMove()
    {
        Action<CoalescedPointerMoveEvent> dispatcher;
        CoalescedPointerMoveEvent pointerMove;
        lock (_stateLock)
        {
            if (!_hasPendingPointerMove || _pointerMoveDispatcher == null)
            {
                return;
            }

            dispatcher = _pointerMoveDispatcher;
            pointerMove = _pendingPointerMove;
            _hasPendingPointerMove = false;
            _pointerMoveDispatchCount++;
        }

        try
        {
            dispatcher(pointerMove);
        }
        catch
        {
            // Pointer-move callback failures must not terminate the compositor thread.
        }
    }

    private void TrySubmitGpuCompositorWork(
        long frameSequence,
        int logicalWidth,
        int logicalHeight,
        int pixelWidth,
        int pixelHeight,
        float dpiScale,
        double composeDurationMs)
    {
        if (_compositorWorkSubmitter == null || frameSequence <= 0)
        {
            return;
        }

        var workItem = new GpuCompositorWorkItem(
            frameSequence,
            logicalWidth,
            logicalHeight,
            pixelWidth,
            pixelHeight,
            dpiScale,
            composeDurationMs,
            _targetFrameInterval.TotalMilliseconds);

        Interlocked.Increment(ref _gpuSubmissionAttemptCount);
        if (_compositorWorkSubmitter.TrySubmit(workItem))
        {
            Interlocked.Increment(ref _gpuSubmissionSuccessCount);
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

        var info = new SKImageInfo(
            targetSize.Width,
            targetSize.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
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
    double TargetFrameIntervalMs,
    long GpuSubmissionAttemptCount,
    long GpuSubmissionSuccessCount,
    long LastSubmittedGpuFrameSequence,
    long LastAcknowledgedGpuFrameSequence,
    long PointerMoveEventsReceived,
    long CoalescedPointerMoveEvents,
    long PointerMoveDispatchCount);

public readonly record struct CoalescedPointerMoveEvent(
    int TabId,
    float X,
    float Y,
    float ViewportLeft,
    float ViewportTop);
