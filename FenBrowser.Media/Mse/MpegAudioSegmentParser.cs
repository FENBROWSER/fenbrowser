using FenBrowser.Media.Types;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Containers.Adts;
using FenBrowser.Media.Containers.Mp3;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Mse;

/// <summary>
/// The MPEG audio byte stream formats (MSE byte stream format registry: "MPEG Audio Byte
/// Stream Format" for <c>audio/mpeg</c>, "MPEG-2 Audio Data Transport Stream" for
/// <c>audio/aac</c>). Neither carries an initialization segment: the first frame header
/// implies one (the track's sample rate and channels), every frame is a media segment, and
/// the frames carry no timestamps, so the SourceBuffer generates them (§3.5.11 step 21).
/// A header that changes the sample rate or channel count is a new initialization segment.
/// </summary>
public sealed class MpegAudioSegmentParser : SegmentParser
{
    private readonly bool _adts;
    private byte[]? _initialization;

    public MpegAudioSegmentParser(MediaPipelineContext context, bool adts)
        : base(context)
    {
        _adts = adts;
    }

    /// <summary>Byte streams without timestamps: the SourceBuffer generates them and runs in sequence mode.</summary>
    public static bool GeneratesTimestamps(string mimeType) =>
        MimeType.Parse(mimeType)?.Essence is "audio/mpeg" or "audio/mp3" or "audio/aac";

    protected override bool PendingIsMediaSegment(List<byte> pending) => InitializationSegment is not null;

    protected override SegmentParseStatus Parse(byte[] data, out int consumed, Action<DemuxerInfo> onInitializationSegment, Action<EncodedPacket> onPacket)
    {
        consumed = 0;
        int position = 0;
        // An ID3v2 tag may lead an MP3 stream (the registry allows it); frames follow.
        if (!_adts && InitializationSegment is null && data.Length >= 10 && data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
        {
            int tagLength = 10 + ((data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F));
            if (data.Length < tagLength)
                return SegmentParseStatus.Ok;
            position = tagLength;
            consumed = position;
        }

        while (position < data.Length)
        {
            if (!TryFrame(data.AsSpan(position), out int frameLength, out int samples, out int sampleRate, out int channels, out var header))
            {
                if (data.Length - position < 10)
                    break; // a header may be split across appends
                throw new MediaFormatException(_adts ? "The bytes are not ADTS frames." : "The bytes are not MPEG audio frames.");
            }

            if (position + frameLength > data.Length)
                break; // the frame is not complete yet

            var frame = data.AsSpan(position, frameLength);
            if (_initialization is null || !SameStream(_initialization, header))
            {
                // The implied initialization segment: this frame's header, run through the
                // demuxer for the track it describes.
                _initialization = header;
                InitializationSegment = header;
                var info = ReadInitialization(_adts ? AdtsDemuxerFactory.Instance : Mp3DemuxerFactory.Instance, frame.ToArray());
                onInitializationSegment(info);
            }

            var packet = EncodedPacket.Rent(Context.Limits, MediaTrackKind.Audio, 0, frameLength, MediaTime.Zero, MediaTime.Zero, MediaTime.FromTimescale(samples, sampleRate), true);
            frame.CopyTo(packet.Memory.Span);
            onPacket(packet);
            position += frameLength;
            consumed = position;
        }

        return SegmentParseStatus.Ok;
    }

    private bool TryFrame(ReadOnlySpan<byte> bytes, out int frameLength, out int samples, out int sampleRate, out int channels, out byte[] header)
    {
        if (_adts)
        {
            if (AdtsFrameHeader.TryParse(bytes, out var adts) && adts.FrameLength > 0)
            {
                frameLength = adts.FrameLength;
                samples = adts.Samples;
                sampleRate = adts.SampleRate;
                channels = adts.Channels;
                header = bytes[..Math.Min(bytes.Length, AdtsFrameHeader.MaxHeaderLength)].ToArray();
                return true;
            }
        }
        else if (MpegAudioFrameHeader.TryParse(bytes, out var mpeg) && mpeg.FrameLength > 0)
        {
            frameLength = mpeg.FrameLength;
            samples = mpeg.SamplesPerFrame;
            sampleRate = mpeg.SampleRate;
            channels = mpeg.Channels;
            header = bytes[..Math.Min(bytes.Length, 4)].ToArray();
            return true;
        }

        frameLength = samples = sampleRate = channels = 0;
        header = [];
        return false;
    }

    /// <summary>The same track: sample rate and channels unchanged (bitrate and padding may vary frame to frame).</summary>
    private bool SameStream(byte[] previous, byte[] header)
    {
        if (_adts)
        {
            return AdtsFrameHeader.TryParse(previous, out var a) && AdtsFrameHeader.TryParse(header, out var b) && a.IsCompatibleWith(b);
        }

        return MpegAudioFrameHeader.TryParse(previous, out var p) && MpegAudioFrameHeader.TryParse(header, out var q)
            && p.SampleRate == q.SampleRate && p.Channels == q.Channels && p.Layer == q.Layer && p.Version == q.Version;
    }
}
