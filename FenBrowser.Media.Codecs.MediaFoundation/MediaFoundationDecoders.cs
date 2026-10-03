using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Gpu.Windows;
using FenBrowser.Media.Pipeline;
using static FenBrowser.Media.Codecs.MediaFoundation.MfInterop;

namespace FenBrowser.Media.Codecs.MediaFoundation;

/// <summary>
/// Registers the OS decoders of ADR-0002 on Windows: Media Foundation's AAC and H.264
/// transforms, and HEVC when its extension is installed. Nothing is registered elsewhere,
/// or for a transform that cannot be created, so <c>canPlayType</c> stays truthful.
/// </summary>
public static class MediaFoundationDecoders
{
    public static bool TryRegister(DecoderRegistry decoders, IMediaLogSink log)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsWindows())
            return false;

        int registered = 0;
        foreach (var (codec, clsid, name) in Catalogue())
        {
            string reason;
            try
            {
                using var probe = MfTransform.Create(clsid, lowLatency: false);
                reason = string.Empty;
            }
            catch (MediaDecoderException ex)
            {
                reason = ex.Message;
            }

            if (reason.Length > 0)
            {
                log.Emit(PlayerId.None, MediaEventKind.DecoderAttempt, MediaLogLevel.Info,
                    $"Media Foundation has no {name} decoder here: {reason}",
                    ("decoder", "mf-" + name), ("result", "missing"));
                continue;
            }

            if (codec == MediaCodec.Aac)
                decoders.Register(new MfAudioDecoderFactory(codec, clsid, name));
            else
                decoders.Register(new MfVideoDecoderFactory(codec, clsid, name));
            registered++;
        }

        // The D3D11 device is not asked about here: creating it takes ~150 ms, and
        // registration runs whenever a page first touches a media element - often
        // only to feature-detect one. The first video decoder creates it.
        log.Emit(PlayerId.None, MediaEventKind.DecoderChosen, MediaLogLevel.Info,
            $"Media Foundation ready: {registered} OS decoders.",
            ("decoder", "mediafoundation"), ("count", registered.ToString(CultureInfo.InvariantCulture)));
        return registered > 0;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(MediaCodec Codec, Guid Clsid, string Name)> Catalogue()
    {
        yield return (MediaCodec.Aac, ClsidMsAacDecoder, "aac");
        yield return (MediaCodec.H264, ClsidMsH264Decoder, "h264");
        yield return (MediaCodec.Hevc, ClsidMsHevcDecoder, "hevc");
    }
}

[SupportedOSPlatform("windows")]
public sealed class MfAudioDecoderFactory(MediaCodec codec, Guid clsid, string name) : IDecoderFactory<AudioBlock>
{
    public string Name { get; } = "mf-" + name;

    public bool IsHardwareAccelerated => false;

    /// <summary>Below the software adapters, so a libavcodec decoder for the same codec (never built in, ADR-0002) would win.</summary>
    public int Priority => 5;

    public DecoderSupport Supports(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Kind == MediaTrackKind.Audio && config.Codec == codec ? DecoderSupport.Supported : DecoderSupport.Unsupported;
    }

    public IMediaDecoder<AudioBlock> Create(MediaPipelineContext context) => new MfAudioDecoder(clsid, Name, context);
}

[SupportedOSPlatform("windows")]
public sealed class MfVideoDecoderFactory(MediaCodec codec, Guid clsid, string name) : IDecoderFactory<VideoFrame>
{
    public string Name { get; } = "mf-" + name;

    /// <summary>True when the process has a Direct3D 11 device the transform decodes on (M7); it still falls back to software per stream.</summary>
    public bool IsHardwareAccelerated => MfGpuDevice.IsAvailable;

    public int Priority => 5;

    public DecoderSupport Supports(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // Every profile and level the web uses is within the OS decoder's reach; a bare
        // "avc1" with no profile is already "maybe" through the codecs string parser.
        return config.Kind == MediaTrackKind.Video && config.Codec == codec ? DecoderSupport.Supported : DecoderSupport.Unsupported;
    }

