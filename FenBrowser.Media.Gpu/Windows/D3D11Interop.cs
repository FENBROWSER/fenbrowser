using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FenBrowser.Media.Gpu.Windows;

/// <summary>
/// The slice of Direct3D 11 the media engine calls: device creation, textures, staging
/// copies and the video processor. COM methods are reached through their vtable slots
/// (the slot numbers come from the SDK's <c>d3d11.h</c> C vtables) with one delegate per
/// method, so no interface has to be declared in full and no unsafe code is needed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class D3D11Interop
{
    public const int DriverTypeHardware = 1;
    public const uint CreateDeviceBgraSupport = 0x20;
    public const uint CreateDeviceVideoSupport = 0x800;
    public const uint SdkVersion = 7;

    public const int FormatB8G8R8A8Unorm = 87;
    public const int FormatNv12 = 103;
    public const int FormatP010 = 104;

    public const int UsageDefault = 0;
    public const int UsageStaging = 3;
    public const uint BindRenderTarget = 0x20;
    public const uint CpuAccessRead = 0x20000;
    public const int MapRead = 1;

    public const int VideoFrameFormatProgressive = 0;
    public const int VideoUsagePlaybackNormal = 0;
    public const int ViewDimensionTexture2D = 1;

    public static readonly Guid IidID3D10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    public static readonly Guid IidID3D11VideoDevice = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
    public static readonly Guid IidID3D11VideoContext = new("61F21C45-3C0E-4A74-9CEA-67100D9AD5E4");
    public static readonly Guid IidID3D11VideoContext1 = new("A7F026DA-A5F8-4487-A564-15E34357651E");
    public static readonly Guid IidID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    // ID3D11Device
    public const int SlotCreateTexture2D = 5;
    public const int SlotGetDeviceRemovedReason = 39;
    public const int SlotGetImmediateContext = 40;

    // ID3D11DeviceContext
    public const int SlotMap = 14;
    public const int SlotUnmap = 15;
    public const int SlotCopySubresourceRegion = 46;
    public const int SlotCopyResource = 47;

    // ID3D11Texture2D
    public const int SlotGetDesc = 10;

    // ID3D10Multithread
    public const int SlotSetMultithreadProtected = 5;

    // ID3D11VideoDevice
    public const int SlotCreateVideoProcessor = 4;
    public const int SlotCreateVideoProcessorInputView = 8;
    public const int SlotCreateVideoProcessorOutputView = 9;
    public const int SlotCreateVideoProcessorEnumerator = 10;

    // ID3D11VideoContext
    public const int SlotVideoProcessorSetOutputTargetRect = 13;
    public const int SlotVideoProcessorSetOutputColorSpace = 15;
    public const int SlotVideoProcessorSetStreamFrameFormat = 27;
    public const int SlotVideoProcessorSetStreamColorSpace = 28;
    public const int SlotVideoProcessorSetStreamSourceRect = 30;
    public const int SlotVideoProcessorSetStreamDestRect = 31;
    public const int SlotVideoProcessorBlt = 53;

    // ID3D11VideoContext1
    public const int SlotVideoProcessorSetOutputColorSpace1 = 70;
    public const int SlotVideoProcessorSetStreamColorSpace1 = 74;

    // DXGI_COLOR_SPACE_TYPE
    public const int ColorSpaceRgbFullG22NoneP709 = 0;
    public const int ColorSpaceYcbcrStudioG22LeftP601 = 6;
    public const int ColorSpaceYcbcrFullG22LeftP601 = 7;
    public const int ColorSpaceYcbcrStudioG22LeftP709 = 8;
    public const int ColorSpaceYcbcrFullG22LeftP709 = 9;

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [StructLayout(LayoutKind.Sequential)]
    public struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SubresourceData
    {
        public IntPtr SystemMemory;
        public uint SystemMemoryPitch;
        public uint SystemMemorySlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Box
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoProcessorContentDesc
    {
        public int InputFrameFormat;
        public uint InputFrameRateNumerator;
        public uint InputFrameRateDenominator;
        public uint InputWidth;
        public uint InputHeight;
        public uint OutputFrameRateNumerator;
        public uint OutputFrameRateDenominator;
        public uint OutputWidth;
        public uint OutputHeight;
        public int Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoProcessorInputViewDesc
    {
        public uint FourCC;
        public int ViewDimension;
        public uint MipSlice;
        public uint ArraySlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoProcessorOutputViewDesc
    {
        public int ViewDimension;
        public uint MipSlice;
        public uint FirstArraySlice;
        public uint ArraySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoProcessorStream
    {
        public int Enable;
        public uint OutputIndex;
        public uint InputFrameOrField;
        public uint PastFrames;
        public uint FutureFrames;
        public IntPtr PastSurfaces;
        public IntPtr InputSurface;
        public IntPtr FutureSurfaces;
        public IntPtr PastSurfacesRight;
        public IntPtr InputSurfaceRight;
        public IntPtr FutureSurfacesRight;
    }

    /// <summary>
    /// D3D11_VIDEO_PROCESSOR_COLOR_SPACE packed: Usage (bit 0), RGB_Range (bit 1),
    /// YCbCr_Matrix (bit 2: 0 BT.601, 1 BT.709), YCbCr_xvYCC (bit 3), Nominal_Range (bits 4-5:
    /// 1 is 16-235, 2 is 0-255).
    /// </summary>
    public static uint ColorSpace(bool bt709, bool fullRange, bool rgbLimited) =>
        (rgbLimited ? 2u : 0u) | (bt709 ? 4u : 0u) | ((fullRange ? 2u : 1u) << 4);

    public delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);
    public delegate uint AddRefFn(IntPtr self);
    public delegate uint ReleaseFn(IntPtr self);
    public delegate int CreateTexture2DFn(IntPtr self, ref Texture2DDesc desc, IntPtr initialData, out IntPtr texture);
    public delegate int CreateTexture2DWithDataFn(IntPtr self, ref Texture2DDesc desc, ref SubresourceData initialData, out IntPtr texture);
    public delegate int GetDeviceRemovedReasonFn(IntPtr self);
    public delegate void GetImmediateContextFn(IntPtr self, out IntPtr context);
    public delegate int MapFn(IntPtr self, IntPtr resource, uint subresource, int mapType, uint mapFlags, out MappedSubresource mapped);
    public delegate void UnmapFn(IntPtr self, IntPtr resource, uint subresource);
    public delegate void CopySubresourceRegionFn(IntPtr self, IntPtr destination, uint destinationSubresource, uint x, uint y, uint z, IntPtr source, uint sourceSubresource, ref Box box);
    public delegate void CopyResourceFn(IntPtr self, IntPtr destination, IntPtr source);
    public delegate void GetDescFn(IntPtr self, out Texture2DDesc desc);
    public delegate int SetMultithreadProtectedFn(IntPtr self, int protect);
    public delegate int CreateVideoProcessorEnumeratorFn(IntPtr self, ref VideoProcessorContentDesc desc, out IntPtr enumerator);
    public delegate int CreateVideoProcessorFn(IntPtr self, IntPtr enumerator, uint rateConversionIndex, out IntPtr processor);
    public delegate int CreateVideoProcessorInputViewFn(IntPtr self, IntPtr resource, IntPtr enumerator, ref VideoProcessorInputViewDesc desc, out IntPtr view);
    public delegate int CreateVideoProcessorOutputViewFn(IntPtr self, IntPtr resource, IntPtr enumerator, ref VideoProcessorOutputViewDesc desc, out IntPtr view);
    public delegate void VideoProcessorSetOutputTargetRectFn(IntPtr self, IntPtr processor, int enable, ref Rect rect);
    public delegate void VideoProcessorSetOutputColorSpaceFn(IntPtr self, IntPtr processor, ref uint colorSpace);
    public delegate void VideoProcessorSetStreamFrameFormatFn(IntPtr self, IntPtr processor, uint stream, int frameFormat);
    public delegate void VideoProcessorSetStreamColorSpaceFn(IntPtr self, IntPtr processor, uint stream, ref uint colorSpace);
    public delegate void VideoProcessorSetOutputColorSpace1Fn(IntPtr self, IntPtr processor, int colorSpace);
    public delegate void VideoProcessorSetStreamColorSpace1Fn(IntPtr self, IntPtr processor, uint stream, int colorSpace);
    public delegate void VideoProcessorSetStreamRectFn(IntPtr self, IntPtr processor, uint stream, int enable, ref Rect rect);
    public delegate int VideoProcessorBltFn(IntPtr self, IntPtr processor, IntPtr outputView, uint outputFrame, uint streamCount, ref VideoProcessorStream streams);

    /// <summary>The delegate for vtable slot <paramref name="slot"/> of the COM object at <paramref name="instance"/>.</summary>
    public static T Method<T>(IntPtr instance, int slot)
        where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        IntPtr function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    public static int QueryInterface(IntPtr instance, Guid iid, out IntPtr result) =>
        Method<QueryInterfaceFn>(instance, 0)(instance, ref iid, out result);

    public static void AddRef(IntPtr instance) => Method<AddRefFn>(instance, 1)(instance);

    public static void Release(ref IntPtr instance)
    {
        if (instance == IntPtr.Zero)
            return;
        var pointer = instance;
        instance = IntPtr.Zero;
        Method<ReleaseFn>(pointer, 2)(pointer);
    }

    public static string Describe(int hr) => $"0x{hr:X8}";
}
