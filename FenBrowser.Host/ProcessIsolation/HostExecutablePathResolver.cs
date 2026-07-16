using System;
using System.Diagnostics;
using System.IO;

namespace FenBrowser.Host.ProcessIsolation;

internal static class HostExecutablePathResolver
{
    public static string? Resolve()
    {
        // Environment.ProcessPath belongs to the embedding process. Under test runners
        // and managed hosts that can be testhost/dotnet rather than FenBrowser.Host.
        // Prefer the apphost emitted beside the loaded Host assembly so renderer-child
        // arguments always enter FenBrowser's startup-mode dispatcher.
        var hostAssemblyPath = typeof(global::FenBrowser.Host.Program).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(hostAssemblyPath))
        {
            var directory = Path.GetDirectoryName(hostAssemblyPath);
            var appHostName = OperatingSystem.IsWindows() ? "FenBrowser.Host.exe" : "FenBrowser.Host";
            var appHostPath = string.IsNullOrWhiteSpace(directory)
                ? null
                : Path.Combine(directory, appHostName);

            if (!string.IsNullOrWhiteSpace(appHostPath) && File.Exists(appHostPath))
            {
                return appHostPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        return Process.GetCurrentProcess().MainModule?.FileName;
    }
}
