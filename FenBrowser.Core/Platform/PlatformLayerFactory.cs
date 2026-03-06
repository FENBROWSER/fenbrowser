using System;
using System.Runtime.InteropServices;
using FenBrowser.Core.Platform.Windows;
using FenBrowser.Core.Security.Sandbox;

namespace FenBrowser.Core.Platform;

/// <summary>
/// Singleton factory that detects the host operating system and returns the
/// appropriate <see cref="IPlatformLayer"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// The singleton is lazily initialised on first access and is safe for concurrent use.
/// The implementation is selected once at process startup; no dynamic switching occurs.
/// </para>
/// <para>
/// Currently supported platforms:
/// <list type="table">
///   <listheader>
///     <term>OS</term>
///     <description>Implementation</description>
///   </listheader>
///   <item>
///     <term>Windows</term>
///     <description><see cref="WindowsPlatformLayer"/></description>
///   </item>
///   <item>
///     <term>Linux / macOS</term>
///     <description><see cref="UnsupportedPlatformLayer"/> (throws on most operations)</description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public static class PlatformLayerFactory
{
    private static readonly Lazy<IPlatformLayer> _instance =
        new Lazy<IPlatformLayer>(Create, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Returns the singleton <see cref="IPlatformLayer"/> for the current host OS.
    /// </summary>
    public static IPlatformLayer GetInstance() => _instance.Value;

    // =========================================================================
    //  Private factory method
    // =========================================================================

    private static IPlatformLayer Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsPlatformLayer();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new UnsupportedPlatformLayer(OSPlatformKind.Linux);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new UnsupportedPlatformLayer(OSPlatformKind.MacOS);

        return new UnsupportedPlatformLayer(OSPlatformKind.Unknown);
    }
}

// =============================================================================
//  Stub implementation for unsupported platforms
// =============================================================================

/// <summary>
/// Placeholder <see cref="IPlatformLayer"/> for platforms that do not yet have a
/// full implementation (Linux, macOS).  All operations that require platform support
/// throw <see cref="PlatformNotSupportedException"/>.
/// </summary>
internal sealed class UnsupportedPlatformLayer : IPlatformLayer
{
    private readonly OSPlatformKind _kind;

    internal UnsupportedPlatformLayer(OSPlatformKind kind)
    {
        _kind = kind;
    }

    /// <inheritdoc/>
    public OSPlatformKind Platform => _kind;

    /// <inheritdoc/>
    public bool IsSupported => false;

    /// <inheritdoc/>
    public ISharedMemoryRegion CreateSharedMemory(string name, int sizeBytes)
    {
        // MemoryMappedFile works on Linux/macOS too; this is a best-effort attempt
        // without Global\ prefix (which is Windows-only).
        throw new PlatformNotSupportedException(
            $"CreateSharedMemory is not yet implemented for platform '{_kind}'. " +
            "A Linux/macOS PAL implementation is planned for a future commit.");
    }

    /// <inheritdoc/>
    public ISharedMemoryRegion OpenSharedMemory(string name, int sizeBytes)
    {
        throw new PlatformNotSupportedException(
            $"OpenSharedMemory is not yet implemented for platform '{_kind}'.");
    }

    /// <inheritdoc/>
    public System.Diagnostics.ProcessStartInfo ApplySandbox(
        System.Diagnostics.ProcessStartInfo psi,
        Security.Sandbox.OsSandboxProfile profile)
    {
        // Return PSI unmodified; no sandboxing applied.
        return psi;
    }

    /// <inheritdoc/>
    public void SelfRestrictToProfile(Security.Sandbox.OsSandboxProfile profile)
    {
        // No-op on unsupported platforms.
    }

    /// <inheritdoc/>
    public long GetProcessMemoryBytes(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            proc.Refresh();
            return proc.WorkingSet64;
        }
        catch
        {
            return -1;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a <see cref="NullSandboxFactory"/> because sandboxing is not supported
    /// on this platform.  All created sandboxes will be no-ops.
    /// </remarks>
    public IOsSandboxFactory CreateSandboxFactory()
    {
        return new NullSandboxFactory();
    }
}

// =============================================================================
//  Null sandbox factory for unsupported platforms
// =============================================================================

/// <summary>
/// <see cref="IOsSandboxFactory"/> stub used on platforms where no real sandboxing
/// implementation is available.  Always returns <see cref="NullSandbox"/> instances.
/// </summary>
internal sealed class NullSandboxFactory : IOsSandboxFactory
{
    /// <inheritdoc/>
    public bool IsSandboxingSupported => false;

    /// <inheritdoc/>
    public ISandbox Create(OsSandboxProfile profile)
    {
        return new NullSandbox(profile);
    }
}
