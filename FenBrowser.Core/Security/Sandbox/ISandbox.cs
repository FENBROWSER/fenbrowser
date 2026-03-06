using System;
using System.Diagnostics;

namespace FenBrowser.Core.Security.Sandbox;

/// <summary>
/// Represents an OS-level process sandbox (Windows Job Object, Linux seccomp/namespaces,
/// macOS App Sandbox) produced by an <see cref="IOsSandboxFactory"/> and applied to child
/// processes before they are spawned.
/// </summary>
/// <remarks>
/// Typical lifecycle:
/// <list type="number">
///   <item>Call <see cref="IOsSandboxFactory.Create"/> to obtain an <see cref="ISandbox"/>.</item>
///   <item>Call <see cref="ApplyToProcessStartInfo"/> with the child's <see cref="ProcessStartInfo"/>.</item>
///   <item>Start the child process.</item>
///   <item>Call <see cref="AttachToProcess"/> with the started <see cref="Process"/> object.</item>
///   <item>Dispose the sandbox when the child exits (or call <see cref="Kill"/> to force termination).</item>
/// </list>
/// </remarks>
public interface ISandbox : IDisposable
{
    /// <summary>Gets the human-readable name of the sandbox profile in use.</summary>
    string ProfileName { get; }

    /// <summary>Gets the set of OS capabilities granted to processes inside this sandbox.</summary>
    OsSandboxCapabilities Capabilities { get; }

    /// <summary>
    /// Gets a value indicating whether the sandbox is currently active and enforcing
    /// constraints.  Returns <c>false</c> after <see cref="IDisposable.Dispose"/> is called
    /// or for a <see cref="NullSandbox"/> (in-process / unsupported platform).
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Modifies <paramref name="psi"/> so that the child process started from it will run
    /// inside this sandbox.
    /// </summary>
    /// <param name="psi">
    /// The <see cref="ProcessStartInfo"/> to modify.  Must not be <c>null</c>.
    /// </param>
    void ApplyToProcessStartInfo(ProcessStartInfo psi);

    /// <summary>
    /// Post-spawn hook: assigns the newly-started <paramref name="process"/> to any Job
    /// Object or cgroup limits managed by this sandbox.
    /// </summary>
    /// <remarks>
    /// Must be called before any untrusted code executes in the child.
    /// On Windows this calls <c>AssignProcessToJobObject</c> immediately after
    /// <see cref="Process.Start"/>.
    /// </remarks>
    /// <param name="process">
    /// The process that has been started.  Must not be <c>null</c>.
    /// </param>
    void AttachToProcess(Process process);

    /// <summary>
    /// Forcibly terminates all processes that have been assigned to this sandbox.
    /// </summary>
    /// <remarks>
    /// On Windows this terminates the entire Job Object.  This is a non-recoverable
    /// operation; call it only when the renderer needs to be killed (e.g. on navigation
    /// abort or memory-limit breach).
    /// </remarks>
    void Kill();

    /// <summary>Queries the current health and resource usage of the sandbox.</summary>
    /// <returns>A snapshot of sandbox health metrics.</returns>
    SandboxHealthStatus GetHealth();

    /// <summary>
    /// Gets a value indicating whether the sandbox must spawn the child process itself
    /// rather than relying on <see cref="System.Diagnostics.Process.Start"/>.
    /// </summary>
    /// <remarks>
    /// This is <c>true</c> for the Windows AppContainer sandbox because AppContainer
    /// process creation requires a <c>PROC_THREAD_ATTRIBUTE_LIST</c> that cannot be
    /// attached via <see cref="System.Diagnostics.ProcessStartInfo"/>.  When this
    /// property is <c>true</c>, callers must use <see cref="SpawnProcess"/> instead of
    /// <see cref="System.Diagnostics.Process.Start"/>.
    /// </remarks>
    bool RequiresCustomSpawn { get; }

    /// <summary>
    /// Spawns the child process inside the sandbox boundary.
    /// </summary>
    /// <remarks>
    /// Only valid when <see cref="RequiresCustomSpawn"/> is <c>true</c>.
    /// Implementations that do not require custom spawning should throw
    /// <see cref="NotSupportedException"/>.
    /// </remarks>
    /// <param name="psi">
    /// The <see cref="ProcessStartInfo"/> describing the child executable and arguments.
    /// </param>
    /// <returns>
    /// A <see cref="System.Diagnostics.Process"/> object representing the spawned child.
    /// The caller owns the returned process and must dispose it when done.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when <see cref="RequiresCustomSpawn"/> is <c>false</c>.
    /// </exception>
    System.Diagnostics.Process SpawnProcess(ProcessStartInfo psi);
}

/// <summary>
/// Snapshot of health and resource-usage metrics for an <see cref="ISandbox"/>.
/// </summary>
public sealed class SandboxHealthStatus
{
    /// <summary>Gets a value indicating whether the sandbox is operating normally.</summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Gets a human-readable reason string when <see cref="IsHealthy"/> is <c>false</c>,
    /// or an empty string when healthy.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets the total private working-set memory usage across all processes in the sandbox, in bytes.</summary>
    public long MemoryUsageBytes { get; init; }

    /// <summary>Gets the number of live processes currently assigned to this sandbox.</summary>
    public int ActiveProcessCount { get; init; }
}
