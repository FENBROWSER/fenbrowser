using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;
using static FenBrowser.Media.Codecs.Ffmpeg.FfmpegLibrary;

namespace FenBrowser.Media.Codecs.Ffmpeg;

public sealed class FfmpegVideoDecoderFactory : IDecoderFactory<VideoFrame>
{
    private readonly MediaCodec _codec;
    private readonly string _decoderName;

    public FfmpegVideoDecoderFactory(MediaCodec codec, string decoderName)
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
        if (config.Kind != MediaTrackKind.Video || config.Codec != _codec)
            return DecoderSupport.Unsupported;
        // Profiles above 8-bit 4:2:0 decode but are not yet converted for display (see
        // FfmpegVideoDecoder.EmitFrame), so the answer for them is only "maybe".
        return config.CodecString is { } s && (s.StartsWith("vp09.", StringComparison.Ordinal) && !s.StartsWith("vp09.00.", StringComparison.Ordinal)
            || s.StartsWith("av01.", StringComparison.Ordinal) && !s.Contains(".08", StringComparison.Ordinal))
            ? DecoderSupport.Maybe
            : DecoderSupport.Supported;
    }

    public IMediaDecoder<VideoFrame> Create(MediaPipelineContext context) => new FfmpegVideoDecoder(_decoderName, context);
}

/// <summary>
/// One libavcodec video decoding context (ADR-0001). Packets are copied into
/// libavcodec-owned buffers, decoded pictures are copied row by row into pooled
/// <see cref="VideoFrame"/>s, and no native pointer outlives the call that produced it.
/// Timestamps ride through libavcodec on the packet's <c>pts</c> (in microseconds) so
/// every output frame carries the stamp of the packet that showed it, whatever the
/// codec's internal frame order.
/// </summary>
public sealed class FfmpegVideoDecoder : IMediaDecoder<VideoFrame>
{
    // AVCodecParameters (libavcodec/codec_par.h, stable within a major).
    private const int ParCodecType = 0;
    private const int ParCodecId = 4;
    private const int ParExtradata = 16;
    private const int ParExtradataSize = 24;
    private const int ParWidth = 64;
    private const int ParHeight = 68;

    // AVCodec (libavcodec/codec.h): name, long_name, type, id.
    private const int CodecId = 20;

    // AVPacket (libavcodec/packet.h): buf, pts, dts, data, size.
    private const int PacketPts = 8;
    private const int PacketDts = 16;
    private const int PacketData = 24;

    // AVFrame (libavutil/frame.h): data[8], linesize[8], extended_data, width, height,
    // nb_samples, format, pict_type, sample_aspect_ratio, pts.
    private const int FrameData = 0;
    private const int FrameLinesize = 64;
    private const int FrameWidth = 104;
    private const int FrameHeight = 108;
    private const int FrameFormat = 116;
    private const int FramePts = 136;
    private const int FrameHwFramesCtx = 328;

    // AVCodecContext.hw_device_ctx, and AVCodecHWConfig (pix_fmt, methods, device_type).
    // Read from the libavcodec 63 / libavutil 61 headers this build pins.
    private const int ContextHwDeviceCtx = 560;
    private const int HwConfigMethods = 4;
    private const int HwConfigDeviceType = 8;
    private const int HwConfigMethodDeviceCtx = 1;

    private const int MediaTypeVideo = 0;
    private const int InputBufferPadding = 64;
    private const int ErrorAgain = -11;
    private const int ErrorEof = -541478725;
    private const long NoPts = long.MinValue;
    private const int MaxDecodeThreads = 8;

    private readonly string _decoderName;
    private readonly MediaPipelineContext _context;
    private readonly FfmpegHardwareDevice _hardware;
    private readonly Dictionary<long, MediaTime> _durations = [];
    private IntPtr _context_;
    private IntPtr _packet;
    private IntPtr _frame;
    private IntPtr _softwareFrame;
    private IntPtr _deviceContext;
    private CodecConfig? _config;
    private string _lastFormatWarning = string.Empty;

    public FfmpegVideoDecoder(string decoderName, MediaPipelineContext context)
        : this(decoderName, context, FfmpegHardwareDevice.None)
    {
    }

    public FfmpegVideoDecoder(string decoderName, MediaPipelineContext context, FfmpegHardwareDevice hardware)
    {
        ArgumentNullException.ThrowIfNull(context);
        _decoderName = decoderName;
        _context = context;
        _hardware = hardware;
    }

    public string Name => (_hardware == FfmpegHardwareDevice.None ? "libavcodec-" : "libavcodec-hw-") + _decoderName;

    /// <summary>Whether the pictures of the current stream are coming off the GPU.</summary>
    public bool UsesHardware => _deviceContext != IntPtr.Zero;

