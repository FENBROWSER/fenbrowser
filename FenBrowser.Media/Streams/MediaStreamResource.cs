using FenBrowser.Media.Audio;
using FenBrowser.Media.Element;
using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Streams;

/// <summary>
/// A MediaStream playing in a media element (mediacapture-main 6, "MediaStreams in media
/// elements"): a live resource with an infinite duration and nothing seekable, ready as
/// soon as the stream is active, whose position is the time it has spent playing and which
/// ends when the stream goes inactive. Its audio is every live, enabled audio track's pipe,
/// read from just behind the live edge, resampled to the device rate and mixed.
/// </summary>
public sealed class MediaStreamResource : IMediaResource, IAudioRenderCallback
{
    private const int ScratchFrames = 8192;

    // How far behind a pipe's newest frame reading starts, in the pipe's frames: enough
    // that the producer's next block lands before the reader needs it.
    private const int LiveLatencyFrames = 2048;

    private static readonly AudioStreamFormat Requested = new(48000, 2);

    private readonly LiveStreamSource _source;
    private readonly IMediaResourceClient _client;
    private readonly Action<Action> _post;
    private readonly IAudioOutputFactory _outputs;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ITimer _positionTimer;

    // Audio thread only.
    private readonly Dictionary<AudioTrackPipe, double> _readPositions = new();
    private readonly float[][] _scratch;

    private IAudioOutput? _output;
    private IAudioCapture? _capture;
    private AudioStreamFormat _format = Requested;
    private string _sink = string.Empty;
    private volatile bool _playing;
    private volatile float _volume = 1f;
    private long _playingSince;
    private double _playedSeconds;
    private bool _metadataReported;
    private bool _endReported;
    private int _disposed;

