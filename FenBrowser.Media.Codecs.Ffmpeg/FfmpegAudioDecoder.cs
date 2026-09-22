using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;
using static FenBrowser.Media.Codecs.Ffmpeg.FfmpegLibrary;

namespace FenBrowser.Media.Codecs.Ffmpeg;

/// <summary>
/// Registers one libavcodec-backed decoder per codec the build supports (Opus, Vorbis,
/// FLAC, MP3; VP8, VP9, AV1), when the library loads.
/// </summary>
public static class FfmpegDecoders
{
    /// <summary>The codecs this adapter offers and the libavcodec decoder names it asks for.</summary>
    private static readonly (MediaCodec Codec, string DecoderName)[] s_audio =
    [
        (MediaCodec.Opus, "opus"),
        (MediaCodec.Vorbis, "vorbis"),
        (MediaCodec.Flac, "flac"),
        (MediaCodec.Mp3, "mp3float"),
    ];

    /// <summary>
    /// Video codecs with the decoder names tried in order: libavcodec's own VP8/VP9, and
    /// for AV1 dav1d, then libaom (the native "av1" decoder needs hardware acceleration).
    /// H.264, AAC and HEVC are deliberately absent (ADR-0002).
    /// </summary>
    private static readonly (MediaCodec Codec, string[] DecoderNames)[] s_video =
    [
        (MediaCodec.Vp8, ["vp8", "libvpx"]),
        (MediaCodec.Vp9, ["vp9", "libvpx-vp9"]),
        (MediaCodec.Av1, ["libdav1d", "libaom-av1"]),
    ];

    /// <summary>
    /// Adds the decoders to <paramref name="decoders"/>. Returns false, with one log line
    /// saying why, when the library is missing or the wrong version (ADR-0001 failure
    /// behaviour: <c>canPlayType</c> then answers "" for these codecs).
    /// </summary>
    public static bool TryRegister(DecoderRegistry decoders, IMediaLogSink log)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        ArgumentNullException.ThrowIfNull(log);
        if (!FfmpegLibrary.TryLoad(out string reason))
        {
            log.Emit(PlayerId.None, MediaEventKind.DecoderAttempt, MediaLogLevel.Warn,
                $"libavcodec is not available; Opus, Vorbis, FLAC and MP3 will not play: {reason}",
                ("decoder", "libavcodec"), ("result", "unavailable"), ("reason", reason));
            return false;
        }

        int registered = 0;
        foreach (var (codec, name) in s_audio)
        {
            if (Native.avcodec_find_decoder_by_name(name) == IntPtr.Zero)
            {
                log.Emit(PlayerId.None, MediaEventKind.DecoderAttempt, MediaLogLevel.Warn,
                    $"libavcodec has no '{name}' decoder in this build.",
                    ("decoder", "libavcodec-" + name), ("result", "missing"));
                continue;
            }

            decoders.Register(new FfmpegAudioDecoderFactory(codec, name));
            registered++;
        }

        int video = 0;
        foreach (var (codec, names) in s_video)
        {
            string? found = names.FirstOrDefault(n => Native.avcodec_find_decoder_by_name(n) != IntPtr.Zero);
            if (found is null)
            {
                log.Emit(PlayerId.None, MediaEventKind.DecoderAttempt, MediaLogLevel.Warn,
                    $"libavcodec has none of '{string.Join("', '", names)}' in this build.",
                    ("decoder", "libavcodec-" + names[0]), ("result", "missing"));
                continue;
            }

            // The hardware form of the same decoder goes in first, ahead of the software
            // one (design section 8, M7); it falls back to software by itself when this
            // machine has no device for it.
            if (FfmpegHardwareVideoDecoderFactory.PlatformDevice != FfmpegHardwareDevice.None)
                decoders.Register(new FfmpegHardwareVideoDecoderFactory(codec, found));
            decoders.Register(new FfmpegVideoDecoderFactory(codec, found));
            video++;
        }

        log.Emit(PlayerId.None, MediaEventKind.DecoderChosen, MediaLogLevel.Info,
            $"libavcodec ready: {registered} audio and {video} video decoders ({FfmpegLibrary.Location}), video hardware {FfmpegHardwareVideoDecoderFactory.PlatformDevice}.",
            ("decoder", "libavcodec"), ("audio", registered.ToString(CultureInfo.InvariantCulture)),
            ("video", video.ToString(CultureInfo.InvariantCulture)),
            ("hardware", FfmpegHardwareVideoDecoderFactory.PlatformDevice.ToString()));
        return registered + video > 0;
    }
}

