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
///     <description><see cref="PosixPlatformLayer"/> (real shared-memory/process PAL with native helper-backed sandbox factory when available)</description>
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
            return new PosixPlatformLayer(OSPlatformKind.Linux);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new PosixPlatformLayer(OSPlatformKind.MacOS);

        return new UnsupportedPlatformLayer(OSPlatformKind.Unknown);
    }
}

// =============================================================================
//  Unsupported-platform implementation
// =============================================================================

/// <summary>
/// Fail-closed <see cref="IPlatformLayer"/> for platforms without an implementation.
/// Operations that require platform isolation or shared-memory support throw instead
/// of silently continuing without the requested security boundary.
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
        throw Unsupported("CreateSharedMemory");
    }

    /// <inheritdoc/>
    public ISharedMemoryRegion OpenSharedMemory(string name, int sizeBytes)
    {
        throw Unsupported("OpenSharedMemory");
    }

    /// <inheritdoc/>
    public System.Diagnostics.ProcessStartInfo ApplySandbox(
        System.Diagnostics.ProcessStartInfo psi,
        Security.Sandbox.OsSandboxProfile profile)
    {
        throw Unsupported("ApplySandbox");
    }

    /// <inheritdoc/>
    public void SelfRestrictToProfile(Security.Sandbox.OsSandboxProfile profile)
    {
        throw Unsupported("SelfRestrictToProfile");
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
    public IOsSandboxFactory CreateSandboxFactory()
    {
        throw Unsupported("CreateSandboxFactory");
    }

    private PlatformNotSupportedException Unsupported(string operation) =>
        new($"{operation} is unavailable on unsupported platform '{_kind}'. FenBrowser will not continue with a no-op sandbox fallback.");
}
