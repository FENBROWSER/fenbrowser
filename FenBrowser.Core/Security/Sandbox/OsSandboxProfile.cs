using System;

namespace FenBrowser.Core.Security.Sandbox;

/// <summary>
/// Identifies the named OS-level sandbox profile applied to a given browser process type.
/// </summary>
public enum OsSandboxProfileKind
{
    /// <summary>
    /// Browser / broker process: unrestricted, all OS capabilities.
    /// The broker is the trust anchor and is never sandboxed at the OS level.
    /// </summary>
    BrokerFull,

    /// <summary>
    /// Renderer process: maximally restricted.
    /// No network, no file I/O, no child-process spawning.
    /// All external communication flows through the broker IPC pipe.
    /// </summary>
    RendererMinimal,

    /// <summary>
    /// Network process: outbound HTTP/HTTPS only.
    /// No file write, no child processes, no GPU access.
    /// </summary>
    NetworkProcess,

    /// <summary>
    /// GPU process: GPU hardware access only.
    /// No network, no file write, no child processes.
    /// </summary>
    GpuProcess,

    /// <summary>
    /// Utility process: maximally restricted, task-specific.
    /// Used for PDF rendering, spell-check, codec workers, etc.
    /// </summary>
    UtilityProcess
}

/// <summary>
/// Encapsulates a complete OS-level sandbox configuration for a specific process type,
/// including resource limits, UI restrictions, and the OS capability set.
/// </summary>
/// <remarks>
/// Static factory properties (<see cref="BrokerFull"/>, <see cref="RendererMinimal"/>,
/// <see cref="NetworkProcess"/>, <see cref="GpuProcess"/>, <see cref="UtilityProcess"/>)
/// return immutable default profiles. Callers that need non-default resource limits may
/// construct a new <see cref="OsSandboxProfile"/> directly, but a named restricted
/// profile cannot be widened beyond that process type's capability ceiling.
/// </remarks>
public sealed class OsSandboxProfile
{
    // -------------------------------------------------------------------------
    // Static default profiles
    // -------------------------------------------------------------------------

    /// <summary>Default profile for the browser/broker process (unrestricted).</summary>
    public static OsSandboxProfile BrokerFull { get; } = new OsSandboxProfile(
        kind: OsSandboxProfileKind.BrokerFull,
        maxMemoryBytes: long.MaxValue,
        maxCpuPercent: 100,
        denyDesktopAccess: false,
        denyWindowEnumeration: false,
        capabilities: OsSandboxCapabilities.BrokerFull);

    /// <summary>Default profile for renderer processes (maximally restricted).</summary>
    public static OsSandboxProfile RendererMinimal { get; } = new OsSandboxProfile(
        kind: OsSandboxProfileKind.RendererMinimal,
        maxMemoryBytes: 2048L * 1024 * 1024,
        maxCpuPercent: 80,
        denyDesktopAccess: true,
        denyWindowEnumeration: true,
        capabilities: OsSandboxCapabilities.RendererMinimal);

    /// <summary>Default profile for the network process.</summary>
    public static OsSandboxProfile NetworkProcess { get; } = new OsSandboxProfile(
        kind: OsSandboxProfileKind.NetworkProcess,
        maxMemoryBytes: 256L * 1024 * 1024,
        maxCpuPercent: 50,
        denyDesktopAccess: true,
        denyWindowEnumeration: true,
        capabilities: OsSandboxCapabilities.NetworkProcess);

    /// <summary>Default profile for the GPU process.</summary>
    public static OsSandboxProfile GpuProcess { get; } = new OsSandboxProfile(
        kind: OsSandboxProfileKind.GpuProcess,
        maxMemoryBytes: 1024L * 1024 * 1024,
        maxCpuPercent: 60,
        denyDesktopAccess: true,
        denyWindowEnumeration: true,
        capabilities: OsSandboxCapabilities.GpuProcess);

    /// <summary>Default profile for utility processes (maximally restricted).</summary>
    public static OsSandboxProfile UtilityProcess { get; } = new OsSandboxProfile(
        kind: OsSandboxProfileKind.UtilityProcess,
        maxMemoryBytes: 256L * 1024 * 1024,
        maxCpuPercent: 25,
        denyDesktopAccess: true,
        denyWindowEnumeration: true,
        capabilities: OsSandboxCapabilities.None);

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public OsSandboxProfile(
        OsSandboxProfileKind kind,
        long maxMemoryBytes,
        int maxCpuPercent,
        bool denyDesktopAccess,
        bool denyWindowEnumeration,
        OsSandboxCapabilities capabilities)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), "Unknown sandbox profile kind.");
        if (maxMemoryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMemoryBytes), "Memory limit must be positive.");
        if (maxCpuPercent < 0 || maxCpuPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(maxCpuPercent), "Must be 0–100.");

        var knownCapabilities = OsSandboxCapabilities.BrokerFull;
        if ((capabilities & ~knownCapabilities) != 0)
            throw new ArgumentOutOfRangeException(nameof(capabilities), "Sandbox profile contains unknown capability bits.");

        var capabilityCeiling = GetCapabilityCeiling(kind);
        if ((capabilities & ~capabilityCeiling) != 0)
        {
            throw new ArgumentException(
                $"Profile '{kind}' cannot be widened to capabilities '{capabilities}'. " +
                $"Maximum allowed capabilities: '{capabilityCeiling}'.",
                nameof(capabilities));
        }

        Kind = kind;
        MaxMemoryBytes = maxMemoryBytes;
        MaxCpuPercent = maxCpuPercent;
        DenyDesktopAccess = denyDesktopAccess;
        DenyWindowEnumeration = denyWindowEnumeration;
        Capabilities = capabilities;
    }

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------

    public OsSandboxProfileKind Kind { get; }
    public long MaxMemoryBytes { get; }
    public int MaxCpuPercent { get; }
    public bool DenyDesktopAccess { get; }
    public bool DenyWindowEnumeration { get; }
    public OsSandboxCapabilities Capabilities { get; }

    public override string ToString() =>
        $"OsSandboxProfile({Kind}, mem={MaxMemoryBytes / (1024 * 1024)} MiB, cpu={MaxCpuPercent}%, caps={Capabilities})";

    private static OsSandboxCapabilities GetCapabilityCeiling(OsSandboxProfileKind kind) => kind switch
    {
        OsSandboxProfileKind.BrokerFull => OsSandboxCapabilities.BrokerFull,
        OsSandboxProfileKind.RendererMinimal => OsSandboxCapabilities.RendererMinimal,
        OsSandboxProfileKind.NetworkProcess => OsSandboxCapabilities.NetworkProcess,
        OsSandboxProfileKind.GpuProcess => OsSandboxCapabilities.GpuProcess,
        OsSandboxProfileKind.UtilityProcess => OsSandboxCapabilities.None,
        _ => OsSandboxCapabilities.None
    };
}
