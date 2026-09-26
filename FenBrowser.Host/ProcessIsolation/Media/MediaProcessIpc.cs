using System;
using FenBrowser.Host.ProcessIsolation.Targets;

namespace FenBrowser.Host.ProcessIsolation.Media
{
    /// <summary>
    /// Canonical startup contract for the media target process (MEDIA_ENGINE_DESIGN §2.2,
    /// ADR-0004). The media target uses the generic target IPC transport for control
    /// messages; resource bytes go in and decoded PCM comes out through shared memory,
    /// never through the JSON pipe.
    /// </summary>
    internal static class MediaProcessIpc
    {
        public static TargetProcessContract Contract { get; } = new(
            TargetProcessKind.Media,
            profileName: "media_process",
            capabilitySet: "media-decode,shared-memory",
            launchArgument: "--media-child",
            readyTimeoutEnvKey: "FEN_MEDIA_READY_TIMEOUT_MS",
            allowUnsandboxedEnvKey: "FEN_MEDIA_ALLOW_UNSANDBOXED",
            childEnvironmentFlag: "FEN_MEDIA_CHILD");

        /// <summary>Set <c>FEN_MEDIA_PROCESS=in-process</c> to decode in the renderer for debugging.</summary>
        public static bool IsMediaProcessEnabled()
        {
            var mode = (Environment.GetEnvironmentVariable("FEN_MEDIA_PROCESS") ?? string.Empty).Trim();
            return !(string.Equals(mode, "in-process", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(mode, "0", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Bounds the child enforces on every media envelope before it touches memory.</summary>
    internal static class MediaIpcLimits
    {
        public const int MaxSessionsPerProcess = 64;
        public const int MaxRegionNameChars = 128;
        public const int MaxDeclaredMimeChars = 256;
        public const int MaxErrorMessageChars = 1024;

        /// <summary>Resource bytes live in one shared region, so a resource is at most one region.</summary>
        public const long MaxInputLength = int.MaxValue;

        /// <summary>A decoder session's input region holds one coded frame (design §4: a packet is at most 16 MB).</summary>
        public const int MaxPacketRegionCapacity = 16 * 1024 * 1024;
        public const int MaxExtradataChars = 64 * 1024;

        /// <summary>The PCM region: one read moves at most this many bytes; a longer block is split.</summary>
        public const int MinOutputCapacity = 4096;
        public const int MaxOutputCapacity = 16 * 1024 * 1024;
        public const int DefaultOutputCapacity = 2 * 1024 * 1024;

        /// <summary>
        /// The picture region: one read moves one decoded picture, planes end to end in the
        /// <see cref="FenBrowser.Media.Buffers.VideoFrame"/> layout. 8K 4:2:0 needs about 50 MB.
        /// </summary>
        public const int MaxVideoCapacity = 64 * 1024 * 1024;
        public const int VideoCapacityGranularity = 64 * 1024;
    }

    public sealed class MediaOpenPayload
    {
        public string SessionId { get; set; }
        public string InputRegion { get; set; }
        public long InputLength { get; set; }
        public string OutputRegion { get; set; }
        public int OutputCapacity { get; set; }
        public string DeclaredMime { get; set; }
    }

    public sealed class MediaTrackData
    {
        public int Id { get; set; }
        public int Kind { get; set; }
        public int Codec { get; set; }
        public string CodecString { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long DurationUs { get; set; }
        public string Language { get; set; }
        public string Label { get; set; }
        public bool IsDefault { get; set; }
    }

    public sealed class MediaOpenResponsePayload
    {
        public bool Success { get; set; }
        public string ErrorKind { get; set; }
        public string ErrorMessage { get; set; }
        public long DurationUs { get; set; }
        public bool IsSeekable { get; set; }
        public MediaTrackData[] Tracks { get; set; }

        /// <summary>The decoded audio track, or -1 when the resource has none the child can decode.</summary>
        public int AudioTrackId { get; set; } = -1;

        /// <summary>The decoded video track, or -1 when there is none.</summary>
        public int VideoTrackId { get; set; } = -1;
    }

    public sealed class MediaReadPayload
    {
        public string SessionId { get; set; }

        /// <summary>
        /// The region pictures go into, created by the renderer once it knows the track's
        /// size (and again, larger, when a picture does not fit). Null for audio-only sessions.
        /// </summary>
        public string VideoRegion { get; set; }
        public int VideoCapacity { get; set; }
    }

    /// <summary>What one read brought back: <see cref="Kind"/> 0 is a block of audio, 1 a picture.</summary>
    public sealed class MediaReadResponsePayload
    {
        public bool Success { get; set; }
        public string ErrorKind { get; set; }
        public string ErrorMessage { get; set; }
        public bool EndOfStream { get; set; }
        public int Kind { get; set; }
        public long TimestampUs { get; set; }

        // Audio.
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int FrameCount { get; set; }

        // Video: the picture is in the video region in the VideoFrame layout for these values.
        public int Width { get; set; }
        public int Height { get; set; }
        public int PixelFormat { get; set; }
        public long DurationUs { get; set; }

        /// <summary>The picture is held in the child: the renderer must attach a region of at least <see cref="RequiredBytes"/> and read again.</summary>
        public bool RegionTooSmall { get; set; }
        public int RequiredBytes { get; set; }
    }

    public sealed class MediaSeekPayload
    {
        public string SessionId { get; set; }
        public long TargetUs { get; set; }
    }

    public sealed class MediaSeekResponsePayload
    {
        public bool Success { get; set; }
        public string ErrorKind { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// The background policy of design section 5: the renderer tells the child to stop or
    /// resume decoding the session's video track while the element is out of sight.
    /// </summary>
    public sealed class MediaVideoDecodePayload
    {
        public string SessionId { get; set; }
        public bool Enabled { get; set; }
    }

    public sealed class MediaVideoDecodeResponsePayload
    {
        public bool Success { get; set; }
        public string ErrorKind { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// A decoder session (MSE, design §2.2): the renderer keeps the SourceBuffers and hands
    /// coded frames over one at a time; the child holds the decoder. The input region
    /// carries one packet; decoded audio and pictures come back through the read path in
    /// the output and video regions, as for a resource session.
    /// </summary>
    public sealed class MediaDecoderOpenPayload
    {
        public string SessionId { get; set; }
        public string InputRegion { get; set; }
        public int InputCapacity { get; set; }
        public string OutputRegion { get; set; }
        public int OutputCapacity { get; set; }
        public int Kind { get; set; }
        public int Codec { get; set; }
        public string CodecString { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string ExtradataBase64 { get; set; }
    }

    public sealed class MediaDecoderOpenResponsePayload
    {
        public bool Success { get; set; }
        public string ErrorKind { get; set; }
        public string ErrorMessage { get; set; }
        public string DecoderName { get; set; }
    }

    /// <summary>One coded frame in the session's input region, or a drain (<see cref="Drain"/>) or a flush (<see cref="Reset"/>) with no bytes.</summary>
    public sealed class MediaDecoderPushPayload
    {
        public string SessionId { get; set; }
        public int Length { get; set; }
        public bool HasPts { get; set; }
        public long PtsUs { get; set; }
        public long DtsUs { get; set; }
        public long DurationUs { get; set; }
        public bool IsKeyframe { get; set; }
        public bool Drain { get; set; }
        public bool Reset { get; set; }
    }

    /// <summary>How many decoded items now wait to be read from the session.</summary>
    public sealed class MediaDecoderPushResponsePayload
    {
        public bool Success { get; set; }
        public string ErrorKind { get; set; }
        public string ErrorMessage { get; set; }
        public int Outputs { get; set; }
    }

    public sealed class MediaClosePayload
    {
        public string SessionId { get; set; }
    }

    /// <summary>How a failure crosses the pipe; the host turns it back into the matching exception.</summary>
    internal static class MediaErrorKinds
    {
        public const string Unsupported = "unsupported";
        public const string Format = "format";
        public const string Decoder = "decoder";
        public const string Limit = "limit";
        public const string Protocol = "protocol";
        public const string Internal = "internal";
    }
}
