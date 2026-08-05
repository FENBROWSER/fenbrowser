using System;
using System.Runtime.InteropServices;
using FenBrowser.Host.Platform;

namespace FenBrowser.Host.Platform;

/// <summary>
/// Factory for creating platform-specific hosts.
/// </summary>
public static class PlatformHostFactory
{
    private static IPlatformHost? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Creates or returns the platform host for the current runtime platform.
    /// </summary>
    public static IPlatformHost Create()
    {
        lock (_lock)
        {
            if (_instance != null)
                return _instance;

            var platform = GetPlatformType();
            _instance = platform switch
            {
                PlatformType.Windows => new FenBrowser.Host.Platform.Windows.WindowsPlatformHost(),
                PlatformType.Linux => new FenBrowser.Host.Platform.Linux.LinuxPlatformHost(),
                PlatformType.MacOS => new FenBrowser.Host.Platform.MacOS.MacOSPlatformHost(),
                _ => throw new PlatformNotSupportedException($"Platform {RuntimeInformation.OSDescription} is not supported.")
            };

            return _instance;
        }
    }

    /// <summary>
    /// Gets the current platform type.
    /// </summary>
    public static PlatformType GetPlatformType()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return PlatformType.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return PlatformType.Linux;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return PlatformType.MacOS;
        return PlatformType.Unknown;
    }

    /// <summary>
    /// Resets the factory (for testing).
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _instance = null;
        }
    }
}