using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FenBrowser.Media.Codecs.MediaFoundation;

/// <summary>
/// The slice of Media Foundation (mfapi.h, mftransform.h, mfobjects.h) the decoders need,
/// declared by hand in vtable order with only the methods used. Every method keeps its
/// HRESULT so a failure is a return code the adapter turns into a media event, never an
/// exception from the marshaller.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MfInterop
{
    public const uint MfVersion = 0x00020070;
    public const uint MfStartupFull = 0;
    public const int ClsctxInprocServer = 0x1;

    public const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    public const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    public const int MfETransformTypeNotSet = unchecked((int)0xC00D6D60);
    public const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
    public const int MfEInvalidMediaType = unchecked((int)0xC00D36B4);

    public const uint MftMessageCommandFlush = 0x00000000;
    public const uint MftMessageCommandDrain = 0x00000001;
    public const uint MftMessageNotifyBeginStreaming = 0x10000000;
    public const uint MftMessageNotifyEndStreaming = 0x10000001;
    public const uint MftMessageNotifyEndOfStream = 0x10000002;
    public const uint MftMessageNotifyStartOfStream = 0x10000003;

    public const uint MftOutputStreamProvidesSamples = 0x00000100;
    public const uint MftOutputStreamCanProvideSamples = 0x00000200;
    public const uint MftOutputDataBufferFormatChange = 0x00000100;

    public static readonly Guid ClsidMsH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    public static readonly Guid ClsidMsHevcDecoder = new("420A51A3-D605-430C-B4FC-45274FA6C562");
    public static readonly Guid ClsidMsAacDecoder = new("32D186A7-218F-4C75-8876-DD77273A8999");
    public static readonly Guid IidIMFTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");

    public static readonly Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    public static readonly Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    public static readonly Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MfAudioFormatAac = new("00001610-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaSubtypeRawAac1 = new("000000FF-0000-0010-8000-00AA00389B71");
    public static readonly Guid MfMtAudioBlockAlignment = new("322DE230-9EEB-43BD-AB7A-FF412251541D");
    public static readonly Guid MfMtAudioAvgBytesPerSecond = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
    public static readonly Guid MfAudioFormatFloat = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MfVideoFormatHevc = new("43564548-0000-0010-8000-00AA00389B71");
    public static readonly Guid MfVideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid MfMtAudioSamplesPerSecond = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    public static readonly Guid MfMtAudioNumChannels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    public static readonly Guid MfMtAudioBitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
    public static readonly Guid MfMtAacPayloadType = new("BFBABE79-7434-4D1C-94F0-72A3B9E17188");
    public static readonly Guid MfMtAacAudioProfileLevelIndication = new("7632F0E6-9538-4D61-ACDA-EA29C8C14456");
    public static readonly Guid MfMtUserData = new("B6BC765F-4C3B-40A4-BD51-2535B66FE09D");
    public static readonly Guid MfMtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    public static readonly Guid MfMtDefaultStride = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    public static readonly Guid MfMtMinimumDisplayAperture = new("D7388766-18FE-48C6-A177-EE894867C8C4");
    public static readonly Guid MfMtInterlaceMode = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    public static readonly Guid MfLowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    [StructLayout(LayoutKind.Sequential)]
    public struct MftOutputStreamInfo
    {
        public uint Flags;
        public uint Size;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MftInputStreamInfo
    {
        public long MaxLatency;
        public uint Flags;
        public uint Size;
        public uint MaxLookahead;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MftOutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType type);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object created);

    [ComImport]
    [Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes other, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, [Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, uint size, out uint written);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport]
    [Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaType : IMFAttributes
    {
        // IMFAttributes, again, in vtable order.
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IMFAttributes other, int matchType, out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] new int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] new int GetString(ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] new int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] new int GetBlob(ref Guid key, [Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, uint size, out uint written);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, uint value);
        [PreserveSig] new int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, uint size);
        [PreserveSig] new int SetUnknown(ref Guid key, IntPtr value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out uint count);
        [PreserveSig] new int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes destination);

        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
        [PreserveSig] int GetRepresentation(Guid representation, out IntPtr value);
        [PreserveSig] int FreeRepresentation(Guid representation, IntPtr value);
    }

    [ComImport]
    [Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint length);
        [PreserveSig] int SetCurrentLength(uint length);
        [PreserveSig] int GetMaxLength(out uint length);
    }

    [ComImport]
    [Guid("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMF2DBuffer
    {
        [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
        [PreserveSig] int Unlock2D();
        [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
        [PreserveSig] int IsContiguousFormat(out bool contiguous);
        [PreserveSig] int GetContiguousLength(out uint length);
        [PreserveSig] int ContiguousCopyTo(IntPtr destination, uint length);
        [PreserveSig] int ContiguousCopyFrom(IntPtr source, uint length);
    }

    [ComImport]
    [Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSample : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] new int Compare(IMFAttributes other, int matchType, out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] new int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] new int GetString(ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] new int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] new int GetBlob(ref Guid key, [Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, uint size, out uint written);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, uint value);
        [PreserveSig] new int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, uint size);
        [PreserveSig] new int SetUnknown(ref Guid key, IntPtr value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out uint count);
        [PreserveSig] new int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes destination);

        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(uint index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport]
    [Guid("BF94C121-5B05-4E6F-8000-BA598961414D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out uint inputMinimum, out uint inputMaximum, out uint outputMinimum, out uint outputMaximum);
        [PreserveSig] int GetStreamCount(out uint inputStreams, out uint outputStreams);
        [PreserveSig] int GetStreamIDs(uint inputSize, [Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] uint[] inputIds, uint outputSize, [Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] uint[] outputIds);
        [PreserveSig] int GetInputStreamInfo(uint streamId, out MftInputStreamInfo info);
        [PreserveSig] int GetOutputStreamInfo(uint streamId, out MftOutputStreamInfo info);
        [PreserveSig] int GetAttributes(out IMFAttributes attributes);
        [PreserveSig] int GetInputStreamAttributes(uint streamId, out IMFAttributes attributes);
        [PreserveSig] int GetOutputStreamAttributes(uint streamId, out IMFAttributes attributes);
        [PreserveSig] int DeleteInputStream(uint streamId);
        [PreserveSig] int AddInputStreams(uint count, [In][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] uint[] ids);
        [PreserveSig] int GetInputAvailableType(uint streamId, uint index, out IMFMediaType type);
        [PreserveSig] int GetOutputAvailableType(uint streamId, uint index, out IMFMediaType type);
        [PreserveSig] int SetInputType(uint streamId, IMFMediaType? type, uint flags);
        [PreserveSig] int SetOutputType(uint streamId, IMFMediaType? type, uint flags);
        [PreserveSig] int GetInputCurrentType(uint streamId, out IMFMediaType type);
        [PreserveSig] int GetOutputCurrentType(uint streamId, out IMFMediaType type);
        [PreserveSig] int GetInputStatus(uint streamId, out uint flags);
        [PreserveSig] int GetOutputStatus(out uint flags);
        [PreserveSig] int SetOutputBounds(long lowerBound, long upperBound);
        [PreserveSig] int ProcessEvent(uint streamId, IntPtr @event);
        [PreserveSig] int ProcessMessage(uint message, UIntPtr parameter);
        [PreserveSig] int ProcessInput(uint streamId, IMFSample sample, uint flags);
        [PreserveSig] int ProcessOutput(uint flags, uint count, ref MftOutputDataBuffer buffer, out uint status);
    }

    /// <summary>Media Foundation time: 100-nanosecond units.</summary>
    public static long ToMfTime(MediaTime time) => time.IsInfinite ? 0 : time.Microseconds * 10;

    public static MediaTime FromMfTime(long time) => MediaTime.FromMicroseconds(time / 10);

    public static string Describe(int hr) => $"0x{hr:X8}";
}
