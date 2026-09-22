using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Pipeline;
using static FenBrowser.Media.Gpu.Windows.D3D11Interop;

namespace FenBrowser.Media.Gpu.Windows;

/// <summary>
/// One Direct3D 11 device with video support, shared by every hardware decoder in the
/// process (design §5: "hardware decode is selected first"). Decoders leave their pictures
/// in GPU textures; <see cref="Download"/> brings one into a pooled <see cref="VideoFrame"/>
/// either converted to BGRA by the GPU's video processor (so the painter only copies) or,
/// when conversion is off, as the NV12 the decoder wrote.
/// </summary>
/// <remarks>
/// The immediate context is not thread-safe and several players may decode at once, so
/// every sequence of context calls runs under <see cref="Gate"/>; the device is also
/// multithread-protected, which Media Foundation requires of a device it decodes on.
/// A failure anywhere surfaces as a <see cref="MediaDecoderException"/>: the player fails
/// with MEDIA_ERR_DECODE and the process goes on.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class D3D11VideoDevice : IDisposable
{
    private static readonly Lock s_sharedGate = new();
    private static D3D11VideoDevice? s_shared;
    private static string s_sharedFailure = string.Empty;
    private static bool s_sharedTried;

    private readonly Lock _gate = new();
    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _videoDevice;
    private IntPtr _videoContext;
    private IntPtr _videoContext1;
    private Converter? _converter;
    private Staging? _staging;
    private bool _disposed;

    private D3D11VideoDevice(IntPtr device, IntPtr context, IntPtr videoDevice, IntPtr videoContext)
    {
        _device = device;
        _context = context;
        _videoDevice = videoDevice;
        _videoContext = videoContext;
    }

    /// <summary>The ID3D11Device, for adapters that hand it to their decoder API. The caller does not own the reference.</summary>
    public IntPtr Device => _device;

    /// <summary>Serialises immediate-context work between the decoders sharing the device.</summary>
    public Lock Gate => _gate;

    /// <summary>
    /// Whether <see cref="Download"/> converts to BGRA on the GPU (the default) or reads
    /// the decoder's NV12 back for the CPU converter; <c>FEN_MEDIA_GPU_CONVERT=off</c>
    /// turns it off, for comparison and as a kill switch.
    /// </summary>
    public static bool GpuConversionEnabled { get; internal set; } = !IsOff(Environment.GetEnvironmentVariable("FEN_MEDIA_GPU_CONVERT"));

    /// <summary><c>FEN_MEDIA_HW_DECODE=off</c> keeps every hardware decoder unregistered (design §4 kill switch).</summary>
    public static bool HardwareDecodeEnabled { get; } = !IsOff(Environment.GetEnvironmentVariable("FEN_MEDIA_HW_DECODE"));

    private static bool IsOff(string? value) =>
        value is not null && (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.Equals("0", StringComparison.Ordinal) || value.Equals("false", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The process's device, created on first use, or null with the reason it could not be
    /// (no adapter, a remote session without a GPU, the kill switch). Never disposed.
    /// </summary>
    public static D3D11VideoDevice? TryGetShared(out string reason)
    {
        lock (s_sharedGate)
        {
            if (!s_sharedTried)
            {
                s_sharedTried = true;
                if (!HardwareDecodeEnabled)
                    s_sharedFailure = "FEN_MEDIA_HW_DECODE is off.";
                else if (!TryCreate(out s_shared, out s_sharedFailure))
                    s_shared = null;
            }

            reason = s_sharedFailure;
            return s_shared;
        }
    }

    /// <summary>Creates a hardware device with video and BGRA support, multithread-protected.</summary>
    public static bool TryCreate(out D3D11VideoDevice? device, out string reason)
    {
        device = null;
        IntPtr d3dDevice = IntPtr.Zero, context = IntPtr.Zero, videoDevice = IntPtr.Zero, videoContext = IntPtr.Zero, multithread = IntPtr.Zero;
        try
        {
            int hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, CreateDeviceVideoSupport | CreateDeviceBgraSupport,
                IntPtr.Zero, 0, SdkVersion, out d3dDevice, out _, out context);
            if (hr < 0)
            {
                reason = $"D3D11CreateDevice failed: {Describe(hr)}";
                return false;
            }

            hr = QueryInterface(d3dDevice, IidID3D11VideoDevice, out videoDevice);
            if (hr < 0)
            {
                reason = $"The device has no video support: {Describe(hr)}";
                return false;
            }

            hr = QueryInterface(context, IidID3D11VideoContext, out videoContext);
            if (hr < 0)
            {
                reason = $"The context has no video support: {Describe(hr)}";
                return false;
            }

            hr = QueryInterface(context, IidID3D10Multithread, out multithread);
            if (hr >= 0)
                _ = Method<SetMultithreadProtectedFn>(multithread, SlotSetMultithreadProtected)(multithread, 1);

            // The DXGI colour-space form of the video processor calls (Windows 10 and later);
            // drivers honour it where they ignore the older bitfield.
            if (QueryInterface(context, IidID3D11VideoContext1, out var videoContext1) < 0)
                videoContext1 = IntPtr.Zero;

            device = new D3D11VideoDevice(d3dDevice, context, videoDevice, videoContext) { _videoContext1 = videoContext1 };
            d3dDevice = context = videoDevice = videoContext = IntPtr.Zero;
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            Release(ref multithread);
            Release(ref videoContext);
            Release(ref videoDevice);
            Release(ref context);
            Release(ref d3dDevice);
        }
    }

    /// <summary>The picture's texture format, or 0 when it is not one the engine downloads.</summary>
    public static int DescribeTexture(IntPtr texture, out int width, out int height, out int format)
    {
        Method<GetDescFn>(texture, SlotGetDesc)(texture, out var desc);
        width = (int)desc.Width;
        height = (int)desc.Height;
        format = desc.Format;
        return format is FormatNv12 or FormatP010 ? format : 0;
    }

    /// <summary>
    /// Brings the picture in array slice <paramref name="subresource"/> of
    /// <paramref name="texture"/> (NV12 or P010, at least <paramref name="width"/> by
    /// <paramref name="height"/>) into a pooled frame: BGRA through the video processor
    /// when <see cref="GpuConversionEnabled"/>, otherwise NV12 read straight back (P010
    /// only converts; without conversion it is reported as unconverted with null).
    /// </summary>
    public VideoFrame? Download(IntPtr texture, uint subresource, int width, int height, MediaLimits limits, MediaTime timestamp, MediaTime duration)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (DescribeTexture(texture, out int codedWidth, out int codedHeight, out int format) == 0)
            throw new MediaDecoderException($"The hardware decoder produced a texture of DXGI format {format}, which the engine cannot read.");
        if (width <= 0 || height <= 0 || width > codedWidth || height > codedHeight)
            throw new MediaDecoderException($"The picture ({width}x{height}) does not fit its texture ({codedWidth}x{codedHeight}).");

        lock (_gate)
        {
            if (GpuConversionEnabled)
            {
                var frame = VideoFrame.Allocate(limits, VideoPixelFormat.Bgra32, width, height, timestamp, duration);
                try
                {
                    ConvertToBgra(texture, subresource, format, codedWidth, codedHeight, frame);
                    GpuPictureCounters.Converted(frame.TotalBytes);
                    return frame;
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            }

            if (format != FormatNv12)
                return null;

            var nv12 = VideoFrame.Allocate(limits, VideoPixelFormat.Nv12, width, height, timestamp, duration);
            try
            {
                ReadBackNv12(texture, subresource, codedWidth, codedHeight, nv12);
                GpuPictureCounters.ReadBack(nv12.TotalBytes);
                return nv12;
            }
            catch
            {
                nv12.Dispose();
                throw;
            }
        }
    }

    private void ReadBackNv12(IntPtr texture, uint subresource, int codedWidth, int codedHeight, VideoFrame frame)
    {
        // Chroma is 2x2 subsampled, so the staging copy covers even dimensions.
        int copyWidth = Math.Min(codedWidth, (frame.Width + 1) & ~1);
        int copyHeight = Math.Min(codedHeight, (frame.Height + 1) & ~1);
        var staging = EnsureStaging(FormatNv12, copyWidth, copyHeight, ref _staging);
        var box = new Box { Left = 0, Top = 0, Front = 0, Right = (uint)copyWidth, Bottom = (uint)copyHeight, Back = 1 };
        Method<CopySubresourceRegionFn>(_context, SlotCopySubresourceRegion)(_context, staging, 0, 0, 0, 0, texture, subresource, ref box);

        int hr = Method<MapFn>(_context, SlotMap)(_context, staging, 0, MapRead, 0, out var mapped);
        if (hr < 0)
            throw new MediaDecoderException($"ID3D11DeviceContext::Map failed: {Describe(hr)}");
        try
        {
            int pitch = (int)mapped.RowPitch;
            CopyRows(mapped.Data, pitch, frame.GetPlane(0), frame.GetStride(0), frame.Width, frame.Height);
            // The chroma plane follows the luma rows at the same pitch.
            CopyRows(mapped.Data + (nint)pitch * copyHeight, pitch, frame.GetPlane(1), frame.GetStride(1), frame.GetPlaneWidth(1) * 2, frame.GetPlaneHeight(1));
        }
        finally
        {
            Method<UnmapFn>(_context, SlotUnmap)(_context, staging, 0);
        }
    }

    private void ConvertToBgra(IntPtr texture, uint subresource, int format, int codedWidth, int codedHeight, VideoFrame frame)
    {
        var converter = EnsureConverter(codedWidth, codedHeight, frame.Width, frame.Height);
        IntPtr inputView = IntPtr.Zero;
        try
        {
            var inputDesc = new VideoProcessorInputViewDesc { FourCC = 0, ViewDimension = ViewDimensionTexture2D, MipSlice = 0, ArraySlice = subresource };
            int hr = Method<CreateVideoProcessorInputViewFn>(_videoDevice, SlotCreateVideoProcessorInputView)(_videoDevice, texture, converter.Enumerator, ref inputDesc, out inputView);
            if (hr < 0)
                throw new MediaDecoderException($"ID3D11VideoDevice::CreateVideoProcessorInputView failed for DXGI format {format}: {Describe(hr)}");

            // The CPU converter's rule (design §2.5 note in PixelConverter): BT.601 below
            // 720 lines, BT.709 from there; studio range in, full-range RGB out.
            bool bt709 = frame.Height >= 720;
            uint streamSpace = ColorSpace(bt709, fullRange: false, rgbLimited: false);
            uint outputSpace = ColorSpace(bt709, fullRange: true, rgbLimited: false);
            Method<VideoProcessorSetStreamColorSpaceFn>(_videoContext, SlotVideoProcessorSetStreamColorSpace)(_videoContext, converter.Processor, 0, ref streamSpace);
            Method<VideoProcessorSetOutputColorSpaceFn>(_videoContext, SlotVideoProcessorSetOutputColorSpace)(_videoContext, converter.Processor, ref outputSpace);
            if (_videoContext1 != IntPtr.Zero)
            {
                Method<VideoProcessorSetStreamColorSpace1Fn>(_videoContext1, SlotVideoProcessorSetStreamColorSpace1)(_videoContext1, converter.Processor, 0, bt709 ? ColorSpaceYcbcrStudioG22LeftP709 : ColorSpaceYcbcrStudioG22LeftP601);
                Method<VideoProcessorSetOutputColorSpace1Fn>(_videoContext1, SlotVideoProcessorSetOutputColorSpace1)(_videoContext1, converter.Processor, ColorSpaceRgbFullG22NoneP709);
            }
            Method<VideoProcessorSetStreamFrameFormatFn>(_videoContext, SlotVideoProcessorSetStreamFrameFormat)(_videoContext, converter.Processor, 0, VideoFrameFormatProgressive);
            var rect = new Rect { Left = 0, Top = 0, Right = frame.Width, Bottom = frame.Height };
            Method<VideoProcessorSetStreamRectFn>(_videoContext, SlotVideoProcessorSetStreamSourceRect)(_videoContext, converter.Processor, 0, 1, ref rect);
            Method<VideoProcessorSetStreamRectFn>(_videoContext, SlotVideoProcessorSetStreamDestRect)(_videoContext, converter.Processor, 0, 1, ref rect);
            Method<VideoProcessorSetOutputTargetRectFn>(_videoContext, SlotVideoProcessorSetOutputTargetRect)(_videoContext, converter.Processor, 1, ref rect);

            var stream = new VideoProcessorStream { Enable = 1, InputSurface = inputView };
            hr = Method<VideoProcessorBltFn>(_videoContext, SlotVideoProcessorBlt)(_videoContext, converter.Processor, converter.OutputView, 0, 1, ref stream);
            if (hr < 0)
                throw new MediaDecoderException($"ID3D11VideoContext::VideoProcessorBlt failed: {Describe(hr)}");
        }
        finally
        {
            Release(ref inputView);
        }

        Method<CopyResourceFn>(_context, SlotCopyResource)(_context, converter.Staging, converter.Output);
        int mapHr = Method<MapFn>(_context, SlotMap)(_context, converter.Staging, 0, MapRead, 0, out var mapped);
        if (mapHr < 0)
            throw new MediaDecoderException($"ID3D11DeviceContext::Map failed: {Describe(mapHr)}");
        try
        {
            CopyRows(mapped.Data, (int)mapped.RowPitch, frame.GetPlane(0), frame.GetStride(0), frame.Width * 4, frame.Height);
        }
        finally
        {
            Method<UnmapFn>(_context, SlotUnmap)(_context, converter.Staging, 0);
        }
    }

    private static void CopyRows(IntPtr source, int sourcePitch, Span<byte> destination, int destinationStride, int rowBytes, int rows)
    {
        if (sourcePitch < rowBytes)
            throw new MediaDecoderException("The mapped texture is narrower than the picture.");

        // A read-only view over the mapped rows for the length of this copy; the texture is
        // unmapped by the caller right after, so the view never outlives the mapping.
        var view = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AddByteOffset(ref Unsafe.NullRef<byte>(), (nint)source), checked(sourcePitch * (rows - 1) + rowBytes));
        if (sourcePitch == destinationStride)
        {
            view.CopyTo(destination);
            return;
        }

        for (int y = 0; y < rows; y++)
            view.Slice(y * sourcePitch, rowBytes).CopyTo(destination.Slice(y * destinationStride, rowBytes));
    }

    private IntPtr EnsureStaging(int format, int width, int height, ref Staging? slot)
    {
        if (slot is { } existing && existing.Format == format && existing.Width == width && existing.Height == height)
            return existing.Texture;
        slot?.Dispose();
        slot = null;
        var texture = CreateTexture(format, width, height, UsageStaging, 0, CpuAccessRead);
        slot = new Staging(format, width, height, texture);
        return texture;
    }

    /// <summary>
    /// An NV12 texture holding <paramref name="frame"/>'s planes, for tests of the download
    /// paths (a real decoder writes its textures itself). The caller releases it.
    /// </summary>
    internal IntPtr UploadNv12(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Format != VideoPixelFormat.Nv12)
            throw new ArgumentException("Only NV12 frames upload.", nameof(frame));
        int width = (frame.Width + 1) & ~1;
        int height = (frame.Height + 1) & ~1;
        int pitch = frame.GetStride(0);
        if (frame.GetStride(1) != pitch)
            throw new ArgumentException("The planes must share a stride.", nameof(frame));

        // Y rows then the chroma rows, one pitch throughout, as D3D lays NV12 out.
        var contiguous = new byte[pitch * (height + height / 2)];
        frame.GetPlane(0).CopyTo(contiguous);
        frame.GetPlane(1).CopyTo(contiguous.AsSpan(pitch * height));
        var handle = GCHandle.Alloc(contiguous, GCHandleType.Pinned);
        try
        {
            var desc = new Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = FormatNv12,
                SampleCount = 1,
                Usage = UsageDefault,
            };
            var data = new SubresourceData { SystemMemory = handle.AddrOfPinnedObject(), SystemMemoryPitch = (uint)pitch, SystemMemorySlicePitch = (uint)contiguous.Length };
            lock (_gate)
            {
                int hr = Method<CreateTexture2DWithDataFn>(_device, SlotCreateTexture2D)(_device, ref desc, ref data, out var texture);
                if (hr < 0)
                    throw new MediaDecoderException($"ID3D11Device::CreateTexture2D with data failed: {Describe(hr)}");
                return texture;
            }
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Releases a texture handed out by this device.</summary>
    internal static void ReleaseTexture(ref IntPtr texture) => Release(ref texture);

    private IntPtr CreateTexture(int format, int width, int height, int usage, uint bindFlags, uint cpuAccess)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = usage,
            BindFlags = bindFlags,
            CpuAccessFlags = cpuAccess,
            MiscFlags = 0,
        };
        int hr = Method<CreateTexture2DFn>(_device, SlotCreateTexture2D)(_device, ref desc, IntPtr.Zero, out var texture);
        if (hr < 0)
            throw new MediaDecoderException($"ID3D11Device::CreateTexture2D ({width}x{height}, format {format}) failed: {Describe(hr)}");
        return texture;
    }

    private Converter EnsureConverter(int codedWidth, int codedHeight, int width, int height)
    {
        if (_converter is { } existing && existing.CodedWidth == codedWidth && existing.CodedHeight == codedHeight && existing.Width == width && existing.Height == height)
            return existing;
        _converter?.Dispose();
        _converter = null;

        var contentDesc = new VideoProcessorContentDesc
        {
            InputFrameFormat = VideoFrameFormatProgressive,
            InputFrameRateNumerator = 60,
            InputFrameRateDenominator = 1,
            InputWidth = (uint)codedWidth,
            InputHeight = (uint)codedHeight,
            OutputFrameRateNumerator = 60,
            OutputFrameRateDenominator = 1,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsagePlaybackNormal,
        };
        IntPtr enumerator = IntPtr.Zero, processor = IntPtr.Zero, output = IntPtr.Zero, outputView = IntPtr.Zero, staging = IntPtr.Zero;
        try
        {
            int hr = Method<CreateVideoProcessorEnumeratorFn>(_videoDevice, SlotCreateVideoProcessorEnumerator)(_videoDevice, ref contentDesc, out enumerator);
            if (hr < 0)
                throw new MediaDecoderException($"ID3D11VideoDevice::CreateVideoProcessorEnumerator failed: {Describe(hr)}");
            hr = Method<CreateVideoProcessorFn>(_videoDevice, SlotCreateVideoProcessor)(_videoDevice, enumerator, 0, out processor);
            if (hr < 0)
                throw new MediaDecoderException($"ID3D11VideoDevice::CreateVideoProcessor failed: {Describe(hr)}");

            output = CreateTexture(FormatB8G8R8A8Unorm, width, height, UsageDefault, BindRenderTarget, 0);
            var outputDesc = new VideoProcessorOutputViewDesc { ViewDimension = ViewDimensionTexture2D, MipSlice = 0, FirstArraySlice = 0, ArraySize = 0 };
            hr = Method<CreateVideoProcessorOutputViewFn>(_videoDevice, SlotCreateVideoProcessorOutputView)(_videoDevice, output, enumerator, ref outputDesc, out outputView);
            if (hr < 0)
                throw new MediaDecoderException($"ID3D11VideoDevice::CreateVideoProcessorOutputView failed: {Describe(hr)}");
            staging = CreateTexture(FormatB8G8R8A8Unorm, width, height, UsageStaging, 0, CpuAccessRead);

            var converter = new Converter(codedWidth, codedHeight, width, height, enumerator, processor, output, outputView, staging);
            enumerator = processor = output = outputView = staging = IntPtr.Zero;
            _converter = converter;
            return converter;
        }
        finally
        {
            Release(ref staging);
            Release(ref outputView);
            Release(ref output);
            Release(ref processor);
            Release(ref enumerator);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _converter?.Dispose();
            _converter = null;
            _staging?.Dispose();
            _staging = null;
            Release(ref _videoContext1);
            Release(ref _videoContext);
            Release(ref _videoDevice);
            Release(ref _context);
            Release(ref _device);
        }
    }

    private sealed class Staging(int format, int width, int height, IntPtr texture) : IDisposable
    {
        private IntPtr _texture = texture;
        public int Format { get; } = format;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public IntPtr Texture => _texture;
        public void Dispose() => Release(ref _texture);
    }

    private sealed class Converter(int codedWidth, int codedHeight, int width, int height, IntPtr enumerator, IntPtr processor, IntPtr output, IntPtr outputView, IntPtr staging) : IDisposable
    {
        private IntPtr _enumerator = enumerator;
        private IntPtr _processor = processor;
        private IntPtr _output = output;
        private IntPtr _outputView = outputView;
        private IntPtr _staging = staging;

        public int CodedWidth { get; } = codedWidth;
        public int CodedHeight { get; } = codedHeight;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public IntPtr Enumerator => _enumerator;
        public IntPtr Processor => _processor;
        public IntPtr Output => _output;
        public IntPtr OutputView => _outputView;
        public IntPtr Staging => _staging;

        public void Dispose()
        {
            Release(ref _staging);
            Release(ref _outputView);
            Release(ref _output);
            Release(ref _processor);
            Release(ref _enumerator);
        }
    }
}

/// <summary>
/// Process-wide counts of pictures that came off the GPU (design §5 "frame copies" and
/// the zero-copy counters of M7): how many were converted to BGRA there, how many were
/// read back as NV12 for the CPU converter, and the bytes each path moved.
/// </summary>
public static class GpuPictureCounters
{
    private static long s_converted;
    private static long s_readBack;
    private static long s_bytes;

    /// <summary>Pictures converted to BGRA by the video processor and read back.</summary>
    public static long ConvertedPictures => Interlocked.Read(ref s_converted);

    /// <summary>Pictures read back as NV12 without conversion.</summary>
    public static long ReadBackPictures => Interlocked.Read(ref s_readBack);

    /// <summary>Bytes copied from GPU to system memory by both paths.</summary>
    public static long BytesReadBack => Interlocked.Read(ref s_bytes);

    internal static void Converted(long bytes)
    {
        Interlocked.Increment(ref s_converted);
        Interlocked.Add(ref s_bytes, bytes);
    }

    internal static void ReadBack(long bytes)
    {
        Interlocked.Increment(ref s_readBack);
        Interlocked.Add(ref s_bytes, bytes);
    }
}