    /// <summary>Frames decoded but not converted because of their pixel format.</summary>
    public long UnconvertedFrames { get; private set; }

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
            Marshal.WriteInt32(parameters, ParCodecType, MediaTypeVideo);
            Marshal.WriteInt32(parameters, ParCodecId, Marshal.ReadInt32(codec, CodecId));
            Marshal.WriteInt32(parameters, ParWidth, config.Width);
            Marshal.WriteInt32(parameters, ParHeight, config.Height);
            if (!config.Extradata.IsEmpty)
            {
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

        // Software decoding may use worker threads (design §2.3); results still arrive on
        // the caller's thread through avcodec_receive_frame.
        int threads = Math.Clamp(Environment.ProcessorCount, 1, MaxDecodeThreads);
        _ = Native.av_opt_set(_context_, "threads", threads.ToString(CultureInfo.InvariantCulture), 0);

        if (_hardware != FfmpegHardwareDevice.None)
            AttachHardwareDevice(codec);

        Check(Native.avcodec_open2(_context_, codec, IntPtr.Zero), "avcodec_open2");

        _packet = Native.av_packet_alloc();
        _frame = Native.av_frame_alloc();
        _softwareFrame = Native.av_frame_alloc();
        if (_packet == IntPtr.Zero || _frame == IntPtr.Zero || _softwareFrame == IntPtr.Zero)
            throw new MediaDecoderException("av_packet_alloc/av_frame_alloc failed.");

        _config = config;
        _durations.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask DecodeAsync(EncodedPacket packet, IDecodeOutput<VideoFrame> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConfigured();

        long stamp = packet.HasPts ? packet.Pts.Microseconds : NoPts;
        if (stamp != NoPts)
        {
            _durations[stamp] = packet.Duration;
            if (_durations.Count > 64)
                _durations.Remove(_durations.Keys.Min());
        }

        Check(Native.av_new_packet(_packet, packet.Length), "av_new_packet");
        try
        {
            IntPtr data = Marshal.ReadIntPtr(_packet, PacketData);
            Marshal.Copy(packet.Span.ToArray(), 0, data, packet.Length);
            Marshal.WriteInt64(_packet, PacketPts, stamp);
            Marshal.WriteInt64(_packet, PacketDts, stamp);
            int sent = Native.avcodec_send_packet(_context_, _packet);
            if (sent < 0 && sent != ErrorAgain)
            {
                // A damaged frame is dropped; the decoder resyncs at the next keyframe.
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

    public ValueTask DrainAsync(IDecodeOutput<VideoFrame> output, CancellationToken cancellationToken)
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
        _durations.Clear();
        return ValueTask.CompletedTask;
    }

    private void ReceiveAll(IDecodeOutput<VideoFrame> output)
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
                EmitFrame(output, ToSystemMemory());
            }
            finally
            {
                Native.av_frame_unref(_softwareFrame);
                Native.av_frame_unref(_frame);
            }
        }
    }

    /// <summary>
    /// Copies the picture into a pooled frame. 8-bit 4:2:0 (libavutil <c>yuv420p</c>,
    /// <c>yuvj420p</c>, <c>nv12</c>) is carried as is; other formats are counted and
    /// skipped until the converter lands (a logged gap, never a crash).
    /// </summary>
    /// <summary>
    /// A picture a hardware decoder produced lives in GPU memory; av_hwframe_transfer_data
    /// brings it into a system-memory frame. That transfer is the one copy the hardware
    /// path pays. A software picture is already where it needs to be.
    /// </summary>
    private IntPtr ToSystemMemory()
    {
        if (_deviceContext == IntPtr.Zero || Marshal.ReadIntPtr(_frame, FrameHwFramesCtx) == IntPtr.Zero)
            return _frame;

        Check(Native.av_hwframe_transfer_data(_softwareFrame, _frame, 0), "av_hwframe_transfer_data");
        // The transfer copies the picture but not its timing.
        Marshal.WriteInt64(_softwareFrame, FramePts, Marshal.ReadInt64(_frame, FramePts));
        FfmpegHardwareCounters.Transferred(
            (long)Marshal.ReadInt32(_softwareFrame, FrameLinesize) * Marshal.ReadInt32(_softwareFrame, FrameHeight));
        return _softwareFrame;
    }

