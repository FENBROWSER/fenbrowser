namespace FenBrowser.Core.Security.Sandbox;

/// <summary>
/// Factory that creates <see cref="ISandbox"/> instances for a given
/// <see cref="OsSandboxProfile"/>.
/// </summary>
/// <remarks>
/// The factory is platform-specific: on Windows it produces
/// <see cref="Windows.WindowsJobObjectSandbox"/> or AppContainer-backed instances; on
/// POSIX hosts it may produce helper-backed sandboxes or <see cref="NullSandbox"/>
/// depending on host support. Callers should always check
/// <see cref="IsSandboxingSupported"/> before trusting that constraints are enforced.
/// </remarks>
public interface IOsSandboxFactory
{
    /// <summary>
    /// Gets a value indicating whether real OS-level sandboxing is available on the
    /// current host.
    /// </summary>
    /// <remarks>
    /// When <c>false</c>, callers must treat the resulting launch as unsandboxed unless
    /// they explicitly reject it. Production launch paths should never silently assume
    /// that constraints are active when this is <c>false</c>.
    /// </remarks>
    bool IsSandboxingSupported { get; }

    /// <summary>
    /// Creates a new sandbox instance configured according to <paramref name="profile"/>.
    /// </summary>
    /// <param name="profile">
    /// The <see cref="OsSandboxProfile"/> that defines memory limits, UI restrictions,
    /// and capability set for the new sandbox.
    /// </param>
    /// <returns>
    /// An <see cref="ISandbox"/> ready to be applied to a child process via
    /// <see cref="ISandbox.ApplyToProcessStartInfo"/> and
    /// <see cref="ISandbox.AttachToProcess"/>.
    /// </returns>
    ISandbox Create(OsSandboxProfile profile);
}
