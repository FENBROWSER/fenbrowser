namespace FenBrowser.Media.WebAudio;

/// <summary>
/// The bounds the Web Audio engine enforces, whatever the realm already validated (design
/// section 3): a native call with a bad argument must not be able to allocate without limit.
/// </summary>
public static class WebAudioLimits
{
    /// <summary>WA 2.4: the render quantum.</summary>
    public const int RenderQuantumFrames = 128;

    /// <summary>WA 1.1: at least 32 channels must be supported; this engine supports exactly 32.</summary>
    public const int MaxChannels = 32;

    /// <summary>WA 1.1: the nominal sample-rate range.</summary>
    public const float MinSampleRate = 3000f;

    public const float MaxSampleRate = 768000f;

    /// <summary>The most inputs or outputs a splitter, merger or worklet node may have.</summary>
    public const int MaxNodePorts = 32;

    /// <summary>
    /// The most samples (frames times channels) one buffer the engine copies may hold:
    /// 2^27 floats, half a gigabyte. That is over 20 minutes of stereo at 48 kHz.
    /// </summary>
    public const long MaxBufferSamples = 1L << 27;

    public static bool IsValidSampleRate(float sampleRate) =>
        sampleRate >= MinSampleRate && sampleRate <= MaxSampleRate;
}