public sealed class FfmpegAudioDecoderFactory : IDecoderFactory<AudioBlock>
{
    private readonly MediaCodec _codec;
    private readonly string _decoderName;

    public FfmpegAudioDecoderFactory(MediaCodec codec, string decoderName)
    {
        _codec = codec;
        _decoderName = decoderName;
        Name = "libavcodec-" + decoderName;
    }

    public string Name { get; }

    public bool IsHardwareAccelerated => false;

    public int Priority => 10;

    public DecoderSupport Supports(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Kind == MediaTrackKind.Audio && config.Codec == _codec
            ? DecoderSupport.Supported
            : DecoderSupport.Unsupported;
    }

    public IMediaDecoder<AudioBlock> Create(MediaPipelineContext context) => new FfmpegAudioDecoder(_decoderName, context);
}

/// <summary>
/// One libavcodec audio decoding context. Packets are copied into libavcodec-owned
/// buffers (with the padding it requires), frames are copied out into pooled float
/// blocks, and no native pointer outlives the call that produced it (ADR-0001).
/// </summary>
public sealed class FfmpegAudioDecoder : IMediaDecoder<AudioBlock>
{
    // AVCodecParameters (libavcodec/codec_par.h, stable within a major).
    private const int ParCodecType = 0;
    private const int ParCodecId = 4;
    private const int ParExtradata = 16;
    private const int ParExtradataSize = 24;

    // AVCodec (libavcodec/codec.h): name, long_name, type, id.
    private const int CodecId = 20;

    // AVPacket (libavcodec/packet.h): buf, pts, dts, data, size.
    private const int PacketPts = 8;
    private const int PacketData = 24;
    private const int PacketSize = 32;

    // AVFrame (libavutil/frame.h): data[8], linesize[8], extended_data, width, height, nb_samples, format.
    private const int FrameExtendedData = 96;
    private const int FrameSamples = 112;
    private const int FrameFormat = 116;

    // AVChannelLayout: order, nb_channels, union, opaque.
    private const int LayoutChannels = 4;
    private const int LayoutSize = 24;

    private const int MediaTypeAudio = 1;
    private const int InputBufferPadding = 64;
    private const int ErrorAgain = -11;
    private const int ErrorEof = -541478725;

    private readonly string _decoderName;
    private readonly MediaPipelineContext _context;
    private IntPtr _context_;
    private IntPtr _packet;
    private IntPtr _frame;
    private CodecConfig? _config;
    private int _channels;
    private int _sampleRate;
    private long _nextSample;      // running output position at the codec rate
    private bool _positioned;
    private int _preSkip;          // Opus: samples libavcodec drops at the stream start
    private bool _atStreamStart;

    public FfmpegAudioDecoder(string decoderName, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _decoderName = decoderName;
        _context = context;
    }

    public string Name => "libavcodec-" + _decoderName;

    public ValueTask ConfigureAsync(CodecConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        Release();

        IntPtr codec = Native.avcodec_find_decoder_by_name(_decoderName);
        if (codec == IntPtr.Zero)
            throw new MediaDecoderException($"libavcodec has no '{_decoderName}' decoder.");

        _context_ = Native.avcodec_alloc_context3(codec);
        if (_context_ == IntPtr.Zero)
            throw new MediaDecoderException("avcodec_alloc_context3 failed.");

        IntPtr parameters = Native.avcodec_parameters_alloc();
        if (parameters == IntPtr.Zero)
            throw new MediaDecoderException("avcodec_parameters_alloc failed.");
        try
        {
            Marshal.WriteInt32(parameters, ParCodecType, MediaTypeAudio);
            Marshal.WriteInt32(parameters, ParCodecId, Marshal.ReadInt32(codec, CodecId));
            if (!config.Extradata.IsEmpty)
            {
                // Owned by the parameters from here; freed with them.
                IntPtr extradata = Native.av_mallocz((nuint)(config.Extradata.Length + InputBufferPadding));
                if (extradata == IntPtr.Zero)
                    throw new MediaDecoderException("av_mallocz failed for extradata.");
                Marshal.Copy(config.Extradata.ToArray(), 0, extradata, config.Extradata.Length);
                Marshal.WriteIntPtr(parameters, ParExtradata, extradata);
                Marshal.WriteInt32(parameters, ParExtradataSize, config.Extradata.Length);
            }

            Check(Native.avcodec_parameters_to_context(_context_, parameters), "avcodec_parameters_to_context");
        }
        finally
        {
            Native.avcodec_parameters_free(ref parameters);
        }

        if (config.Channels > 0)
            _ = Native.av_opt_set(_context_, "ch_layout", config.Channels == 1 ? "mono" : config.Channels == 2 ? "stereo" : config.Channels.ToString(CultureInfo.InvariantCulture) + "C", 0);
        if (config.SampleRate > 0)
            _ = Native.av_opt_set(_context_, "ar", config.SampleRate.ToString(CultureInfo.InvariantCulture), 0);

        Check(Native.avcodec_open2(_context_, codec, IntPtr.Zero), "avcodec_open2");

        _packet = Native.av_packet_alloc();
        _frame = Native.av_frame_alloc();
        if (_packet == IntPtr.Zero || _frame == IntPtr.Zero)
            throw new MediaDecoderException("av_packet_alloc/av_frame_alloc failed.");

        _sampleRate = ReadInt("ar", config.SampleRate);
        _channels = ReadChannels(config.Channels);
        _context.Limits.CheckAudioFormat(_sampleRate, _channels);
        _preSkip = config.Codec == MediaCodec.Opus && config.Extradata.Length >= 12
            ? BinaryPrimitives.ReadUInt16LittleEndian(config.Extradata.Span[10..])
            : 0;
        _atStreamStart = true;
        _config = config;
        _positioned = false;
        _nextSample = 0;
        return ValueTask.CompletedTask;
    }

