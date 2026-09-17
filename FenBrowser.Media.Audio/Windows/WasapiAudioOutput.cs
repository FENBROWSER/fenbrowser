using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FenBrowser.Media.Clock;
using static FenBrowser.Media.Audio.Windows.WasapiInterop;

namespace FenBrowser.Media.Audio.Windows;

/// <summary>
/// WASAPI shared-mode, event-driven playback on the default render device (ADR-0003).
/// The stream is opened in the device's mix format (float or 16-bit PCM); the renderer
/// produces whatever format comes back. Position comes from <c>IAudioClock</c>, so the
/// master clock follows what the listener hears, including the device latency.
/// </summary>
/// <remarks>
/// The render thread pre-allocates its buffers and never allocates afterwards. If the
/// device goes away (<c>AUDCLNT_E_DEVICE_INVALIDATED</c>) the output keeps consuming at
/// wall-clock speed so playback carries on silently and the clock keeps moving, which is
/// the fallback ADR-0003 asks for; <see cref="Failure"/> says what happened.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioOutput : IAudioOutput, IAudioPlaybackPosition
{
    private const long BufferDuration100ns = 1_000_000; // 100 ms
    private const int FallbackPeriodMs = 10;

    private readonly object _gate = new();
    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private IAudioClock? _clock;
    private AutoResetEvent? _event;
    private Thread? _thread;
    private IAudioRenderCallback? _callback;
    private AudioStreamFormat _format;
    private bool _floatFormat;
    private uint _bufferFrames;
    private ulong _clockFrequency;
    private float[] _scratch = [];
    private short[] _scratch16 = [];
    private long _underruns;
    private long _framesPlayedAtFailure;
    private long _fallbackFrames;
    private long _lastFallbackTick;
    private volatile bool _running;
    private volatile bool _disposed;
    private volatile bool _fallback;
    private string? _failure;

    public string Name => "wasapi";

    public IAudioPlaybackPosition Position => this;

    public long Underruns => Interlocked.Read(ref _underruns);

    public int SampleRate => _format.SampleRate;

    /// <summary>Why the device stopped being used, or null while it is.</summary>
    public string? Failure => _failure;

    public long FramesPlayed
    {
        get
        {
            if (_fallback)
                return _framesPlayedAtFailure + Interlocked.Read(ref _fallbackFrames);
            var clock = _clock;
            if (clock is null || _clockFrequency == 0)
                return 0;
            try
            {
                if (clock.GetPosition(out ulong position, out _) < 0)
                    return _framesPlayedAtFailure;
                return (long)((UInt128)position * (ulong)_format.SampleRate / _clockFrequency);
            }
            catch (COMException)
            {
                return _framesPlayedAtFailure;
            }
        }
    }

    public ValueTask<AudioStreamFormat> OpenAsync(AudioStreamFormat requested, IAudioRenderCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        Check(enumerator.GetDefaultAudioEndpoint(FlowRender, RoleConsole, out var device), "GetDefaultAudioEndpoint");
        var iid = IidIAudioClient;
        Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object activated), "IMMDevice.Activate");
        var client = (IAudioClient)activated;

        Check(client.GetMixFormat(out IntPtr mixFormat), "GetMixFormat");
        try
        {
            var format = Marshal.PtrToStructure<WaveFormatEx>(mixFormat);
            bool isFloat = format.FormatTag == WaveFormatIeeeFloat;
            if (format.FormatTag == WaveFormatExtensibleTag)
            {
                var extensible = Marshal.PtrToStructure<WaveFormatExtensible>(mixFormat);
                isFloat = extensible.SubFormat == SubtypeIeeeFloat;
                if (!isFloat && extensible.SubFormat != SubtypePcm)
                    throw new InvalidOperationException("The device mix format is neither float nor PCM.");
            }
            else if (format.FormatTag != WaveFormatPcm && !isFloat)
            {
                throw new InvalidOperationException($"Unsupported device mix format tag {format.FormatTag}.");
            }

            if (!isFloat && format.BitsPerSample != 16)
                throw new InvalidOperationException($"Unsupported PCM mix format with {format.BitsPerSample} bits.");
            if (isFloat && format.BitsPerSample != 32)
                throw new InvalidOperationException($"Unsupported float mix format with {format.BitsPerSample} bits.");

            Check(client.Initialize(ShareModeShared, StreamFlagsEventCallback, BufferDuration100ns, 0, mixFormat, IntPtr.Zero), "IAudioClient.Initialize");
            _format = new AudioStreamFormat((int)format.SamplesPerSec, format.Channels);
            _floatFormat = isFloat;
        }
        finally
        {
            Marshal.FreeCoTaskMem(mixFormat);
        }

        Check(client.GetBufferSize(out _bufferFrames), "GetBufferSize");
        _event = new AutoResetEvent(false);
        Check(client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");

        var renderIid = IidIAudioRenderClient;
        Check(client.GetService(ref renderIid, out object renderService), "GetService(IAudioRenderClient)");
        var clockIid = IidIAudioClock;
        Check(client.GetService(ref clockIid, out object clockService), "GetService(IAudioClock)");
        _render = (IAudioRenderClient)renderService;
        _clock = (IAudioClock)clockService;
        Check(_clock.GetFrequency(out _clockFrequency), "IAudioClock.GetFrequency");

        _scratch = new float[_bufferFrames * _format.Channels];
        _scratch16 = _floatFormat ? [] : new short[_bufferFrames * _format.Channels];
        _callback = callback;
        _client = client;

        _thread = new Thread(RenderLoop)
        {
            Name = "fen-audio-wasapi",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        return ValueTask.FromResult(_format);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running || _client is null)
                return;
            if (!_fallback)
            {
                int hr = _client.Start();
                if (hr < 0)
                    EnterFallback($"IAudioClient.Start failed with 0x{hr:X8}");
            }

            _lastFallbackTick = Stopwatch.GetTimestamp();
            _running = true;
            _event?.Set();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running || _client is null)
                return;
            _running = false;
            if (!_fallback)
                _ = _client.Stop();
        }
    }

    private void RenderLoop()
    {
        uint taskIndex = 0;
        IntPtr mmcss = IntPtr.Zero;
        try
        {
            mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        }
        catch (DllNotFoundException)
        {
        }

        try
        {
            while (!_disposed)
            {
                if (_fallback)
                {
                    Thread.Sleep(FallbackPeriodMs);
                    if (_running)
                        PullFallback();
                    continue;
                }

                _event!.WaitOne(2000);
                if (_disposed || !_running)
                    continue;

                try
                {
                    FillDevice();
                }
                catch (COMException ex)
                {
                    EnterFallback($"{ex.Message} (0x{ex.HResult:X8})");
                }
            }
        }
        finally
        {
            if (mmcss != IntPtr.Zero)
                _ = AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    private void FillDevice()
    {
        var client = _client!;
        var render = _render!;
        Check(client.GetCurrentPadding(out uint padding), "GetCurrentPadding");
        uint available = _bufferFrames - padding;
        if (available == 0)
            return;

        Check(render.GetBuffer(available, out IntPtr data), "IAudioRenderClient.GetBuffer");
        int channels = _format.Channels;
        int count = (int)available * channels;
        var span = _scratch.AsSpan(0, count);
        int written = _callback!.Render(span, channels);
        if (written < available)
        {
            Interlocked.Increment(ref _underruns);
            span[(written * channels)..].Clear();
        }

        if (_floatFormat)
        {
            Marshal.Copy(_scratch, 0, data, count);
        }
        else
        {
            var pcm = _scratch16.AsSpan(0, count);
            for (int i = 0; i < count; i++)
                pcm[i] = (short)Math.Clamp(span[i] * 32767f, -32768f, 32767f);
            Marshal.Copy(_scratch16, 0, data, count);
        }

        Check(render.ReleaseBuffer(available, 0), "IAudioRenderClient.ReleaseBuffer");
    }

    /// <summary>Without a device, consume at wall-clock speed so playback and the clock carry on.</summary>
    private void PullFallback()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_lastFallbackTick, now).TotalSeconds;
        _lastFallbackTick = now;
        int frames = (int)Math.Min(_format.SampleRate / 4, Math.Round(seconds * _format.SampleRate));
        int channels = _format.Channels;
        while (frames > 0)
        {
            int chunk = Math.Min(frames, _scratch.Length / channels);
            int written = _callback!.Render(_scratch.AsSpan(0, chunk * channels), channels);
            if (written < chunk)
                Interlocked.Increment(ref _underruns);
            Interlocked.Add(ref _fallbackFrames, chunk);
            frames -= chunk;
        }
    }

    private void EnterFallback(string reason)
    {
        if (_fallback)
            return;
        _framesPlayedAtFailure = FramesPlayed;
        _failure = reason;
        _lastFallbackTick = Stopwatch.GetTimestamp();
        _fallback = true;
        try
        {
            _ = _client?.Stop();
        }
        catch (COMException)
        {
        }
    }

    private static void Check(int hr, string call)
    {
        if (hr < 0)
            throw new COMException($"{call} failed.", hr);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        Stop();
        _event?.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _event?.Dispose();
        if (_render is not null)
            Marshal.FinalReleaseComObject(_render);
        if (_clock is not null)
            Marshal.FinalReleaseComObject(_clock);
        if (_client is not null)
            Marshal.FinalReleaseComObject(_client);
        _render = null;
        _clock = null;
        _client = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Opens WASAPI streams on Windows; on other platforms, or when the device cannot be opened, the null sink.</summary>
public sealed class PlatformAudioOutputFactory : IAudioOutputFactory
{
    public static readonly PlatformAudioOutputFactory Instance = new();

    /// <summary>Set when the last attempt to open the platform device failed, for the media log.</summary>
    public string? LastFailure { get; private set; }

    public IAudioOutput Create()
    {
        if (OperatingSystem.IsWindows())
            return new FallbackOnOpenFailure(this);
        return new NullAudioOutput();
    }

    /// <summary>
    /// Tries the WASAPI stream at open time; if the device cannot be opened (no audio
    /// hardware, a headless session, a remote desktop without audio) the null sink takes
    /// over so playback and the clock still run (ADR-0003 rollback).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class FallbackOnOpenFailure(PlatformAudioOutputFactory owner) : IAudioOutput
    {
        private IAudioOutput _inner = new WasapiAudioOutput();

        public string Name => _inner.Name;
        public IAudioPlaybackPosition Position => _inner.Position;
        public long Underruns => _inner.Underruns;

        public async ValueTask<AudioStreamFormat> OpenAsync(AudioStreamFormat requested, IAudioRenderCallback callback, CancellationToken cancellationToken)
        {
            try
            {
                return await _inner.OpenAsync(requested, callback, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException)
            {
                owner.LastFailure = $"{ex.GetType().Name}: {ex.Message}";
                await _inner.DisposeAsync().ConfigureAwait(false);
                _inner = new NullAudioOutput();
                return await _inner.OpenAsync(requested, callback, cancellationToken).ConfigureAwait(false);
            }
        }

        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
