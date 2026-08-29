using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FenBrowser.Host.ProcessIsolation;

internal static class RendererChildEnvironment
{
    private static readonly string[] SafeInheritedKeys =
    {
        "SystemRoot",
        "WINDIR",
        "DOTNET_ROOT",
        "DOTNET_ROOT(x86)",
        // Diagnostics-only toggles; the renderer child writes probes/dumps to the
        // workspace logs dir, so it needs the same envelope as the host process.
        "FEN_DIAGNOSTIC_APPENDS",
        "FEN_DIAGNOSTICS_DIR"
    };

    private static readonly string[] UnixGraphicsEnvironmentKeys =
    {
        "DISPLAY",
        "WAYLAND_DISPLAY",
        "XDG_RUNTIME_DIR",
        "XAUTHORITY"
    };

    public static void ResetToSafeBase(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var safeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SafeInheritedKeys)
        {
            Preserve(startInfo, safeValues, key);
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var key in UnixGraphicsEnvironmentKeys)
            {
                Preserve(startInfo, safeValues, key);
            }
        }

        startInfo.Environment.Clear();
        foreach (var pair in safeValues)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    private static void Preserve(
        ProcessStartInfo startInfo,
        Dictionary<string, string> safeValues,
        string key)
    {
        if (startInfo.Environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            safeValues[key] = value;
        }
    }
}
