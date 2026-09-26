namespace FenBrowser.Media.Audio;

/// <summary>
/// Receives a copy of what an <see cref="AudioRenderer"/> hands the device, taken before
/// the element's volume and muting. Called on the device thread, once per render call.
/// </summary>
public interface IAudioCapture
{
    /// <summary>
    /// <paramref name="interleaved"/> holds whole frames of <paramref name="channels"/>
    /// samples at <paramref name="sampleRate"/>; silence where the renderer had nothing.
    /// </summary>
    void Write(ReadOnlySpan<float> interleaved, int channels, int sampleRate);
}
