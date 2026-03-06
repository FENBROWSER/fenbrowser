using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FenBrowser.Core.Interop.Windows;

namespace FenBrowser.Core.Security.Sandbox.Windows;

/// <summary>
/// Windows-specific <see cref="ISandbox"/> implementation that combines an AppContainer
/// token restriction with a Job Object to enforce both token-level and resource-level
/// isolation for renderer child processes.
/// </summary>
/// <remarks>
/// <para>
/// AppContainer is a Windows 8+ integrity-level isolation primitive that confines a
/// process to a low-privilege token with no default access to system resources.  The
/// renderer is given <em>zero</em> capabilities — it cannot open network sockets, read
/// files, or enumerate devices without explicit capability grants.  All content
/// acquisition is proxied through the broker IPC pipe.
/// </para>
/// <para>
/// Because <see cref="System.Diagnostics.Process.Start"/> cannot set a
/// <c>PROC_THREAD_ATTRIBUTE_LIST</c> on the child's startup information, AppContainer
/// process creation must be performed via a direct <c>CreateProcessW</c> call.
/// Callers should check <see cref="RequiresCustomSpawn"/> and use
/// <see cref="SpawnProcess"/> accordingly.
/// </para>
/// <para>
/// After spawning, the child is also assigned to a <see cref="WindowsJobObjectSandbox"/>
/// which enforces memory limits, CPU rate caps, and UI restrictions on top of the
/// AppContainer token.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppContainerSandbox : ISandbox
{
    private readonly OsSandboxProfile _profile;
    private readonly string _containerName;
    private readonly WindowsJobObjectSandbox _jobSandbox;

    private IntPtr _acSid;        // AppContainer SID — freed in Dispose
    private bool _disposed;

    // =========================================================================
    //  Construction
    // =========================================================================

    /// <summary>
    /// Initialises a new <see cref="WindowsAppContainerSandbox"/> for the given profile.
    /// </summary>
    /// <param name="profile">
    /// The <see cref="OsSandboxProfile"/> that drives memory limits, CPU rate, and UI
    /// restrictions.  Must not be <c>null</c>.
    /// </param>
    /// <exception cref="Win32Exception">
    /// Thrown when the OS rejects a Job Object or AppContainer configuration call.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when the host OS is older than Windows 8 (AppContainer is not available).
    /// </exception>
    public WindowsAppContainerSandbox(OsSandboxProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        EnsureWindowsVersion();

        // Profile name used as the AppContainer registry key.
        // Must be <= 64 characters, no backslashes.
        _containerName = $"FenBrowser.{profile.Kind}";

        // Ensure the AppContainer profile exists and obtain its SID.
        _acSid = EnsureProfileExists(_containerName);

        // Create a Job Object on top of the AppContainer for resource limits.
        _jobSandbox = new WindowsJobObjectSandbox(profile);
    }

    // =========================================================================
    //  ISandbox — identity
    // =========================================================================

    /// <inheritdoc/>
    public string ProfileName => $"AppContainer({_containerName})";

    /// <inheritdoc/>
    public OsSandboxCapabilities Capabilities => _profile.Capabilities;

    /// <inheritdoc/>
    public bool IsActive => !_disposed && _acSid != IntPtr.Zero && _jobSandbox.IsActive;

    // =========================================================================
    //  ISandbox — process lifecycle
    // =========================================================================

    /// <inheritdoc/>
    /// <remarks>
    /// Sets the <c>FEN_APPCONTAINER_PROFILE</c> environment variable on the child so
    /// that it knows its sandbox context.  The actual AppContainer token is applied by
    /// <see cref="SpawnProcess"/>; this method is a lightweight metadata pass.
    /// </remarks>
    public void ApplyToProcessStartInfo(ProcessStartInfo psi)
    {
        if (psi == null) throw new ArgumentNullException(nameof(psi));
        ThrowIfDisposed();

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.Environment["FEN_APPCONTAINER_PROFILE"] = _containerName;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Because AppContainer processes are created via <see cref="SpawnProcess"/>, this
    /// method is only needed when a caller bypasses the custom-spawn path (e.g. in tests).
    /// It assigns the process to the internal Job Object.
    /// </remarks>
    public void AttachToProcess(Process process)
    {
        if (process == null) throw new ArgumentNullException(nameof(process));
        ThrowIfDisposed();

        _jobSandbox.AttachToProcess(process);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the underlying <see cref="WindowsJobObjectSandbox"/>, which closes
    /// the Job Object handle and triggers <c>KILL_ON_JOB_CLOSE</c>.
    /// </remarks>
    public void Kill()
    {
        ThrowIfDisposed();
        _jobSandbox.Kill();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Queries the underlying Job Object for memory usage and active process count,
    /// and adds AppContainer-specific context to the status message.
    /// </remarks>
    public SandboxHealthStatus GetHealth()
    {
        if (_disposed || _acSid == IntPtr.Zero)
        {
            return new SandboxHealthStatus
            {
                IsHealthy = false,
                Reason = "AppContainer sandbox has been disposed.",
                MemoryUsageBytes = 0,
                ActiveProcessCount = 0
            };
        }

        var jobHealth = _jobSandbox.GetHealth();
        return new SandboxHealthStatus
        {
            IsHealthy = jobHealth.IsHealthy,
            Reason = jobHealth.IsHealthy
                ? string.Empty
                : $"[AppContainer/{_containerName}] {jobHealth.Reason}",
            MemoryUsageBytes = jobHealth.MemoryUsageBytes,
            ActiveProcessCount = jobHealth.ActiveProcessCount
        };
    }

    // =========================================================================
    //  ISandbox — custom spawn
    // =========================================================================

    /// <inheritdoc/>
    /// <value>
    /// Always <c>true</c>: AppContainer process creation requires a
    /// <c>PROC_THREAD_ATTRIBUTE_LIST</c> that cannot be set via
    /// <see cref="ProcessStartInfo"/>.
    /// </value>
    public bool RequiresCustomSpawn => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Calls <c>CreateProcessW</c> with <c>PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES</c>
    /// set to the AppContainer SID established in the constructor.  After successful
    /// creation the primary thread handle is immediately closed (the process is not
    /// suspended).  The returned <see cref="Process"/> is also assigned to the internal
    /// Job Object.
    /// </remarks>
    public Process SpawnProcess(ProcessStartInfo psi)
    {
        if (psi == null) throw new ArgumentNullException(nameof(psi));
        ThrowIfDisposed();

        // Build the command line the same way Process.Start does for a bare executable.
        string commandLine = BuildCommandLine(psi.FileName, psi.Arguments);

        // ---------------------------------------------------------------
        //  Step 1: Query the required attribute list buffer size.
        // ---------------------------------------------------------------
        IntPtr attrListSize = IntPtr.Zero;
        ProcessThreadsInterop.InitializeProcThreadAttributeList(
            IntPtr.Zero, 1, 0, ref attrListSize);
        // ^ Returns false (ERROR_INSUFFICIENT_BUFFER) — that is expected and sets attrListSize.

        IntPtr attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            // ---------------------------------------------------------------
            //  Step 2: Initialise the attribute list.
            // ---------------------------------------------------------------
            if (!ProcessThreadsInterop.InitializeProcThreadAttributeList(
                    attrList, 1, 0, ref attrListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "InitializeProcThreadAttributeList failed.");
            }

            // ---------------------------------------------------------------
            //  Step 3: Set PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES.
            // ---------------------------------------------------------------
            var secCaps = new ProcessThreadsInterop.SECURITY_CAPABILITIES
            {
                AppContainerSid = _acSid,
                Capabilities    = IntPtr.Zero,
                CapabilityCount = 0,
                Reserved        = 0
            };

            int secCapsSize = Marshal.SizeOf<ProcessThreadsInterop.SECURITY_CAPABILITIES>();
            IntPtr secCapsPtr = Marshal.AllocHGlobal(secCapsSize);
            try
            {
                Marshal.StructureToPtr(secCaps, secCapsPtr, fDeleteOld: false);

                if (!ProcessThreadsInterop.UpdateProcThreadAttribute(
                        attrList,
                        0,
                        (IntPtr)ProcessThreadsInterop.PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
                        secCapsPtr,
                        (IntPtr)secCapsSize,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "UpdateProcThreadAttribute(SECURITY_CAPABILITIES) failed.");
                }

                // ---------------------------------------------------------------
                //  Step 4: Build STARTUPINFOEX.
                // ---------------------------------------------------------------
                var startupInfoEx = new ProcessThreadsInterop.STARTUPINFOEX();
                startupInfoEx.StartupInfo.cb = Marshal.SizeOf<ProcessThreadsInterop.STARTUPINFOEX>();
                startupInfoEx.StartupInfo.dwFlags = 0;
                // Renderer child uses named pipes, not stdio — do not inherit handles.
                startupInfoEx.lpAttributeList = attrList;

                // ---------------------------------------------------------------
                //  Step 5: Call CreateProcessW.
                // ---------------------------------------------------------------
                uint creationFlags =
                    ProcessThreadsInterop.EXTENDED_STARTUPINFO_PRESENT |
                    ProcessThreadsInterop.CREATE_NO_WINDOW;

                bool created = ProcessThreadsInterop.CreateProcessW(
                    lpApplicationName: psi.FileName,
                    lpCommandLine: commandLine,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: creationFlags,
                    lpEnvironment: IntPtr.Zero,
                    lpCurrentDirectory: null,
                    lpStartupInfo: ref startupInfoEx,
                    lpProcessInformation: out var procInfo);

                if (!created)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"CreateProcessW failed when spawning '{psi.FileName}' inside AppContainer '{_containerName}'.");
                }

                // ---------------------------------------------------------------
                //  Step 6: Close the thread handle — we do not need it.
                // ---------------------------------------------------------------
                if (procInfo.hThread != IntPtr.Zero)
                    Kernel32Interop.CloseHandle(procInfo.hThread);

                // ---------------------------------------------------------------
                //  Step 7: Wrap in a managed Process and attach to the Job Object.
                // ---------------------------------------------------------------
                Process process;
                try
                {
                    process = Process.GetProcessById(procInfo.dwProcessId);
                }
                catch (ArgumentException ex)
                {
                    // The process exited before we could attach — close the orphaned handle.
                    if (procInfo.hProcess != IntPtr.Zero)
                        Kernel32Interop.CloseHandle(procInfo.hProcess);
                    throw new InvalidOperationException(
                        $"Renderer child (PID {procInfo.dwProcessId}) exited immediately after AppContainer spawn.", ex);
                }

                // The raw process handle from CreateProcessW carries PROCESS_ALL_ACCESS.
                // Process.GetProcessById opens its own handle, so we can close the raw one.
                if (procInfo.hProcess != IntPtr.Zero)
                    Kernel32Interop.CloseHandle(procInfo.hProcess);

                // Assign to the Job Object for resource limits.
                try
                {
                    _jobSandbox.AttachToProcess(process);
                }
                catch (Win32Exception ex)
                {
                    // Non-fatal: Job Object attachment failure should not abort launch,
                    // but we log it as a warning via the exception message.
                    Console.Error.WriteLine(
                        $"[FenBrowser.Security] WARNING: Job Object attachment failed for " +
                        $"AppContainer child PID {process.Id}: {ex.Message}");
                }

                return process;
            }
            finally
            {
                Marshal.FreeHGlobal(secCapsPtr);
                ProcessThreadsInterop.DeleteProcThreadAttributeList(attrList);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attrList);
        }
    }

    // =========================================================================
    //  IDisposable
    // =========================================================================

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Release the AppContainer SID.
        if (_acSid != IntPtr.Zero)
        {
            UserenvInterop.FreeSid(_acSid);
            _acSid = IntPtr.Zero;
        }

        // Dispose the Job Object (triggers KILL_ON_JOB_CLOSE for all child processes).
        _jobSandbox?.Dispose();
    }

    // =========================================================================
    //  Private helpers
    // =========================================================================

    /// <summary>
    /// Ensures that an AppContainer profile with <paramref name="containerName"/> exists
    /// in the registry and returns its SID.
    /// </summary>
    private static IntPtr EnsureProfileExists(string containerName)
    {
        // Try to derive the SID for an existing profile first.
        int hr = UserenvInterop.DeriveAppContainerSidFromAppContainerName(
            containerName, out IntPtr sid);

        if (hr == 0)
        {
            // Profile already exists; SID was returned.
            return sid;
        }

        // E_INVALIDARG (0x80070057) is returned when the profile does not exist.
        // Any other error code is unexpected — fall through to Create and let it fail.
        if (hr != UserenvInterop.E_INVALIDARG && hr >= 0)
        {
            // Positive HRESULT is unexpected but not fatal; attempt creation.
        }

        // Create the profile (zero capabilities — renderer is maximally restricted).
        hr = UserenvInterop.CreateAppContainerProfile(
            pszAppContainerName: containerName,
            pszDisplayName: $"FenBrowser Renderer ({containerName})",
            pszDescription: "Isolated renderer process — no network, no file I/O.",
            pCapabilities: IntPtr.Zero,
            dwCapabilityCount: 0,
            ppSidAppContainerSid: out sid);

        if (hr == UserenvInterop.HRESULT_ALREADY_EXISTS)
        {
            // Another instance created it concurrently; derive the SID now.
            hr = UserenvInterop.DeriveAppContainerSidFromAppContainerName(
                containerName, out sid);
            if (hr != 0)
            {
                throw new Win32Exception(Marshal.GetHRForLastWin32Error(),
                    $"DeriveAppContainerSidFromAppContainerName failed after ALREADY_EXISTS (HRESULT=0x{hr:X8}).");
            }
        }
        else if (hr != 0)
        {
            throw new Win32Exception(Marshal.GetHRForLastWin32Error(),
                $"CreateAppContainerProfile('{containerName}') failed (HRESULT=0x{hr:X8}).");
        }

        return sid;
    }

    /// <summary>
    /// Verifies that the host OS is Windows 8 or later (AppContainer requires Win8+).
    /// </summary>
    private static void EnsureWindowsVersion()
    {
        var ver = Environment.OSVersion.Version;
        // Windows 8 is NT 6.2; Windows 10 is NT 10.0.
        bool isWin8OrLater = ver.Major > 6 || (ver.Major == 6 && ver.Minor >= 2);
        if (!isWin8OrLater)
        {
            throw new PlatformNotSupportedException(
                $"AppContainer sandboxing requires Windows 8 or later. " +
                $"Detected OS version: {ver}.");
        }
    }

    /// <summary>
    /// Builds a Windows-style command-line string from an executable path and arguments,
    /// quoting the executable path when it contains spaces.
    /// </summary>
    private static string BuildCommandLine(string exePath, string arguments)
    {
        // Quote the executable path if it contains spaces (same logic as .NET's Process).
        string quoted = exePath.Contains(' ') ? $"\"{exePath}\"" : exePath;
        if (string.IsNullOrEmpty(arguments))
            return quoted;
        return $"{quoted} {arguments}";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsAppContainerSandbox));
    }
}
