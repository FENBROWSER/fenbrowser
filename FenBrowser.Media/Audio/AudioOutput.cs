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

/// <summary>
/// One audio output endpoint the system offers. <c>DeviceId</c> is what a page passes to
/// <c>setSinkId</c>: the platform's own identifier for the endpoint, stable across runs on
/// the same machine. <c>Label</c> is the name a person would recognise, and is never handed
/// to a page that has not been given permission to see it.
/// </summary>
public readonly record struct AudioOutputDevice(string DeviceId, string Label, bool IsDefault);

/// <summary>Creates an output stream per player; the host supplies the platform backend.</summary>
public interface IAudioOutputFactory
{
    /// <summary>A stream on whatever the system calls the default output right now.</summary>
    IAudioOutput Create();

    /// <summary>
    /// The output endpoints this backend can open. Empty when the backend has no notion of
    /// separate devices (the null sink) or cannot ask the system for them.
    /// </summary>
    IReadOnlyList<AudioOutputDevice> Devices => [];

    /// <summary>
    /// A stream on one named endpoint, or null when nothing has that identifier - which is
    /// what makes <c>setSinkId</c> reject with <c>NotFoundError</c>. The empty identifier
    /// means the default output and never answers null.
    /// </summary>
    IAudioOutput? Create(string deviceId) => string.IsNullOrEmpty(deviceId) ? Create() : null;
}
