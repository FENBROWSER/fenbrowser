namespace FenBrowser.Core.Platform;

/// <summary>
/// Identifies the host operating system detected at runtime.
/// </summary>
public enum OSPlatformKind
{
    /// <summary>Microsoft Windows (Win32/Win64).</summary>
    Windows,

    /// <summary>Linux kernel (any distribution).</summary>
    Linux,

    /// <summary>Apple macOS (formerly OS X).</summary>
    MacOS,

    /// <summary>Platform could not be identified.</summary>
    Unknown
}