    private int ReadInt(string option, int fallback)
    {
        return Native.av_opt_get_int(_context_, option, 0, out long value) >= 0 && value > 0 && value <= int.MaxValue ? (int)value : fallback;
    }

    private int ReadChannels(int fallback)
    {
        IntPtr layout = Marshal.AllocHGlobal(LayoutSize);
        try
        {
            for (int i = 0; i < LayoutSize; i += 8)
                Marshal.WriteInt64(layout, i, 0);
            if (Native.av_opt_get_chlayout(_context_, "ch_layout", 0, layout) < 0)
                return fallback;
            int channels = Marshal.ReadInt32(layout, LayoutChannels);
            Native.av_channel_layout_uninit(layout);
            return channels > 0 ? channels : fallback;
        }
        finally
        {
            Marshal.FreeHGlobal(layout);
        }
    }

    public ValueTask DecodeAsync(EncodedPacket packet, IDecodeOutput<AudioBlock> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConfigured();

        // A stamped packet that disagrees with the running count re-anchors the output
        // (a seek); an unstamped one, or one that agrees, continues it. At the very start
        // of an Opus stream libavcodec drops the pre-skip samples itself, so the first
        // output begins that much after the first packet's stamp.
        if (packet.HasPts)
        {
            long stamped = packet.Pts.ToTimescale(_sampleRate);
            if (_atStreamStart)
                stamped += _preSkip;
            if (!_positioned || Math.Abs(stamped - _nextSample) > _sampleRate / 10)
                _nextSample = stamped;
            _positioned = true;
        }

        _atStreamStart = false;

        Check(Native.av_new_packet(_packet, packet.Length), "av_new_packet");
        try
        {
            IntPtr data = Marshal.ReadIntPtr(_packet, PacketData);
            Marshal.Copy(packet.Span.ToArray(), 0, data, packet.Length);
            // No time base is set on the context, so timestamps stay AV_NOPTS_VALUE; the
            // adapter keeps time itself.
            Marshal.WriteInt64(_packet, PacketPts, long.MinValue);
            int sent = Native.avcodec_send_packet(_context_, _packet);
            if (sent < 0 && sent != ErrorAgain)
            {
                // A bad packet is dropped, not fatal: the stream resyncs on the next one.
                _context.Log.Emit(_context.Player, MediaEventKind.DecoderAttempt, MediaLogLevel.Debug,
                    $"{Name} rejected a packet: {Describe(sent)}", ("decoder", Name), ("result", "packet-rejected"));
                return ValueTask.CompletedTask;
            }
        }
        finally
        {
            Native.av_packet_unref(_packet);
        }

        ReceiveAll(output);
        return ValueTask.CompletedTask;
    }

