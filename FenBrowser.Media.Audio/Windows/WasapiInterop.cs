using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FenBrowser.Media.Audio.Windows;

/// <summary>
/// The slice of the Core Audio COM surface WASAPI playback needs (mmdeviceapi.h,
/// audioclient.h). Declared by hand: only the methods used, in vtable order.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WasapiInterop
{
    public const int DeviceStateActive = 0x00000001;
    public const int FlowRender = 0;         // EDataFlow::eRender
    public const int RoleConsole = 0;        // ERole::eConsole
    public const int ClsctxAll = 0x17;
    public const int StgmRead = 0;
    public const int ShareModeShared = 0;
    public const int StreamFlagsEventCallback = 0x00040000;
    public const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
    public const int StreamFlagsSrcDefaultQuality = 0x08000000;
    public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    public const ushort WaveFormatPcm = 1;
    public const ushort WaveFormatIeeeFloat = 3;
    public const ushort WaveFormatExtensibleTag = 0xFFFE;

    public static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IidIAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IidIAudioClock = new("CD63314F-3FBA-4A1B-812C-EF96358728E7");
    public static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
        int OpenPropertyStore(int access, out IPropertyStore properties);
        int GetId(out IntPtr id);
        int GetState(out int state);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        int GetCount(out uint count);
        int Item(uint index, out IMMDevice device);
    }

    /// <summary>
    /// The one property read off an endpoint: its friendly name. A PROPVARIANT comes back,
    /// which for this key holds a VT_LPWSTR the caller frees.
    /// </summary>
    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PropertyKey key);
        int GetValue(ref PropertyKey key, IntPtr value);
        int SetValue(ref PropertyKey key, IntPtr value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>PKEY_Device_FriendlyName (functiondiscoverykeys_devpkey.h).</summary>
    public static readonly PropertyKey PkeyDeviceFriendlyName = new()
    {
        FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        PropertyId = 14,
    };

    /// <summary>The head of a PROPVARIANT: the type tag, then the pointer for VT_LPWSTR.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariantHead
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Value;
    }

    public const ushort VtLpwstr = 31;

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(IntPtr variant);

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out uint frames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint frames);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        int GetBuffer(uint frames, out IntPtr data);
        int ReleaseBuffer(uint framesWritten, int flags);
    }

    [ComImport]
    [Guid("CD63314F-3FBA-4A1B-812C-EF96358728E7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClock
    {
        int GetFrequency(out ulong frequency);
        int GetPosition(out ulong position, out ulong qpcPosition);
        int GetCharacteristics(out uint characteristics);
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorComObject
    {
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
