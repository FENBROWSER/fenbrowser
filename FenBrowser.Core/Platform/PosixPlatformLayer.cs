using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FenBrowser.Core.Security.Sandbox;
using FenBrowser.Core.Security.Sandbox.Posix;

namespace FenBrowser.Core.Platform;

/// <summary>
/// Linux/macOS platform layer that provides baseline shared-memory and process
/// PAL functionality without Win32-specific primitives.
/// </summary>
public sealed class PosixPlatformLayer : IPlatformLayer
{
    private readonly OSPlatformKind _platform;
    private readonly PosixOsSandboxFactory _sandboxFactory;

    public PosixPlatformLayer(OSPlatformKind platform)
    {
        _platform = platform;
        _sandboxFactory = new PosixOsSandboxFactory(platform);
    }

    public OSPlatformKind Platform => _platform;

    public bool IsSupported =>
        IsRunningOnTargetPlatform() && _sandboxFactory.IsSandboxingSupported;

    public ISharedMemoryRegion CreateSharedMemory(string name, int sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be null or whitespace.", nameof(name));
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size must be positive.");

        return new CrossPlatformSharedMemoryRegion(name, sizeBytes);
    }

    public ISharedMemoryRegion OpenSharedMemory(string name, int sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be null or whitespace.", nameof(name));
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size must be positive.");

        return CrossPlatformSharedMemoryRegion.Open(name, sizeBytes);
    }

    public ProcessStartInfo ApplySandbox(ProcessStartInfo psi, OsSandboxProfile profile)
    {
        if (psi == null) throw new ArgumentNullException(nameof(psi));
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        psi.UseShellExecute = false;
        if (profile.DenyDesktopAccess)
            psi.CreateNoWindow = true;

        return psi;
    }

    public void SelfRestrictToProfile(OsSandboxProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        if (profile.Kind == OsSandboxProfileKind.BrokerFull)
        {
            // The broker is intentionally the capability-owning process.
            return;
        }

        // Restricted POSIX children are launched through PosixCommandSandbox so the
        // helper establishes namespaces / sandbox-exec policy before untrusted code
        // runs. This PAL does not currently have an in-process one-way restriction
        // primitive equivalent to that launcher. Silently returning here falsely
        // advertised a security boundary that had not been established.
        throw new PlatformNotSupportedException(
            $"POSIX in-process self-restriction is unavailable for profile '{profile.Kind}'. " +
            "Launch restricted children through the POSIX sandbox factory instead.");
    }

    public long GetProcessMemoryBytes(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Refresh();
            return proc.WorkingSet64;
        }
        catch (ArgumentException)
        {
            return -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }

    public IOsSandboxFactory CreateSandboxFactory()
    {
        return _sandboxFactory;
    }

    private bool IsRunningOnTargetPlatform()
    {
        return (_platform == OSPlatformKind.Linux && RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) ||
               (_platform == OSPlatformKind.MacOS && RuntimeInformation.IsOSPlatform(OSPlatform.OSX));
    }
}
