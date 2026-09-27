using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FenBrowser.Media.Gpu.Windows;
using static FenBrowser.Media.Codecs.MediaFoundation.MfInterop;

namespace FenBrowser.Media.Codecs.MediaFoundation;

/// <summary>
/// The process's Direct3D 11 device wrapped in the DXGI device manager Media Foundation
/// transforms take (design §5, M7). Created on first use; when there is no device, or the
/// hardware kill switch is set, the transforms decode in software as before.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MfGpuDevice
{
    private static readonly Lock s_gate = new();
    private static bool s_tried;
    private static IMFDXGIDeviceManager? s_manager;
    private static D3D11VideoDevice? s_device;
    private static string s_reason = string.Empty;

    /// <summary>Why there is no GPU path, or empty when there is one.</summary>
    public static string Reason
    {
        get
        {
            _ = TryGet(out _, out _);
            return s_reason;
        }
    }

    public static bool IsAvailable => TryGet(out _, out _);

    public static bool TryGet([NotNullWhen(true)] out IMFDXGIDeviceManager? manager, [NotNullWhen(true)] out D3D11VideoDevice? device)
    {
        lock (s_gate)
        {
            if (!s_tried)
            {
                s_tried = true;
                Create();
            }

            manager = s_manager;
            device = s_device;
            return manager is not null && device is not null;
        }
    }

    private static void Create()
    {
        var device = D3D11VideoDevice.TryGetShared(out var reason);
        if (device is null)
        {
            s_reason = reason;
            return;
        }

        int hr = MFCreateDXGIDeviceManager(out uint token, out var manager);
        if (hr < 0 || manager is null)
        {
            s_reason = $"MFCreateDXGIDeviceManager failed: {Describe(hr)}";
            return;
        }

        hr = manager.ResetDevice(device.Device, token);
        if (hr < 0)
        {
            Marshal.ReleaseComObject(manager);
            s_reason = $"IMFDXGIDeviceManager::ResetDevice failed: {Describe(hr)}";
            return;
        }

        s_manager = manager;
        s_device = device;
        s_reason = string.Empty;
    }
}
