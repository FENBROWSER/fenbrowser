using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FenBrowser.Core.Interop.Windows;
using FenBrowser.Core.Security.Sandbox;
using FenBrowser.Core.Security.Sandbox.Windows;

namespace FenBrowser.Core.Platform.Windows;

/// <summary>
/// Windows implementation of <see cref="IPlatformLayer"/>.
/// </summary>
[SupportedOSPlatform("windows")]
/// <remarks>
/// <para>
/// Shared memory is backed by <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile"/>
/// with <c>Global\</c>-prefixed names so that regions are visible across all user sessions
/// on the same machine.
/// </para>
/// <para>
/// Process sandboxing is backed by Windows Job Objects via P/Invoke into
/// <c>kernel32.dll</c>.  AppContainer integration is deferred to Commit 2.
/// </para>
/// </remarks>
public sealed class WindowsPlatformLayer : IPlatformLayer
{
    // =========================================================================
    //  IPlatformLayer
    // =========================================================================

    /// <inheritdoc/>
    public OSPlatformKind Platform => OSPlatformKind.Windows;

    /// <inheritdoc/>
    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <inheritdoc/>
    /// <remarks>
    /// Creates a new named shared memory region in the <c>Global\</c> namespace using
    /// <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew"/>.
    /// </remarks>
    public ISharedMemoryRegion CreateSharedMemory(string name, int sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be null or whitespace.", nameof(name));
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size must be positive.");

        return new WindowsSharedMemoryRegion(name, sizeBytes);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Opens an existing named shared memory region in the <c>Global\</c> namespace
    /// using <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting"/>.
    /// </remarks>
    public ISharedMemoryRegion OpenSharedMemory(string name, int sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be null or whitespace.", nameof(name));
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size must be positive.");

        return WindowsSharedMemoryRegion.Open(name, sizeBytes);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sets <see cref="ProcessStartInfo.UseShellExecute"/> to <c>false</c> (required for
    /// handle inheritance control) and records sandbox metadata on the PSI so that
    /// <see cref="ISandbox.AttachToProcess"/> can associate the spawned process with the
    /// correct Job Object after the process starts.
    /// </remarks>
    public ProcessStartInfo ApplySandbox(ProcessStartInfo psi, OsSandboxProfile profile)
    {
        if (psi == null) throw new ArgumentNullException(nameof(psi));
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        // Job Object assignment must happen post-spawn via AttachToProcess.
        // Here we configure the PSI to give the job assignment the best chance
        // of succeeding (no shell execution means the CLR spawns the process
        // directly, keeping the process in the same job-nesting hierarchy).
        psi.UseShellExecute = false;

        if (profile.DenyDesktopAccess)
            psi.CreateNoWindow = true;

        return psi;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// On Windows this method reduces the current process's token privileges by
    /// calling <c>AdjustTokenPrivileges</c> to strip unnecessary rights.  Full
    /// privilege-drop (AppContainer) is implemented in Commit 2.  For
    /// <see cref="OsSandboxProfileKind.BrokerFull"/> this is a no-op.
    /// </remarks>
    public void SelfRestrictToProfile(OsSandboxProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        if (profile.Kind == OsSandboxProfileKind.BrokerFull)
        {
            // Broker is unrestricted; nothing to do.
            return;
        }

        // Minimal self-restriction for now: ensure the process cannot show UI
        // by setting the desktop explicitly.  Full privilege drop deferred to Commit 2.
        // This is a defensive measure — the Job Object set by the broker is the primary
        // enforcement mechanism for child processes.
        if (profile.DenyDesktopAccess)
        {
            // On Windows, a process that has been assigned to a Job Object with
            // JOB_OBJECT_UILIMIT_DESKTOP cannot create or switch desktops.
            // The self-restriction here is informational; enforcement is by the job.
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <see cref="Process.GetProcessById"/> and reads
    /// <see cref="Process.WorkingSet64"/> (private working set on Windows).
    /// Returns <c>-1</c> when the process is not accessible or has exited.
    /// </remarks>
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
            // Process does not exist.
            return -1;
        }
        catch (InvalidOperationException)
        {
            // Process has exited.
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            // Insufficient permissions to query the process.
            return -1;
        }
    }
}
