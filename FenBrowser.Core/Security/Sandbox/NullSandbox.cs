using System;
using System.Diagnostics;

namespace FenBrowser.Core.Security.Sandbox;

/// <summary>
/// A no-op <see cref="ISandbox"/> implementation used when:
/// <list type="bullet">
///   <item>The browser is running in-process (single-process mode for testing).</item>
///   <item>The host OS does not support the required sandboxing primitives.</item>
///   <item><see cref="IOsSandboxFactory.IsSandboxingSupported"/> returns <c>false</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NullSandbox"/> applies <em>no OS-level constraints whatsoever</em>.
/// A warning is emitted via the console the first time an instance is created in a
/// non-test context.  Production deployments should ensure that a real sandbox
/// implementation is in use for all renderer and utility processes.
/// </para>
/// </remarks>
public sealed class NullSandbox : ISandbox
{
    private readonly OsSandboxProfile _profile;
    private bool _disposed;

    /// <summary>
    /// Initialises a new <see cref="NullSandbox"/> for the given profile.
    /// </summary>
    /// <param name="profile">The profile this sandbox nominally represents.</param>
    /// <param name="suppressWarning">
    /// Pass <c>true</c> to suppress the console warning.
    /// Useful in unit tests that deliberately use a null sandbox.
    /// </param>
    public NullSandbox(OsSandboxProfile profile, bool suppressWarning = false)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        if (!suppressWarning && profile.Kind != OsSandboxProfileKind.BrokerFull)
        {
            Console.Error.WriteLine(
                $"[FenBrowser.Security] WARNING: NullSandbox is active for profile '{profile.Kind}'. " +
                "No OS-level process isolation is being enforced. " +
                "Do NOT use NullSandbox in production renderer processes.");
        }
    }

    // =========================================================================
    //  ISandbox
    // =========================================================================

    /// <inheritdoc/>
    public string ProfileName => $"Null({_profile.Kind})";

    /// <inheritdoc/>
    public OsSandboxCapabilities Capabilities => _profile.Capabilities;

    /// <inheritdoc/>
    /// <remarks>
    /// Always returns <c>false</c> for <see cref="NullSandbox"/>: the sandbox has no
    /// active OS constructs and therefore no active enforcement.
    /// </remarks>
    public bool IsActive => false;

    /// <inheritdoc/>
    /// <remarks>No-op: the <see cref="ProcessStartInfo"/> is not modified.</remarks>
    public void ApplyToProcessStartInfo(ProcessStartInfo psi)
    {
        // Intentionally a no-op: NullSandbox cannot enforce any constraints.
    }

    /// <inheritdoc/>
    /// <remarks>No-op: the process is not assigned to any Job Object or cgroup.</remarks>
    public void AttachToProcess(Process process)
    {
        // Intentionally a no-op.
    }

    /// <inheritdoc/>
    /// <remarks>No-op: there is no underlying OS primitive to terminate.</remarks>
    public void Kill()
    {
        // Intentionally a no-op.
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a <see cref="SandboxHealthStatus"/> indicating that the sandbox is
    /// unhealthy (because it is not enforcing any constraints).
    /// </remarks>
    public SandboxHealthStatus GetHealth()
    {
        return new SandboxHealthStatus
        {
            IsHealthy = false,
            Reason = "NullSandbox: no OS-level isolation is active.",
            MemoryUsageBytes = 0,
            ActiveProcessCount = 0
        };
    }

    // =========================================================================
    //  IDisposable
    // =========================================================================

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
    }
}
