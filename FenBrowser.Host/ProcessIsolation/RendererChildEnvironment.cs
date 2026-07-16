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
        "DOTNET_ROOT(x86)"
    };

    public static void ResetToSafeBase(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var safeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SafeInheritedKeys)
        {
            if (startInfo.Environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                safeValues[key] = value;
            }
        }

        startInfo.Environment.Clear();
        foreach (var pair in safeValues)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }
}
