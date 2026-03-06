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
/// return immutable default profiles.  Callers that need non-default limits may construct
/// a new <see cref="OsSandboxProfile"/> directly.
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
        maxMemoryBytes: 512L * 1024 * 1024,          // 512 MiB
        maxCpuPercent: 80,
        denyDesktopAccess: true,
        denyWindowEnumeration: true,
        capabilities: OsSandboxCapabilities.RendererMinimal);

    /// <summary>Default profile for the network process.</summary>
    public static OsSandboxProfile NetworkProcess { get; } = new OsSandboxProfile(
        kind: OsSandboxProfileKind.NetworkProcess,
        maxMemoryBytes: 256L * 1024 * 1024,          // 256 MiB
        maxCpuPercent: 50,
        denyDesktopAccess: true,
        denyWindowEnumeration: true,
        capabilities: OsSandboxCapabilities.NetworkProcess);

    /// <summary>Default profile for the GPU process.</summary>
    public static OsSandboxProfile GpuProcess { get; } = new OsSandboxProfile(
        kind: OsSandboxProfileKind.GpuProcess,
        maxMemoryBytes: 1024L * 1024 * 1024,         // 1 GiB (GPU buffers can be large)
        maxCpuPercent: 60,
        denyDesktopAccess: true,
        denyWindowEnumeration: true,
        capabilities: OsSandboxCapabilities.GpuProcess);

    /// <summary>Default profile for utility processes (maximally restricted).</summary>
    public static OsSandboxProfile UtilityProcess { get; } = new OsSandboxProfile(
        kind: OsSandboxProfileKind.UtilityProcess,
        maxMemoryBytes: 256L * 1024 * 1024,          // 256 MiB
        maxCpuPercent: 25,
        denyDesktopAccess: true,
        denyWindowEnumeration: true,
        capabilities: OsSandboxCapabilities.None);

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    /// <summary>
    /// Initialises a new <see cref="OsSandboxProfile"/> with explicit parameters.
    /// </summary>
    /// <param name="kind">The logical profile kind that identifies the process type.</param>
    /// <param name="maxMemoryBytes">
    /// Maximum private working-set memory the process may consume, in bytes.
    /// Use <see cref="long.MaxValue"/> to indicate no limit (broker only).
    /// </param>
    /// <param name="maxCpuPercent">
    /// Soft CPU usage ceiling expressed as a percentage (0–100).
    /// Enforcement is best-effort via Job Object CPU rate control on Windows.
    /// </param>
    /// <param name="denyDesktopAccess">
    /// When <c>true</c>, the process is prevented from creating, opening, or
    /// enumerating desktops (Windows: <c>JOB_OBJECT_UILIMIT_DESKTOP</c>).
    /// </param>
    /// <param name="denyWindowEnumeration">
    /// When <c>true</c>, the process is prevented from enumerating top-level windows
    /// belonging to other processes or sessions.
    /// </param>
    /// <param name="capabilities">
    /// The bitfield of OS-level capabilities granted to this process type.
    /// </param>
    public OsSandboxProfile(
        OsSandboxProfileKind kind,
        long maxMemoryBytes,
        int maxCpuPercent,
        bool denyDesktopAccess,
        bool denyWindowEnumeration,
        OsSandboxCapabilities capabilities)
    {
        if (maxCpuPercent < 0 || maxCpuPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(maxCpuPercent), "Must be 0–100.");

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

    /// <summary>Gets the logical kind (process type) this profile represents.</summary>
    public OsSandboxProfileKind Kind { get; }

    /// <summary>
    /// Gets the maximum private working-set memory in bytes.
    /// <see cref="long.MaxValue"/> means unlimited (broker process only).
    /// </summary>
    public long MaxMemoryBytes { get; }

    /// <summary>
    /// Gets the maximum CPU usage as a whole-number percentage (0–100).
    /// </summary>
    public int MaxCpuPercent { get; }

    /// <summary>
    /// Gets a value indicating whether the process should be denied access to desktop
    /// objects (windows, message queues belonging to other sessions).
    /// </summary>
    public bool DenyDesktopAccess { get; }

    /// <summary>
    /// Gets a value indicating whether the process should be denied the ability to
    /// enumerate top-level windows of other processes.
    /// </summary>
    public bool DenyWindowEnumeration { get; }

    /// <summary>Gets the set of OS-level capabilities granted to this profile.</summary>
    public OsSandboxCapabilities Capabilities { get; }

    /// <summary>
    /// Returns a human-readable summary of the profile for logging and diagnostics.
    /// </summary>
    public override string ToString() =>
        $"OsSandboxProfile({Kind}, mem={MaxMemoryBytes / (1024 * 1024)} MiB, cpu={MaxCpuPercent}%, caps={Capabilities})";
}
