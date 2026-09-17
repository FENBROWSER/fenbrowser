using FenBrowser.Media.Clock;

namespace FenBrowser.Media.Audio;

/// <summary>Interleaved float PCM at a sample rate and channel count.</summary>
public readonly record struct AudioStreamFormat(int SampleRate, int Channels);

/// <summary>
/// The pull side of an audio device: the output calls this on its own thread whenever it
/// needs more samples. The call must not allocate or block (design §5: zero allocations
/// in the audio callback).
/// </summary>
public interface IAudioRenderCallback
{
    /// <summary>
    /// Fills <paramref name="destination"/> (interleaved, <paramref name="channels"/> per
    /// frame) and returns the number of frames written. Frames not written are silence and
    /// count as an underrun at the output.
    /// </summary>
    int Render(Span<float> destination, int channels);
}

/// <summary>
/// An audio device stream (ADR-0003): WASAPI, PulseAudio, CoreAudio, or the null sink used
/// by tests and <c>fenplay</c>. One stream plays one format; the backend chooses the format
/// it opens with, which need not be the requested one.
/// </summary>
public interface IAudioOutput : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Opens the stream. The returned format is what <see cref="IAudioRenderCallback.Render"/> must produce.</summary>
    ValueTask<AudioStreamFormat> OpenAsync(AudioStreamFormat requested, IAudioRenderCallback callback, CancellationToken cancellationToken);

    /// <summary>Where the listener is, in output frames; the master clock reads this.</summary>
    IAudioPlaybackPosition Position { get; }

    /// <summary>Number of times the callback delivered fewer frames than the device needed.</summary>
    long Underruns { get; }

    /// <summary>Starts pulling samples (the device plays).</summary>
    void Start();

    /// <summary>Stops pulling samples; the position holds and no silence is inserted.</summary>
    void Stop();
}

/// <summary>Creates an output stream per player; the host supplies the platform backend.</summary>
public interface IAudioOutputFactory
{
    IAudioOutput Create();
}