    public ValueTask DrainAsync(IDecodeOutput<AudioBlock> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConfigured();
        int sent = Native.avcodec_send_packet(_context_, IntPtr.Zero);
        if (sent >= 0 || sent == ErrorEof)
            ReceiveAll(output);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context_ != IntPtr.Zero)
            Native.avcodec_flush_buffers(_context_);
        _positioned = false;
        return ValueTask.CompletedTask;
    }

    private void ReceiveAll(IDecodeOutput<AudioBlock> output)
    {
        while (true)
        {
            int got = Native.avcodec_receive_frame(_context_, _frame);
            if (got == ErrorAgain || got == ErrorEof)
                return;
            if (got < 0)
                throw new MediaDecoderException($"{Name} failed to decode: {Describe(got)}");

            try
            {
                EmitFrame(output);
            }
            finally
            {
                Native.av_frame_unref(_frame);
            }
        }
    }

    private void EmitFrame(IDecodeOutput<AudioBlock> output)
    {
        int samples = Marshal.ReadInt32(_frame, FrameSamples);
        int format = Marshal.ReadInt32(_frame, FrameFormat);
        if (samples <= 0)
            return;

        long start = _nextSample;
        _nextSample += samples;

        var block = AudioBlock.Allocate(_context.Limits, _sampleRate, _channels, samples, MediaTime.FromTimescale(start, _sampleRate));
        try
        {
            IntPtr extended = Marshal.ReadIntPtr(_frame, FrameExtendedData);
            Convert(extended, format, samples, 0, samples, block.Samples);
        }
        catch
        {
            block.Dispose();
            throw;
        }

        output.Emit(block);
    }

    /// <summary>libavutil sample formats (samplefmt.h) to interleaved floats; planar formats read one plane per channel.</summary>
    private void Convert(IntPtr extendedData, int format, int frameSamples, int skip, int keep, Span<float> destination)
    {
        int channels = _channels;
        bool planar = (format >= 5 && format <= 9) || format == 11;
        int bytesPerSample = format switch
        {
            0 or 5 => 1,
            1 or 6 => 2,
            2 or 7 or 3 or 8 => 4,
            4 or 9 or 10 or 11 => 8,
            _ => throw new MediaDecoderException($"Unsupported libavcodec sample format {format}."),
        };

        int planes = planar ? channels : 1;
        int planeBytes = (planar ? frameSamples : frameSamples * channels) * bytesPerSample;
        byte[] plane = MediaBufferPool.Bytes.Rent(planeBytes);
        try
        {
            var span = plane.AsSpan(0, planeBytes);
            for (int p = 0; p < planes; p++)
            {
                IntPtr source = Marshal.ReadIntPtr(extendedData, p * IntPtr.Size);
                if (source == IntPtr.Zero)
                    throw new MediaDecoderException("libavcodec returned a frame without data.");
                Marshal.Copy(source, plane, 0, planeBytes);
                if (planar)
                {
                    for (int i = 0; i < keep; i++)
                        destination[i * channels + p] = Sample(span, (skip + i) * bytesPerSample, format);
                }
                else
                {
                    for (int i = 0; i < keep * channels; i++)
                        destination[i] = Sample(span, (skip * channels + i) * bytesPerSample, format);
                }
            }
        }
        finally
        {
            MediaBufferPool.Return(plane);
        }
    }

    private static float Sample(ReadOnlySpan<byte> bytes, int at, int format) => format switch
    {
        0 or 5 => (bytes[at] - 128) / 128f,
        1 or 6 => BinaryPrimitives.ReadInt16LittleEndian(bytes[at..]) / 32768f,
        2 or 7 => BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]) / 2147483648f,
        3 or 8 => Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]), -1f, 1f),
        4 or 9 => (float)Math.Clamp(BinaryPrimitives.ReadDoubleLittleEndian(bytes[at..]), -1.0, 1.0),
        _ => (float)(BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]) / 9223372036854775808.0),
    };

    private void EnsureConfigured()
    {
        if (_config is null || _context_ == IntPtr.Zero)
            throw new InvalidOperationException("ConfigureAsync has not run.");
    }

    private static void Check(int result, string call)
    {
        if (result < 0)
            throw new MediaDecoderException($"{call} failed: {Describe(result)}");
    }

    private static string Describe(int error)
    {
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.WriteByte(buffer, 0, 0);
            _ = Native.av_strerror(error, buffer, 256);
            string text = Marshal.PtrToStringAnsi(buffer) ?? string.Empty;
            return string.IsNullOrEmpty(text) ? error.ToString(CultureInfo.InvariantCulture) : $"{text} ({error.ToString(CultureInfo.InvariantCulture)})";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void Release()
    {
        if (_frame != IntPtr.Zero)
            Native.av_frame_free(ref _frame);
        if (_packet != IntPtr.Zero)
            Native.av_packet_free(ref _packet);
        if (_context_ != IntPtr.Zero)
            Native.avcodec_free_context(ref _context_);
        _config = null;
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }
}