    public IMediaDecoder<VideoFrame> Create(MediaPipelineContext context) => new MfVideoDecoder(codec, clsid, Name, context);
}

/// <summary>
/// AAC through the Media Foundation AAC decoder: raw AAC frames in (the AudioSpecificConfig
/// from the container as MF_MT_USER_DATA), float or 16-bit PCM out, converted to the
/// engine's float blocks. Timestamps pass through the transform.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MfAudioDecoder : IMediaDecoder<AudioBlock>
{
    private readonly Guid _clsid;
    private readonly MediaPipelineContext _context;
    private MfTransform? _transform;
    private CodecConfig? _config;
    private bool _outputIsFloat;
    private int _outputChannels;
    private int _outputRate;
    private int _outputBits = 16;
    private long _nextSample;
    private bool _positioned;

    public MfAudioDecoder(Guid clsid, string name, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _clsid = clsid;
        Name = name;
        _context = context;
    }

    public string Name { get; }

    public ValueTask ConfigureAsync(CodecConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        Release();

        var transform = MfTransform.Create(_clsid, lowLatency: true);
        try
        {
            // Start from the decoder's own offered input type for the subtype (raw AAC frames,
            // else the HEAACWAVEINFO flavour) and fill in this stream's rate, channels and
            // AudioSpecificConfig; a type built from scratch is refused by some builds.
            int lastError = 0;
            bool accepted = false;
            foreach (var subtype in (Guid[])[MediaSubtypeRawAac1, MfAudioFormatAac])
            {
                var input = FindOfferedInputType(transform, subtype);
                if (input is null)
                    continue;
                try
                {
                    Set(input, MfMtAudioSamplesPerSecond, (uint)Math.Max(1, config.SampleRate));
                    Set(input, MfMtAudioNumChannels, (uint)Math.Max(1, config.Channels));
                    byte[] userData;
                    if (subtype == MediaSubtypeRawAac1)
                    {
                        userData = config.Extradata.ToArray();
                    }
                    else
                    {
                        Set(input, MfMtAacPayloadType, 0u);
                        Set(input, MfMtAacAudioProfileLevelIndication, 0x29u);
                        userData = new byte[12 + config.Extradata.Length];
                        BinaryPrimitives.WriteUInt16LittleEndian(userData.AsSpan(2), 0x29);
                        config.Extradata.Span.CopyTo(userData.AsSpan(12));
                    }

                    if (userData.Length > 0)
                    {
                        var userKey = MfMtUserData;
                        MfTransform.Check(input.SetBlob(ref userKey, userData, (uint)userData.Length), "IMFAttributes::SetBlob(MF_MT_USER_DATA)");
                    }

                    lastError = transform.Transform.SetInputType(0, input, 0);
                    if (lastError >= 0)
                    {
                        accepted = true;
                        break;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(input);
                }
            }

            if (!accepted)
                throw new MediaDecoderException($"IMFTransform::SetInputType failed for AAC: {Describe(lastError)}");

            var chosen = transform.NegotiateOutputType([MfAudioFormatFloat, MfAudioFormatPcm]);
            _outputIsFloat = chosen == MfAudioFormatFloat;
            var output = transform.OutputType!;
            _outputChannels = (int)Get(output, MfMtAudioNumChannels, (uint)Math.Max(1, config.Channels));
            _outputRate = (int)Get(output, MfMtAudioSamplesPerSecond, (uint)Math.Max(1, config.SampleRate));
            _outputBits = (int)Get(output, MfMtAudioBitsPerSample, _outputIsFloat ? 32u : 16u);
            _context.Limits.CheckAudioFormat(_outputRate, _outputChannels);
            transform.StartStreaming();
        }
        catch
        {
            transform.Dispose();
            throw;
        }

        _transform = transform;
        _config = config;
        _positioned = false;
        _nextSample = 0;
        return ValueTask.CompletedTask;
    }

    public ValueTask DecodeAsync(EncodedPacket packet, IDecodeOutput<AudioBlock> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        var transform = _transform ?? throw new InvalidOperationException("ConfigureAsync has not run.");

        // Output times continue a running sample count that stamped packets re-anchor (seeks).
        if (packet.HasPts)
        {
            long stamped = packet.Pts.ToTimescale(_outputRate);
            if (!_positioned || Math.Abs(stamped - _nextSample) > _outputRate / 10)
                _nextSample = stamped;
            _positioned = true;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (transform.ProcessInput(packet.Span, packet.Pts, packet.Duration))
                break;
            ReceiveAll(output);
        }

        ReceiveAll(output);
        return ValueTask.CompletedTask;
    }

    public ValueTask DrainAsync(IDecodeOutput<AudioBlock> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        var transform = _transform ?? throw new InvalidOperationException("ConfigureAsync has not run.");
        transform.Drain();
        ReceiveAll(output);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_transform is { } transform)
        {
            transform.Flush();
            transform.StartStreaming();
        }

        _positioned = false;
        return ValueTask.CompletedTask;
    }

    private void ReceiveAll(IDecodeOutput<AudioBlock> output)
    {
        var transform = _transform!;
        for (int guard = 0; guard < 4096; guard++)
        {
            var result = transform.ProcessOutput(sample => EmitSample(sample, output));
            if (result == MfTransform.OutputResult.NeedMoreInput)
                return;
            if (result == MfTransform.OutputResult.StreamChanged)
            {
                var chosen = transform.NegotiateOutputType([MfAudioFormatFloat, MfAudioFormatPcm]);
                _outputIsFloat = chosen == MfAudioFormatFloat;
                var type = transform.OutputType!;
                _outputChannels = (int)Get(type, MfMtAudioNumChannels, (uint)_outputChannels);
                _outputRate = (int)Get(type, MfMtAudioSamplesPerSecond, (uint)_outputRate);
                _outputBits = (int)Get(type, MfMtAudioBitsPerSample, (uint)_outputBits);
                _context.Limits.CheckAudioFormat(_outputRate, _outputChannels);
            }
        }
    }

    private void EmitSample(IMFSample sample, IDecodeOutput<AudioBlock> output)
    {
        byte[] bytes = MfTransform.ReadContiguous(sample, out int length);
        try
        {
            int bytesPerSample = _outputIsFloat ? 4 : _outputBits / 8;
            int frames = length / (bytesPerSample * _outputChannels);
            if (frames <= 0)
                return;

            long start = _nextSample;
            _nextSample += frames;
            var block = AudioBlock.Allocate(_context.Limits, _outputRate, _outputChannels, frames, MediaTime.FromTimescale(start, _outputRate));
            var samples = block.Samples;
            var data = bytes.AsSpan(0, length);
            int total = frames * _outputChannels;
            if (_outputIsFloat)
            {
                for (int i = 0; i < total; i++)
                    samples[i] = Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]), -1f, 1f);
            }
            else if (bytesPerSample == 2)
            {
                for (int i = 0; i < total; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]) / 32768f;
            }
            else
            {
                for (int i = 0; i < total; i++)
                    samples[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]) / 2147483648f;
            }

            output.Emit(block);
        }
        finally
        {
            MediaBufferPool.Return(bytes);
        }
    }

    private static IMFMediaType? FindOfferedInputType(MfTransform transform, Guid subtype)
    {
        for (uint index = 0; ; index++)
        {
            int hr = transform.Transform.GetInputAvailableType(0, index, out var candidate);
            if (hr < 0 || candidate is null)
                return null;
            var key = MfMtSubtype;
            if (candidate.GetGUID(ref key, out var offered) >= 0 && offered == subtype)
                return candidate;
            Marshal.ReleaseComObject(candidate);
        }
    }

    private static void Set(IMFMediaType type, Guid key, Guid value) =>
        MfTransform.Check(type.SetGUID(ref key, ref value), "IMFAttributes::SetGUID");

    private static void Set(IMFMediaType type, Guid key, uint value) =>
        MfTransform.Check(type.SetUINT32(ref key, value), "IMFAttributes::SetUINT32");

    private static uint Get(IMFMediaType type, Guid key, uint fallback) =>
        type.GetUINT32(ref key, out uint value) >= 0 ? value : fallback;

    private void Release()
    {
        _transform?.Dispose();
        _transform = null;
        _config = null;
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// H.264 (and HEVC) through the Media Foundation decoders: the container's length-prefixed
/// access units are rewritten as Annex B byte streams with the parameter sets from avcC or
/// hvcC in front of every sync sample, NV12 pictures come out through IMF2DBuffer with
/// their pitch, cropped to the display aperture. The transform reorders B-frames and stamps
/// each picture with the presentation time of the access unit that showed it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MfVideoDecoder : IMediaDecoder<VideoFrame>
{
    private static readonly byte[] s_startCode = [0, 0, 0, 1];

    private readonly MediaCodec _codec;
    private readonly Guid _clsid;
    private readonly MediaPipelineContext _context;
    private readonly Dictionary<long, MediaTime> _durations = [];
    private MfTransform? _transform;
    private CodecConfig? _config;
    private byte[] _parameterSets = [];
    private int _lengthSize = 4;
    private int _codedWidth;
    private int _codedHeight;
    private int _displayWidth;
    private int _displayHeight;
    private int _defaultStride;
    private bool _pendingParameterSets = true;
    private D3D11VideoDevice? _device;
    private bool _warnedUnconverted;

    public MfVideoDecoder(MediaCodec codec, Guid clsid, string name, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _codec = codec;
        _clsid = clsid;
        Name = name;
        _context = context;
    }

    public string Name { get; }

    public ValueTask ConfigureAsync(CodecConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        Release();
        (_parameterSets, _lengthSize) = _codec == MediaCodec.Hevc ? ParseHvcC(config.Extradata.Span) : ParseAvcC(config.Extradata.Span);

        // Without MF_LOW_LATENCY the OS decoder holds a full decoded picture buffer (16
        // frames at small sizes) before the first picture comes out: a paused element
        // would fill its audio look-ahead and still have no picture to show, and a seek
        // would wait a second for its frame.
        var transform = MfTransform.Create(_clsid, lowLatency: true);
        try
        {
            MfTransform.Check(MFCreateMediaType(out var input), "MFCreateMediaType");
            try
            {
                var major = MfMtMajorType;
                var video = MfMediaTypeVideo;
                MfTransform.Check(input.SetGUID(ref major, ref video), "IMFAttributes::SetGUID");
                var subtypeKey = MfMtSubtype;
                var subtype = _codec == MediaCodec.Hevc ? MfVideoFormatHevc : MfVideoFormatH264;
                MfTransform.Check(input.SetGUID(ref subtypeKey, ref subtype), "IMFAttributes::SetGUID");
                if (config.Width > 0 && config.Height > 0)
                {
                    var sizeKey = MfMtFrameSize;
                    MfTransform.Check(input.SetUINT64(ref sizeKey, ((ulong)(uint)config.Width << 32) | (uint)config.Height), "IMFAttributes::SetUINT64");
                }

                var interlaceKey = MfMtInterlaceMode;
                _ = input.SetUINT32(ref interlaceKey, 2); // MFVideoInterlace_Progressive
                transform.SetInputType(input);
            }
            finally
            {
                Marshal.ReleaseComObject(input);
            }

            _ = transform.NegotiateOutputType([MfVideoFormatNv12]);
            ReadOutputGeometry(transform.OutputType!, config);
            // The device manager goes in once the types are set (MFT_MESSAGE_SET_D3D_MANAGER);
            // a transform that declines it decodes in software, as every one did before M7.
            _device = null;
            if (MfGpuDevice.TryGet(out var manager, out var device))
            {
                if (transform.TrySetD3DManager(manager, out string declined))
                {
                    _device = device;
                }
                else
                {
                    _context.Log.Emit(_context.Player, MediaEventKind.DecoderAttempt, MediaLogLevel.Info,
                        $"{Name} decodes in software: {declined}", ("decoder", Name), ("result", "software"));
                }
            }

            transform.StartStreaming();
        }
        catch
        {
            transform.Dispose();
            throw;
        }

        _transform = transform;
        _config = config;
        _pendingParameterSets = true;
        _durations.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>MF_MT_FRAME_SIZE is the coded size; MF_MT_MINIMUM_DISPLAY_APERTURE (an MFVideoArea) the part to show.</summary>
    private void ReadOutputGeometry(IMFMediaType type, CodecConfig config)
    {
        var sizeKey = MfMtFrameSize;
        if (type.GetUINT64(ref sizeKey, out ulong packed) >= 0)
        {
            _codedWidth = (int)(packed >> 32);
            _codedHeight = (int)(packed & 0xFFFFFFFF);
        }
        else
        {
            _codedWidth = config.Width;
            _codedHeight = config.Height;
        }

        _displayWidth = _codedWidth;
        _displayHeight = _codedHeight;
        var apertureKey = MfMtMinimumDisplayAperture;
        var aperture = new byte[16];
        if (type.GetBlob(ref apertureKey, aperture, 16, out uint written) >= 0 && written == 16)
        {
            int width = BinaryPrimitives.ReadInt32LittleEndian(aperture.AsSpan(8));
            int height = BinaryPrimitives.ReadInt32LittleEndian(aperture.AsSpan(12));
            if (width > 0 && height > 0 && width <= _codedWidth && height <= _codedHeight)
            {
                _displayWidth = width;
                _displayHeight = height;
            }
        }
        else if (config.Width > 0 && config.Height > 0 && config.Width <= _codedWidth && config.Height <= _codedHeight)
        {
            _displayWidth = config.Width;
            _displayHeight = config.Height;
        }

        var strideKey = MfMtDefaultStride;
        _defaultStride = type.GetUINT32(ref strideKey, out uint stride) >= 0 && stride > 0 && stride <= int.MaxValue ? (int)stride : _codedWidth;
        _context.Limits.CheckVideoDimensions(_displayWidth, _displayHeight);
    }

    /// <summary>ISO/IEC 14496-15 §5.3.3.1 AVCDecoderConfigurationRecord: the NAL length size and the SPS/PPS as an Annex B prefix.</summary>
    internal static (byte[] ParameterSets, int LengthSize) ParseAvcC(ReadOnlySpan<byte> avcC)
    {
        if (avcC.Length < 7 || avcC[0] != 1)
            throw new MediaDecoderException("The avcC record is missing or not version 1.");
        int lengthSize = (avcC[4] & 3) + 1;
        var sets = new List<byte>();
        int at = 5;
        int spsCount = avcC[at++] & 0x1F;
        for (int i = 0; i < spsCount; i++)
            at = AppendNal(avcC, at, sets);
        if (at >= avcC.Length)
            throw new MediaDecoderException("The avcC record ends before its PPS count.");
        int ppsCount = avcC[at++];
        for (int i = 0; i < ppsCount; i++)
            at = AppendNal(avcC, at, sets);
        return (sets.ToArray(), lengthSize);
    }

    /// <summary>ISO/IEC 14496-15 §8.3.3.1 HEVCDecoderConfigurationRecord: arrays of VPS/SPS/PPS.</summary>
    internal static (byte[] ParameterSets, int LengthSize) ParseHvcC(ReadOnlySpan<byte> hvcC)
    {
        if (hvcC.Length < 23 || hvcC[0] != 1)
            throw new MediaDecoderException("The hvcC record is missing or not version 1.");
        int lengthSize = (hvcC[21] & 3) + 1;
        var sets = new List<byte>();
        int arrays = hvcC[22];
        int at = 23;
        for (int i = 0; i < arrays; i++)
        {
            if (at + 3 > hvcC.Length)
                throw new MediaDecoderException("The hvcC record ends inside an array.");
            int count = (hvcC[at + 1] << 8) | hvcC[at + 2];
            at += 3;
            for (int n = 0; n < count; n++)
                at = AppendNal(hvcC, at, sets);
        }

        return (sets.ToArray(), lengthSize);
    }

    private static int AppendNal(ReadOnlySpan<byte> record, int at, List<byte> sets)
    {
        if (at + 2 > record.Length)
            throw new MediaDecoderException("The decoder configuration record ends inside a length.");
        int length = (record[at] << 8) | record[at + 1];
        at += 2;
        if (at + length > record.Length)
            throw new MediaDecoderException("The decoder configuration record ends inside a parameter set.");
        sets.AddRange(s_startCode);
        sets.AddRange(record.Slice(at, length));
        return at + length;
    }

    /// <summary>The access unit as an Annex B byte stream: start codes for the length prefixes, parameter sets first on sync samples.</summary>
    internal byte[] ToAnnexB(ReadOnlySpan<byte> accessUnit, bool keyframe)
    {
        var stream = new List<byte>(accessUnit.Length + _parameterSets.Length + 32);
        if (keyframe || _pendingParameterSets)
        {
            stream.AddRange(_parameterSets);
            _pendingParameterSets = false;
        }

        int at = 0;
        while (at + _lengthSize <= accessUnit.Length)
        {
            int length = 0;
            for (int i = 0; i < _lengthSize; i++)
                length = (length << 8) | accessUnit[at + i];
            at += _lengthSize;
            if (length < 0 || at + length > accessUnit.Length)
                throw new MediaDecoderException("A NAL unit length runs past its access unit.");
            stream.AddRange(s_startCode);
            stream.AddRange(accessUnit.Slice(at, length));
            at += length;
        }

        return stream.ToArray();
    }

    public ValueTask DecodeAsync(EncodedPacket packet, IDecodeOutput<VideoFrame> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        var transform = _transform ?? throw new InvalidOperationException("ConfigureAsync has not run.");

        var pts = packet.HasPts ? packet.Pts : MediaTime.Zero;
        long key = Math.Max(0, ToMfTime(pts));
        _durations[key] = packet.Duration;
        if (_durations.Count > 64)
            _durations.Remove(_durations.Keys.Min());

        var annexB = ToAnnexB(packet.Span, packet.IsKeyframe);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (transform.ProcessInput(annexB, pts, packet.Duration))
                break;
            ReceiveAll(output);
        }

        ReceiveAll(output);
        return ValueTask.CompletedTask;
    }

    public ValueTask DrainAsync(IDecodeOutput<VideoFrame> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        var transform = _transform ?? throw new InvalidOperationException("ConfigureAsync has not run.");
        transform.Drain();
        ReceiveAll(output);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_transform is { } transform)
        {
            transform.Flush();
            transform.StartStreaming();
        }

        _pendingParameterSets = true;
        _durations.Clear();
        return ValueTask.CompletedTask;
    }

    private void ReceiveAll(IDecodeOutput<VideoFrame> output)
    {
        var transform = _transform!;
        var config = _config!;
        for (int guard = 0; guard < 4096; guard++)
        {
            var result = transform.ProcessOutput(sample => EmitSample(sample, output));
            if (result == MfTransform.OutputResult.NeedMoreInput)
                return;
            if (result == MfTransform.OutputResult.StreamChanged)
            {
                _ = transform.NegotiateOutputType([MfVideoFormatNv12]);
                ReadOutputGeometry(transform.OutputType!, config);
            }
        }
    }

    private void EmitSample(IMFSample sample, IDecodeOutput<VideoFrame> output)
    {
        long time = sample.GetSampleTime(out long mfTime) >= 0 ? mfTime : 0;
        var timestamp = FromMfTime(time);
        var duration = _durations.Remove(time, out var known) ? known
            : sample.GetSampleDuration(out long mfDuration) >= 0 && mfDuration > 0 ? FromMfTime(mfDuration)
            : MediaTime.Zero;

        if (_device is { } device && TryEmitFromGpu(sample, device, timestamp, duration, output))
            return;

        MfTransform.Check(sample.ConvertToContiguousBuffer(out var buffer), "IMFSample::ConvertToContiguousBuffer");
        try
        {
            var frame = VideoFrame.Allocate(_context.Limits, VideoPixelFormat.Nv12, _displayWidth, _displayHeight, timestamp, duration);
            try
            {
                if (buffer is IMF2DBuffer twoD && twoD.Lock2D(out var scanline0, out int pitch) >= 0)
                {
                    try
                    {
                        CopyNv12(scanline0, Math.Abs(pitch), frame);
                    }
                    finally
                    {
                        _ = twoD.Unlock2D();
                    }
                }
                else
                {
                    MfTransform.Check(buffer.Lock(out var pointer, out _, out uint current), "IMFMediaBuffer::Lock");
                    try
                    {
                        if (current < (uint)(_defaultStride * _codedHeight * 3 / 2))
                            throw new MediaDecoderException("The decoder returned a picture shorter than its frame size.");
                        CopyNv12(pointer, _defaultStride, frame);
                    }
                    finally
                    {
                        _ = buffer.Unlock();
                    }
                }
            }
            catch
            {
                frame.Dispose();
                throw;
            }

            output.Emit(frame);
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>
    /// A sample the transform decoded on the GPU carries a DXGI buffer over the texture
    /// slice holding the picture; the device brings it into a frame. False when the buffer
    /// is system memory, so the caller reads it the software way.
    /// </summary>
    private bool TryEmitFromGpu(IMFSample sample, D3D11VideoDevice device, MediaTime timestamp, MediaTime duration, IDecodeOutput<VideoFrame> output)
    {
        if (sample.GetBufferByIndex(0, out var buffer) < 0 || buffer is null)
            return false;
        try
        {
            if (buffer is not IMFDXGIBuffer dxgi)
                return false;

            var iid = D3D11Interop.IidID3D11Texture2D;
            MfTransform.Check(dxgi.GetResource(ref iid, out var texture), "IMFDXGIBuffer::GetResource");
            try
            {
                MfTransform.Check(dxgi.GetSubresourceIndex(out uint index), "IMFDXGIBuffer::GetSubresourceIndex");
                var frame = device.Download(texture, index, _displayWidth, _displayHeight, _context.Limits, timestamp, duration);
                if (frame is null)
                {
                    if (!_warnedUnconverted)
                    {
                        _warnedUnconverted = true;
                        _context.Log.Emit(_context.Player, MediaEventKind.DecoderAttempt, MediaLogLevel.Warn,
                            $"{Name} produced 10-bit pictures that only the GPU converter can display, and it is off.",
                            ("decoder", Name), ("result", "unconverted-format"));
                    }

                    return true;
                }

                output.Emit(frame);
                return true;
            }
            finally
            {
                D3D11VideoDevice.ReleaseTexture(ref texture);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>The luma plane, then the interleaved chroma plane at <c>codedHeight</c> rows down, cropped to the display size.</summary>
    private void CopyNv12(IntPtr scanline0, int pitch, VideoFrame frame)
    {
        int rowBytes = frame.Width;
        var row = MediaBufferPool.Bytes.Rent(Math.Max(rowBytes, 2));
        try
        {
            var luma = frame.GetPlane(0);
            int lumaStride = frame.GetStride(0);
            for (int y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(scanline0 + (nint)y * pitch, row, 0, rowBytes);
                row.AsSpan(0, rowBytes).CopyTo(luma[(y * lumaStride)..]);
            }

            var chroma = frame.GetPlane(1);
            int chromaStride = frame.GetStride(1);
            int chromaRows = frame.GetPlaneHeight(1);
            int chromaBytes = frame.GetPlaneWidth(1) * 2;
            var chromaBase = scanline0 + (nint)_codedHeight * pitch;
            if (chromaBytes > row.Length)
            {
                MediaBufferPool.Return(row);
                row = MediaBufferPool.Bytes.Rent(chromaBytes);
            }

            for (int y = 0; y < chromaRows; y++)
            {
                Marshal.Copy(chromaBase + (nint)y * pitch, row, 0, chromaBytes);
                row.AsSpan(0, chromaBytes).CopyTo(chroma[(y * chromaStride)..]);
            }
        }
        finally
        {
            MediaBufferPool.Return(row);
        }
    }

    private void Release()
    {
        _transform?.Dispose();
        _transform = null;
        _config = null;
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }
}
