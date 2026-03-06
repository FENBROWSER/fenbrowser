namespace FenBrowser.Core.Security.Sandbox;

/// <summary>
/// Factory that creates <see cref="ISandbox"/> instances for a given
/// <see cref="OsSandboxProfile"/>.
/// </summary>
/// <remarks>
/// The factory is platform-specific: on Windows it produces
/// <see cref="Windows.WindowsJobObjectSandbox"/> instances; on unsupported platforms
/// it produces <see cref="NullSandbox"/> instances.  Callers should always check
/// <see cref="IsSandboxingSupported"/> before trusting that constraints are enforced.
/// </remarks>
public interface IOsSandboxFactory
{
    /// <summary>
    /// Gets a value indicating whether real OS-level sandboxing is available on the
    /// current host.
    /// </summary>
    /// <remarks>
    /// When <c>false</c>, <see cref="Create"/> returns a <see cref="NullSandbox"/> and
    /// no OS-level constraints are applied.  This is acceptable for development builds
    /// running in environments where Job Objects or equivalent primitives are unavailable
    /// (e.g. nested virtualisation without nested Job Object support).
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
