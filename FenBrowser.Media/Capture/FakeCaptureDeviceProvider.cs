using System.Diagnostics;
using FenBrowser.Media.Streams;
using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Capture;

/// <summary>
/// A microphone that plays a 440 Hz tone and a camera that shows a moving test pattern, in
/// real time - for tests and headless runs, where a page's getUserMedia must get something
/// that behaves like a device without there being one.
/// </summary>
public sealed class FakeCaptureDeviceProvider : ICaptureDeviceProvider
{
    public const string MicrophoneId = "fake-audio-input";
    public const string CameraId = "fake-video-input";

    private static readonly CaptureDeviceInfo Microphone =
        new(MicrophoneId, "fake-devices", "Fake Microphone", MediaTrackKind.Audio, SampleRate: 48000, Channels: 1);

    private static readonly CaptureDeviceInfo Camera =
        new(CameraId, "fake-devices", "Fake Camera", MediaTrackKind.Video, Width: 640, Height: 480, FrameRate: 30);

    public IReadOnlyList<CaptureDeviceInfo> Devices { get; } = [Microphone, Camera];

    public ICaptureSession? Open(string deviceId) => deviceId switch
    {
        MicrophoneId => new ToneSession(Microphone),
        CameraId => new PatternSession(Camera),
        _ => null,
    };

    /// <summary>A sine at 440 Hz, written into the pipe as fast as real time passes.</summary>
    private sealed class ToneSession : ICaptureSession
    {
        private const double Frequency = 440;
        private readonly AudioTrackPipe _pipe;
        private readonly AudioBus _block = new(1);
        private readonly Timer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private long _produced;
        private bool _disposed;

        public ToneSession(CaptureDeviceInfo device)
        {
            Device = device;
            _pipe = new AudioTrackPipe(device.SampleRate, device.Channels);
            _timer = new Timer(_ => Produce(), null, 0, 10);
        }

        public CaptureDeviceInfo Device { get; }

        public AudioTrackPipe? Audio => _pipe;

        public VideoTrackSource? Video => null;

        private void Produce()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                long due = (long)(_clock.Elapsed.TotalSeconds * Device.SampleRate);
                while (_produced + _block.Frames <= due)
                {
                    _block.Reset(1);
                    var samples = _block.Channel(0);
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = 0.5f * (float)Math.Sin(2 * Math.PI * Frequency * (_produced + i) / Device.SampleRate);
                    _block.MarkNotSilent();
                    _pipe.Write(_block);
                    _produced += _block.Frames;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
                _disposed = true;
            _timer.Dispose();
        }
    }

    /// <summary>A background whose hue turns over time and a white bar that sweeps across it.</summary>
    private sealed class PatternSession : ICaptureSession
    {
        private readonly VideoTrackSource _source = new();
        private readonly byte[] _pixels;
        private readonly Timer _timer;
        private readonly object _gate = new();
        private long _frame;
        private bool _disposed;

        public PatternSession(CaptureDeviceInfo device)
        {
            Device = device;
            _pixels = new byte[device.Width * device.Height * 4];
            _timer = new Timer(_ => Produce(), null, 0, (int)(1000 / device.FrameRate));
        }

        public CaptureDeviceInfo Device { get; }

        public AudioTrackPipe? Audio => null;

        public VideoTrackSource? Video => _source;

        private void Produce()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                int width = Device.Width, height = Device.Height;
                double hue = (_frame % 360) / 360.0;
                var (r, g, b) = HueToRgb(hue);
                int bar = (int)(_frame * 8 % width);
                for (int y = 0; y < height; y++)
                {
                    int row = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int at = row + x * 4;
                        bool onBar = x >= bar && x < bar + 16;
                        _pixels[at] = onBar ? (byte)255 : b;
                        _pixels[at + 1] = onBar ? (byte)255 : g;
                        _pixels[at + 2] = onBar ? (byte)255 : r;
                        _pixels[at + 3] = 255;
                    }
                }

                _frame++;
                _source.Publish(_pixels, width, height, width * 4);
            }
        }

        private static (byte R, byte G, byte B) HueToRgb(double hue)
        {
            double h = hue * 6;
            double x = 1 - Math.Abs(h % 2 - 1);
            var (r, g, b) = (int)h switch
            {
                0 => (1.0, x, 0.0),
                1 => (x, 1.0, 0.0),
                2 => (0.0, 1.0, x),
                3 => (0.0, x, 1.0),
                4 => (x, 0.0, 1.0),
                _ => (1.0, 0.0, x),
            };
            return ((byte)(r * 200), (byte)(g * 200), (byte)(b * 200));
        }

        public void Dispose()
        {
            lock (_gate)
                _disposed = true;
            _timer.Dispose();
        }
    }
}
