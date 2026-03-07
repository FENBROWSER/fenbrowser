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
    private readonly bool _requireSandboxHelper;

    public PosixOsSandboxFactory(OSPlatformKind platform)
    {
        _platform = platform;
        _helperPath = PosixCommandSandbox.TryResolveHelper(platform);
        _requireSandboxHelper = string.Equals(
            Environment.GetEnvironmentVariable("FEN_POSIX_SANDBOX_REQUIRED"),
            "1",
            StringComparison.OrdinalIgnoreCase);
    }

    public bool IsSandboxingSupported => !string.IsNullOrWhiteSpace(_helperPath);

    public ISandbox Create(OsSandboxProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        if (profile.Kind == OsSandboxProfileKind.BrokerFull)
        {
            return new NullSandbox(profile, suppressWarning: true);
        }

        if (!IsSandboxingSupported)
        {
            if (_requireSandboxHelper)
            {
                throw new InvalidOperationException(
                    $"POSIX sandbox helper is required for platform '{_platform}' but no helper executable was found in PATH.");
            }

            return new NullSandbox(profile);
        }

        return new PosixCommandSandbox(profile, _platform, _helperPath);
    }
}