    private void EmitFrame(IDecodeOutput<VideoFrame> output, IntPtr source)
    {
        int width = Marshal.ReadInt32(source, FrameWidth);
        int height = Marshal.ReadInt32(source, FrameHeight);
        int format = Marshal.ReadInt32(source, FrameFormat);
        long pts = Marshal.ReadInt64(source, FramePts);
        if (width <= 0 || height <= 0)
            return;

        string formatName = Marshal.PtrToStringAnsi(Native.av_get_pix_fmt_name(format)) ?? format.ToString(CultureInfo.InvariantCulture);
        VideoPixelFormat pixelFormat;
        switch (formatName)
        {
            case "yuv420p":
            case "yuvj420p":
                pixelFormat = VideoPixelFormat.I420;
                break;
            case "nv12":
                pixelFormat = VideoPixelFormat.Nv12;
                break;
            default:
                UnconvertedFrames++;
                if (_lastFormatWarning != formatName)
                {
                    _lastFormatWarning = formatName;
                    _context.Log.Emit(_context.Player, MediaEventKind.DecoderAttempt, MediaLogLevel.Warn,
                        $"{Name} produced {formatName} pictures, which this build cannot display yet.",
                        ("decoder", Name), ("result", "unconverted-format"), ("format", formatName));
                }
                return;
        }

        MediaTime timestamp = pts == NoPts ? MediaTime.NegativeInfinity : MediaTime.FromMicroseconds(pts);
        MediaTime duration = _durations.Remove(pts, out var known) ? known : MediaTime.Zero;
        var frame = VideoFrame.Allocate(_context.Limits, pixelFormat, width, height, timestamp, duration);
        try
        {
            for (int plane = 0; plane < frame.PlaneCount; plane++)
            {
                IntPtr planeData = Marshal.ReadIntPtr(source, FrameData + plane * IntPtr.Size);
                int linesize = Marshal.ReadInt32(source, FrameLinesize + plane * sizeof(int));
                if (planeData == IntPtr.Zero || linesize <= 0)
                    throw new MediaDecoderException("libavcodec returned a picture without plane data.");
                CopyPlane(planeData, linesize, frame, plane, pixelFormat);
            }
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        output.Emit(frame);
    }

    private static void CopyPlane(IntPtr source, int linesize, VideoFrame frame, int plane, VideoPixelFormat format)
    {
        int rowBytes = frame.GetPlaneWidth(plane) * (format == VideoPixelFormat.Nv12 && plane == 1 ? 2 : 1);
        int rows = frame.GetPlaneHeight(plane);
        int stride = frame.GetStride(plane);
        if (linesize < rowBytes)
            throw new MediaDecoderException("libavcodec returned a picture narrower than its width.");

        // A read-only view over libavcodec's plane for the length of this copy; the
        // frame is unreferenced by the caller right after, so the view never outlives it.
        var view = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AddByteOffset(ref Unsafe.NullRef<byte>(), (nint)source), checked(linesize * (rows - 1) + rowBytes));
        var destination = frame.GetPlane(plane);
        if (linesize == stride)
        {
            view.CopyTo(destination);
            return;
        }

        for (int y = 0; y < rows; y++)
            view.Slice(y * linesize, rowBytes).CopyTo(destination.Slice(y * stride, rowBytes));
    }

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

    /// <summary>
    /// Opens the platform's video device and gives it to libavcodec, but only when this
    /// decoder advertises a hardware configuration that takes one. A device that cannot be
    /// created - no GPU, no driver, a headless session - leaves the decoder decoding in
    /// software, with the reason logged once.
    /// </summary>
    private void AttachHardwareDevice(IntPtr codec)
    {
        int type = (int)_hardware;
        if (!SupportsHardwareDevice(codec, type))
        {
            LogSoftware($"the {_decoderName} decoder has no {DeviceTypeName(type)} configuration");
            return;
        }

        int created = Native.av_hwdevice_ctx_create(out IntPtr device, type, null, IntPtr.Zero, 0);
        if (created < 0 || device == IntPtr.Zero)
        {
            LogSoftware($"no {DeviceTypeName(type)} device: {Describe(created)}");
            return;
        }

        IntPtr reference = Native.av_buffer_ref(device);
        if (reference == IntPtr.Zero)
        {
            Native.av_buffer_unref(ref device);
            LogSoftware("av_buffer_ref failed");
            return;
        }

        Marshal.WriteIntPtr(_context_, ContextHwDeviceCtx, reference);
        _deviceContext = device;
        _context.Log.Emit(_context.Player, MediaEventKind.DecoderAttempt, MediaLogLevel.Info,
            $"{Name} decodes on the GPU through {DeviceTypeName(type)}.",
            ("decoder", Name), ("result", "hardware"), ("device", DeviceTypeName(type)));
    }

    /// <summary>Whether the decoder lists this device type among the ones it takes as an AVHWDeviceContext.</summary>
    private static bool SupportsHardwareDevice(IntPtr codec, int type)
    {
        for (int index = 0; ; index++)
        {
            IntPtr config = Native.avcodec_get_hw_config(codec, index);
            if (config == IntPtr.Zero)
                return false;
            if ((Marshal.ReadInt32(config, HwConfigMethods) & HwConfigMethodDeviceCtx) != 0 &&
                Marshal.ReadInt32(config, HwConfigDeviceType) == type)
            {
                return true;
            }
        }
    }

    private static string DeviceTypeName(int type) =>
        Marshal.PtrToStringAnsi(Native.av_hwdevice_get_type_name(type)) ?? type.ToString(CultureInfo.InvariantCulture);

    private void LogSoftware(string reason) =>
        _context.Log.Emit(_context.Player, MediaEventKind.DecoderAttempt, MediaLogLevel.Info,
            $"{Name} decodes in software: {reason}.", ("decoder", Name), ("result", "software"));

    private void Release()
    {
        if (_deviceContext != IntPtr.Zero)
            Native.av_buffer_unref(ref _deviceContext);
        if (_softwareFrame != IntPtr.Zero)
            Native.av_frame_free(ref _softwareFrame);
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
