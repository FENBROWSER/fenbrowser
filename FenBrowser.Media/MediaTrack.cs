namespace FenBrowser.Media;

public enum MediaTrackKind
{
    Audio,
    Video,
    Text,
}

/// <summary>
/// Codecs the engine can name. A name here does not mean a decoder exists; the
/// decoder registry decides that at runtime (ADR-0001, ADR-0002).
/// </summary>
public enum MediaCodec
{
    Unknown,

    // Video
    Vp8,
    Vp9,
    Av1,
    H264,
    Hevc,

    // Audio
    Opus,
    Vorbis,
    Flac,
    Mp3,
    Aac,
    Pcm,

    // Text
    WebVtt,
}

public enum PcmSampleFormat
{
    None,
    U8,
    S16,
    S24,
    S32,
    F32,
    F64,
}

/// <summary>
/// Everything a decoder needs to start decoding a track.
/// </summary>
/// <param name="CodecString">The RFC 6381 <c>codecs</c> value when known (for example <c>vp09.00.10.08</c>).</param>
/// <param name="Extradata">Codec-private setup data (for example the Matroska CodecPrivate or an MP4 <c>avcC</c> box).</param>
public sealed record CodecConfig(
    MediaTrackKind Kind,
    MediaCodec Codec,
    string? CodecString = null,
    int Width = 0,
    int Height = 0,
    int SampleRate = 0,
    int Channels = 0,
    PcmSampleFormat PcmFormat = PcmSampleFormat.None,
    ReadOnlyMemory<byte> Extradata = default)
{
    public static CodecConfig Video(MediaCodec codec, int width, int height, string? codecString = null) =>
        new(MediaTrackKind.Video, codec, codecString, Width: width, Height: height);

    public static CodecConfig Audio(MediaCodec codec, int sampleRate, int channels, string? codecString = null) =>
        new(MediaTrackKind.Audio, codec, codecString, SampleRate: sampleRate, Channels: channels);
}

/// <summary>
/// One track as reported by a demuxer.
/// </summary>
/// <param name="Id">Demuxer-assigned identifier, unique within one resource.</param>
/// <param name="Duration">Track duration, or <see cref="MediaTime.PositiveInfinity"/> when unbounded or unknown.</param>
public sealed record MediaTrackInfo(
    int Id,
    CodecConfig Config,
    MediaTime Duration,
    string Language = "",
    string Label = "",
    bool IsDefault = false)
{
    public MediaTrackKind Kind => Config.Kind;
}
