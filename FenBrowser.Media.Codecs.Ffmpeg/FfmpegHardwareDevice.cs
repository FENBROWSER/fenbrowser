using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Codecs.Ffmpeg;

/// <summary>
/// How many pictures libavcodec decoded on the GPU and brought back to system memory
/// (design section 5's zero-copy accounting). Readable from any thread.
/// </summary>
public static class FfmpegHardwareCounters
{
    private static long s_pictures;
    private static long s_bytes;

    /// <summary>Pictures a hardware decoder produced and transferred out of GPU memory.</summary>
    public static long TransferredPictures => Interlocked.Read(ref s_pictures);

    /// <summary>How many bytes those transfers moved.</summary>
    public static long BytesTransferred => Interlocked.Read(ref s_bytes);

    internal static void Transferred(long bytes)
    {
        Interlocked.Increment(ref s_pictures);
        Interlocked.Add(ref s_bytes, bytes);
    }
}

/// <summary>
/// The libavutil <c>AVHWDeviceType</c> values this engine uses: the platform video API a
/// decoder runs on (MEDIA_ENGINE_DESIGN section 8, M7). The numbers are libavutil's own
/// and are part of its ABI, like every other offset and constant in this adapter.
/// </summary>
public enum FfmpegHardwareDevice
{
    /// <summary>Decode in software.</summary>
    None = 0,

    /// <summary>VA-API: the Linux video acceleration API, over DRM.</summary>
    VaApi = 3,

    /// <summary>VideoToolbox: the macOS and iOS video acceleration API.</summary>
    VideoToolbox = 6,

    /// <summary>Direct3D 11 Video: the Windows one, on the same device Media Foundation uses.</summary>
    D3D11Va = 7,
}

/// <summary>
/// A libavcodec decoder that runs on the platform's video hardware: Direct3D 11 Video on
/// Windows, VA-API on Linux, VideoToolbox on macOS (ADR-0002 keeps the patent-encumbered
/// codecs on the OS decoders; this is the same idea for the free ones). It is offered
/// ahead of the software decoder for the same codec; when the device or the decoder's
/// hardware configuration is missing, the decoder falls back to software by itself, so
/// selection never fails over to a slower answer than before.
/// </summary>
public sealed class FfmpegHardwareVideoDecoderFactory : IDecoderFactory<VideoFrame>
{
    private readonly MediaCodec _codec;
    private readonly string _decoderName;

    public FfmpegHardwareVideoDecoderFactory(MediaCodec codec, string decoderName)
    {
        _codec = codec;
        _decoderName = decoderName;
        Name = "libavcodec-hw-" + decoderName;
    }

    /// <summary>The device type for the platform this build is running on, or None where there is none.</summary>
    public static FfmpegHardwareDevice PlatformDevice
    {
        get
        {
            if (IsDisabled)
                return FfmpegHardwareDevice.None;
            if (OperatingSystem.IsWindows())
                return FfmpegHardwareDevice.D3D11Va;
            if (OperatingSystem.IsMacOS())
                return FfmpegHardwareDevice.VideoToolbox;
            if (OperatingSystem.IsLinux())
                return FfmpegHardwareDevice.VaApi;
            return FfmpegHardwareDevice.None;
        }
    }

    /// <summary><c>FEN_MEDIA_HW_DECODE=off</c> keeps every libavcodec decoder in software.</summary>
    private static bool IsDisabled
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("FEN_MEDIA_HW_DECODE");
            return value is not null && (value.Equals("off", StringComparison.OrdinalIgnoreCase)
                || value.Equals("0", StringComparison.Ordinal)
                || value.Equals("false", StringComparison.OrdinalIgnoreCase));
        }
    }

    public string Name { get; }

    public bool IsHardwareAccelerated => true;

    /// <summary>Ahead of the software decoder for the same codec, behind an OS decoder.</summary>
    public int Priority => 8;

    public DecoderSupport Supports(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (PlatformDevice == FfmpegHardwareDevice.None || config.Kind != MediaTrackKind.Video || config.Codec != _codec)
            return DecoderSupport.Unsupported;

        // Whether this machine's driver decodes this stream is only known once the device
        // is open and the decoder has seen the stream, so the honest answer is "maybe";
        // the decoder itself falls back to software when the answer turns out to be no.
        return DecoderSupport.Maybe;
    }

    public IMediaDecoder<VideoFrame> Create(MediaPipelineContext context) =>
        new FfmpegVideoDecoder(_decoderName, context, PlatformDevice);
}