    public MediaStreamResource(
        LiveStreamSource source,
        IMediaResourceClient client,
        Action<Action> postToElementThread,
        IAudioOutputFactory outputs,
        TimeProvider? time = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _post = postToElementThread ?? throw new ArgumentNullException(nameof(postToElementThread));
        _outputs = outputs ?? throw new ArgumentNullException(nameof(outputs));
        _time = time ?? TimeProvider.System;
        _scratch = new float[WebAudioLimits.MaxChannels][];
        for (int c = 0; c < _scratch.Length; c++)
            _scratch[c] = new float[ScratchFrames];

        _source.Changed += OnSourceChanged;
        _positionTimer = _time.CreateTimer(_ => ReportPosition(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        _post(Evaluate);
    }

    /// <summary>A stream stops delaying the load event at once, like a MediaSource.</summary>
    public bool IsProviderObject => true;

    /// <summary>Seconds of playback so far: a stream's position is the time it has played.</summary>
    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                return _playedSeconds + (_playing ? _time.GetElapsedTime(_playingSince).TotalSeconds : 0);
            }
        }
    }

    public void UpdatePlayback(bool potentiallyPlaying, double playbackRate, bool preservesPitch, double effectiveVolume)
    {
        _volume = (float)Math.Clamp(effectiveVolume, 0, 1);
        lock (_gate)
        {
            if (potentiallyPlaying == _playing)
                return;

            if (potentiallyPlaying)
            {
                _playingSince = _time.GetTimestamp();
            }
            else
            {
                _playedSeconds += _time.GetElapsedTime(_playingSince).TotalSeconds;
            }

            _playing = potentiallyPlaying;
        }

        UpdateOutput();
    }

    // Nothing in a live stream is seekable; the position stays where it is.
    public void Seek(MediaTime target, bool approximateForSpeed) =>
        _post(() => _client.SeekCompleted(MediaTime.FromSeconds(PositionSeconds)));

    public void RequestFullLoad()
    {
    }

    /// <summary>
    /// captureStream() on the element: the tee receives the mixed stream before the
    /// element's volume and muting (mediacapture-fromelement: muting the element does not
    /// silence the capture).
    /// </summary>
    public void SetAudioCapture(IAudioCapture? capture) => Volatile.Write(ref _capture, capture);

    public void SetAudioSink(string deviceId)
    {
        IAudioOutput? previous;
        lock (_gate)
        {
            _sink = deviceId ?? string.Empty;
            previous = _output;
            _output = null;
        }

        if (previous is not null)
            _ = Task.Run(async () => await previous.DisposeAsync().ConfigureAwait(false));
        UpdateOutput();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _source.Changed -= OnSourceChanged;
        _positionTimer.Dispose();
        IAudioOutput? output;
        lock (_gate)
        {
            output = _output;
            _output = null;
            _playing = false;
        }

        if (output is not null)
            _ = Task.Run(async () => await output.DisposeAsync().ConfigureAwait(false));
    }

    private void OnSourceChanged() => _post(Evaluate);

    // Element thread: report what the stream now is.
    private void Evaluate()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var tracks = _source.Tracks;
        if (_source.Active)
        {
            // mediacapture-main 6: an active stream is ready at once; it has no duration
            // and nothing seekable or buffered. A track added later reports anew.
            var infos = new List<MediaTrackInfo>();
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (!track.Live)
                    continue;
                var config = track.Kind == MediaTrackKind.Audio
                    ? CodecConfig.Audio(MediaCodec.Pcm, Requested.SampleRate, Requested.Channels)
                    : CodecConfig.Video(MediaCodec.Unknown, 0, 0);
                infos.Add(new MediaTrackInfo(i, config, MediaTime.PositiveInfinity, Label: track.Id, IsDefault: true));
            }

            _client.MetadataAvailable(new MediaResourceMetadata(MediaTime.PositiveInfinity, 0, 0, infos));
            _client.SeekableChanged(MediaTimeRanges.Empty);
            _client.BufferedChanged(MediaTimeRanges.Empty);
            _client.ReadyStateChanged(MediaReadyState.HaveEnoughData);
            _metadataReported = true;
            _endReported = false;
            UpdateOutput();
        }
        else if (_metadataReported && !_endReported)
        {
            // The stream went inactive: playback has ended.
            _endReported = true;
            _client.ReachedEnd();
        }
    }

    private void ReportPosition()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_playing)
            return;

        var position = MediaTime.FromSeconds(PositionSeconds);
        _post(() => _client.PositionChanged(position, monotonic: true));
    }

    // Opens and starts the device while the element is playing a stream with audio, and
    // stops it otherwise.
    private void UpdateOutput()
    {
        bool want = _playing && _source.HasAudio && Volatile.Read(ref _disposed) == 0;
        IAudioOutput? output;
        lock (_gate)
        {
            output = _output;
            if (want && output is null)
            {
                output = _outputs.Create(_sink) ?? _outputs.Create();
                _output = output;
                var opening = output;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var format = await opening.OpenAsync(Requested, this, CancellationToken.None).ConfigureAwait(false);
                        lock (_gate)
                        {
                            if (!ReferenceEquals(_output, opening))
                                return;
                            _format = format;
                            if (_playing)
                                opening.Start();
                        }
                    }
                    catch (Exception)
                    {
                        // No device: the stream still plays, silently, on its own clock.
                        lock (_gate)
                        {
                            if (ReferenceEquals(_output, opening))
                                _output = null;
                        }
                    }
                });
                return;
            }
        }

        if (output is null)
            return;
        if (want)
            output.Start();
        else
            output.Stop();
    }

    /// <summary>The device's pull: every live, enabled audio track, resampled and mixed.</summary>
    public int Render(Span<float> destination, int channels)
    {
        destination.Clear();
        int frames = channels > 0 ? destination.Length / channels : 0;
        if (frames == 0 || !_playing)
            return frames;

        int outputRate = _format.SampleRate > 0 ? _format.SampleRate : Requested.SampleRate;
        foreach (var track in _source.Tracks)
        {
            if (!track.Live || !track.Enabled || track.Kind != MediaTrackKind.Audio || track.AudioPipe is not { } pipe)
                continue;
            MixTrack(pipe, destination, channels, frames, outputRate, 1f);
        }

        var mixed = destination[..(frames * channels)];
        Volatile.Read(ref _capture)?.Write(mixed, channels, outputRate);

        float volume = _volume;
        if (volume != 1f)
        {
            for (int i = 0; i < mixed.Length; i++)
                mixed[i] *= volume;
        }

        return frames;
    }

    private void MixTrack(AudioTrackPipe pipe, Span<float> destination, int channels, int frames, int outputRate, float volume)
    {
        double ratio = pipe.SampleRate / outputRate;
        long written = pipe.Written;
        if (!_readPositions.TryGetValue(pipe, out double position) ||
            written - position > ScratchFrames * 2)
        {
            // First read of this track, or the reader fell far behind: start at the live edge.
            position = Math.Max(0, written - LiveLatencyFrames);
        }

        int done = 0;
        while (done < frames)
        {
            int chunk = Math.Min(frames - done, (int)((ScratchFrames - 2) / Math.Max(ratio, 1e-6)));
            if (chunk <= 0)
                break;

            int need = (int)Math.Ceiling(chunk * ratio) + 2;

            // The device pulled faster than the producer writes (a clock running ahead, or
            // an output that is not real time): reading on would be silence from here on,
            // so fall back to just behind the live edge again.
            if (position + need > written && written >= need)
                position = Math.Max(0, written - Math.Max(need, LiveLatencyFrames));

            long start = (long)Math.Floor(position);
            pipe.Read(start, need, _scratch);
            double fraction = position - start;
            for (int i = 0; i < chunk; i++)
            {
                double x = fraction + i * ratio;
                int j = (int)x;
                float t = (float)(x - j);
                int frameBase = (done + i) * channels;
                for (int c = 0; c < channels; c++)
                {
                    // A mono track feeds every output channel; a wider one maps channel to
                    // channel and leaves the rest silent.
                    int sourceChannel = pipe.Channels == 1 ? 0 : c;
                    if (sourceChannel >= pipe.Channels)
                        continue;
                    var samples = _scratch[sourceChannel];
                    float sample = samples[j] + ((samples[j + 1] - samples[j]) * t);
                    destination[frameBase + c] += sample * volume;
                }
            }

            position += chunk * ratio;
            done += chunk;
        }

        _readPositions[pipe] = position;
    }
}
