using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Security.Sandbox.Windows;

/// <summary>
/// Windows implementation of <see cref="IOsSandboxFactory"/> that selects the
/// appropriate sandbox type based on the requested <see cref="OsSandboxProfile"/>.
/// </summary>
/// <remarks>
/// <list type="table">
///   <listheader>
///     <term>Profile kind</term>
///     <description>Sandbox type returned</description>
///   </listheader>
///   <item>
///     <term><see cref="OsSandboxProfileKind.BrokerFull"/></term>
///     <description>
///       <see cref="NullSandbox"/> — the broker process is the trust root and must
///       not be restricted by an OS sandbox.
///     </description>
///   </item>
///   <item>
///     <term>All other kinds</term>
///     <description>
///       <see cref="WindowsAppContainerSandbox"/> when the AppContainer can
///       actually launch child processes on this machine; otherwise a
///       <see cref="WindowsJobObjectSandbox"/> (memory caps, CPU rate cap, UI
///       restrictions, kill-on-close) is used so the browser still runs with
///       process-level isolation.
///     </description>
///   </item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsOsSandboxFactory : IOsSandboxFactory
{
    private static readonly object s_appContainerProbeLock = new();
    private static bool s_appContainerProbed;
    private static bool s_appContainerViable;

    /// <inheritdoc/>
    public bool IsSandboxingSupported => true;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The broker (<see cref="OsSandboxProfileKind.BrokerFull"/>) receives a
    /// <see cref="NullSandbox"/> — no OS-level restrictions are applied.
    /// </para>
    /// <para>
    /// All other process types receive a <see cref="WindowsAppContainerSandbox"/> which
    /// combines an AppContainer token with a Job Object.  This provides both token-level
    /// isolation (no network, no file system, no child-process spawning) and
    /// resource-level limits (memory cap, CPU rate cap, UI restrictions).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="profile"/> is <c>null</c>.
    /// </exception>
    public ISandbox Create(OsSandboxProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        if (profile.Kind == OsSandboxProfileKind.BrokerFull)
        {
            // Broker is the unrestricted trust root — apply no OS sandbox.
            return new NullSandbox(profile, suppressWarning: true);
        }

        // AppContainer launch requires the package SID to have execute access to
        // the target binary. On machines where CreateProcessW inside an
        // AppContainer fails (ERROR_FILE_NOT_FOUND is how the OS hides the
        // missing ACL), fall back to the Job Object sandbox — still real
        // process isolation (memory cap, CPU rate cap, UI restrictions,
        // kill-on-close) — rather than silently running unsandboxed.
        if (AppContainerIsViable())
        {
            return new WindowsAppContainerSandbox(profile);
        }

        return new WindowsJobObjectSandbox(profile);
    }

    private static bool AppContainerIsViable()
    {
        lock (s_appContainerProbeLock)
        {
            if (s_appContainerProbed)
            {
                return s_appContainerViable;
            }

            s_appContainerProbed = true;
            s_appContainerViable = false;

            try
            {
                // Probe with a system binary (System32 is world-executable, so a
                // failure here means the AppContainer path itself is broken, not
                // the target's ACL). Use a throwaway profile so the probe never
                // touches the real renderer profile.
                using var sandbox = new WindowsAppContainerSandbox(OsSandboxProfile.GpuProcess);
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.SystemDirectory + @"\cmd.exe",
                    Arguments = "/c exit 0",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var probe = sandbox.SpawnProcess(psi);
                probe.WaitForExit(3000);
                s_appContainerViable = true;
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[SandboxFactory] AppContainer spawn probe succeeded (pid={probe.Id}).");
            }
            catch (Exception ex)
            {
                s_appContainerViable = false;
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                    $"[SandboxFactory] AppContainer is not viable on this machine; falling back to Job Object sandbox for child processes. {ex.GetType().Name}: {ex.Message}");
            }

            return s_appContainerViable;
        }
    }
}
