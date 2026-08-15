using System;
using System.Diagnostics;

namespace FenBrowser.Core.Security.Sandbox;

/// <summary>
/// A no-op <see cref="ISandbox"/> implementation used only when an explicitly
/// authorized unsandboxed fallback is required (or by tests).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NullSandbox"/> applies no OS-level constraints whatsoever. Its
/// <see cref="Capabilities"/> therefore reports the effective unrestricted process
/// surface rather than echoing the requested profile and pretending those restrictions
/// were enforced.
/// </para>
/// </remarks>
public sealed class NullSandbox : ISandbox
{
    private readonly OsSandboxProfile _profile;

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

    public string ProfileName => $"Null({_profile.Kind})";

    /// <summary>
    /// A no-op sandbox grants no restrictions. Report the effective unrestricted
    /// process surface instead of the desired profile's capability set; consumers must
    /// never infer that RendererMinimal/NetworkProcess restrictions are active here.
    /// </summary>
    public OsSandboxCapabilities Capabilities => OsSandboxCapabilities.BrokerFull;

    public bool IsActive => false;

    public void ApplyToProcessStartInfo(ProcessStartInfo psi)
    {
        ArgumentNullException.ThrowIfNull(psi);
        // Intentionally no-op: NullSandbox cannot enforce any constraints.
    }

    public void AttachToProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        // Intentionally no-op.
    }

    public void Kill()
    {
        // Intentionally no-op: there is no sandbox-owned process group to terminate.
    }

    public bool RequiresCustomSpawn => false;

    public Process SpawnProcess(ProcessStartInfo psi)
    {
        ArgumentNullException.ThrowIfNull(psi);
        throw new NotSupportedException(
            "NullSandbox does not support custom process spawning. " +
            "Use Process.Start directly when RequiresCustomSpawn is false.");
    }

    public SandboxHealthStatus GetHealth()
    {
        return new SandboxHealthStatus
        {
            IsHealthy = false,
            Reason = "NullSandbox: no OS-level isolation is active; effective process capabilities are unrestricted.",
            MemoryUsageBytes = 0,
            ActiveProcessCount = 0
        };
    }

    public void Dispose()
    {
        // Intentionally no-op: NullSandbox owns no native resources.
    }
}
