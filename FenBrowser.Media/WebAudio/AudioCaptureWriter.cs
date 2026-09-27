using FenBrowser.Media.Audio;

namespace FenBrowser.Media.WebAudio;

/// <summary>
/// The audio half of <c>HTMLMediaElement.captureStream()</c> (Media Capture from DOM
/// Elements §3): the element's rendered audio, before its volume and muting, written into
/// the pipes its captured audio tracks carry, so a MediaStreamAudioSourceNode hears it.
/// The device's rate is interpolated to the pipes' rate. Nothing is written while the
/// element is not rendering - paused, or waiting for data - which a reader hears as
/// silence.
/// </summary>
public sealed class AudioCaptureWriter : IAudioCapture
{
    private const int Block = 128;

    private readonly float _rate;
    private readonly int _channels;
    private readonly AudioBus _bus;
    private readonly float[] _previous;
    private AudioTrackPipe[] _pipes = [];
    private double _position;
    private int _filled;

    /// <param name="sampleRate">The rate every pipe given to <see cref="SetPipes"/> runs at.</param>
    /// <param name="channels">The channel count every such pipe has.</param>
    public AudioCaptureWriter(float sampleRate, int channels)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels < 1 || channels > WebAudioLimits.MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(channels));
        _rate = sampleRate;
        _channels = channels;
        _bus = new AudioBus(WebAudioLimits.MaxChannels, Block);
        _bus.Reset(channels);
        _previous = new float[WebAudioLimits.MaxChannels];
    }

    /// <summary>The pipes of the captured audio tracks that are live now.</summary>
    public void SetPipes(IReadOnlyList<AudioTrackPipe> pipes)
    {
        ArgumentNullException.ThrowIfNull(pipes);
        Volatile.Write(ref _pipes, pipes.ToArray());
    }

    public void Write(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        if (channels < 1 || sampleRate <= 0)
            return;
        int frames = interleaved.Length / channels;
        if (frames == 0)
            return;

        var pipes = Volatile.Read(ref _pipes);
        double step = sampleRate / (double)_rate;

        // _position is where the next output sample falls, in input frames counted from the
        // start of this call; -1 is the last frame of the previous call.
        while (true)
        {
            int k = (int)Math.Floor(_position);
            if (k + 1 >= frames)
                break;

            float t = (float)(_position - k);
            for (int c = 0; c < _channels; c++)
            {
                int source = c < channels ? c : 0;
                float a = k < 0 ? _previous[source] : interleaved[k * channels + source];
                float b = interleaved[(k + 1) * channels + source];
                _bus.Channel(c)[_filled] = a + (b - a) * t;
            }

            if (++_filled == Block)
            {
                _bus.MarkNotSilent();
                foreach (var pipe in pipes)
                    pipe.Write(_bus);
                _bus.Reset(_channels);
                _filled = 0;
            }

            _position += step;
        }

        _position -= frames;
        for (int c = 0; c < channels && c < _previous.Length; c++)
            _previous[c] = interleaved[(frames - 1) * channels + c];
    }
}
