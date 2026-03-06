using System;
using System.Runtime.Versioning;

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
///       <see cref="WindowsAppContainerSandbox"/> — AppContainer token restriction
///       combined with a Job Object for resource limits.
///     </description>
///   </item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsOsSandboxFactory : IOsSandboxFactory
{
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

        // All child process types use AppContainer + Job Object.
        return new WindowsAppContainerSandbox(profile);
    }
}
