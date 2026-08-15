using System;
using FenBrowser.Core.Platform;

namespace FenBrowser.Core.Security.Sandbox.Posix;

/// <summary>
/// POSIX sandbox factory that uses native launcher helpers when available.
/// Linux uses <c>bwrap</c> and macOS uses <c>sandbox-exec</c>.
/// </summary>
public sealed class PosixOsSandboxFactory : IOsSandboxFactory
{
    private readonly OSPlatformKind _platform;
    private readonly string _helperPath;
    private readonly bool _allowUnsandboxedDevelopmentFallback;

    public PosixOsSandboxFactory(OSPlatformKind platform)
    {
        _platform = platform;
        _helperPath = PosixCommandSandbox.TryResolveHelper(platform);

        // Restricted browser child processes must not silently lose their OS sandbox
        // just because the helper is absent from PATH. Development builds that truly
        // need to run without the native boundary must opt in explicitly.
        _allowUnsandboxedDevelopmentFallback = string.Equals(
            Environment.GetEnvironmentVariable("FEN_ALLOW_UNSANDBOXED_POSIX"),
            "1",
            StringComparison.OrdinalIgnoreCase);
    }

    public bool IsSandboxingSupported => !string.IsNullOrWhiteSpace(_helperPath);

    public ISandbox Create(OsSandboxProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        // The broker intentionally owns the capabilities denied to renderer/network/
        // utility children, so BrokerFull is the only profile allowed to be native-
        // sandbox-free by construction.
        if (profile.Kind == OsSandboxProfileKind.BrokerFull)
        {
            return new NullSandbox(profile, suppressWarning: true);
        }

        if (!IsSandboxingSupported)
        {
            if (_allowUnsandboxedDevelopmentFallback)
            {
                return new NullSandbox(profile);
            }

            throw new InvalidOperationException(
                $"POSIX sandbox helper is unavailable for restricted profile '{profile.Kind}' on platform '{_platform}'. " +
                "FenBrowser will not launch the child unsandboxed. Install the platform sandbox helper, or set " +
                "FEN_ALLOW_UNSANDBOXED_POSIX=1 only for an explicitly unsandboxed development run.");
        }

        return new PosixCommandSandbox(profile, _platform, _helperPath);
    }
}
