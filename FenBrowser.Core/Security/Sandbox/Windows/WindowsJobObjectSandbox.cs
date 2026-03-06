using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FenBrowser.Core.Interop.Windows;

namespace FenBrowser.Core.Security.Sandbox.Windows;

/// <summary>
/// Windows-specific <see cref="ISandbox"/> implementation that uses a Windows Job Object
/// to enforce process isolation and resource limits.
/// </summary>
[SupportedOSPlatform("windows")]
/// <remarks>
/// <para>
/// The following constraints are applied at construction time via
/// <c>SetInformationJobObject</c>:
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Kill-on-close</term>
///     <description>
///     <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> — if the broker process holding the
///     last handle to this job object exits, all renderer processes inside the job are
///     automatically terminated.
///     </description>
///   </item>
///   <item>
///     <term>Die on unhandled exception</term>
///     <description>
///     <c>JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION</c> — unhandled exceptions in
///     renderer processes crash immediately rather than showing a WER dialog.
///     </description>
///   </item>
///   <item>
///     <term>Memory limit</term>
///     <description>
///     <c>JOB_OBJECT_LIMIT_JOB_MEMORY</c> — the profile's
///     <see cref="OsSandboxProfile.MaxMemoryBytes"/> cap is applied across all processes
///     in the job.
///     </description>
///   </item>
///   <item>
///     <term>UI restrictions (RendererMinimal / UtilityProcess)</term>
///     <description>
///     Desktop enumeration, clipboard read/write, system parameters, ExitWindows, and
///     global atoms are denied via <c>JOBOBJECT_BASIC_UI_RESTRICTIONS</c>.
///     </description>
///   </item>
/// </list>
/// <para>
/// AppContainer integration (Commit 2) will augment this with token-level isolation.
/// </para>
/// </remarks>
public sealed class WindowsJobObjectSandbox : ISandbox
{
    private SafeJobObjectHandle _jobHandle;
    private bool _disposed;
    private readonly OsSandboxProfile _profile;
    private int _activeProcessCount;

    /// <summary>
    /// Initialises a new <see cref="WindowsJobObjectSandbox"/> for the given profile.
    /// The underlying Job Object is created and configured immediately.
    /// </summary>
    /// <param name="profile">
    /// The <see cref="OsSandboxProfile"/> that drives memory limits, CPU rate, and UI
    /// restrictions.
    /// </param>
    /// <exception cref="Win32Exception">
    /// Thrown when the OS rejects a Job Object configuration call.
    /// </exception>
    public WindowsJobObjectSandbox(OsSandboxProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        _jobHandle = Kernel32Interop.CreateJobObjectW(IntPtr.Zero, null);
        if (_jobHandle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObjectW failed.");

        ApplyExtendedLimits();
        ApplyUiRestrictions();
    }

    // =========================================================================
    //  ISandbox
    // =========================================================================

    /// <inheritdoc/>
    public string ProfileName => _profile.Kind.ToString();

    /// <inheritdoc/>
    public OsSandboxCapabilities Capabilities => _profile.Capabilities;

    /// <inheritdoc/>
    public bool IsActive => !_disposed && _jobHandle is { IsInvalid: false };

    /// <inheritdoc/>
    public void ApplyToProcessStartInfo(ProcessStartInfo psi)
    {
        if (psi == null) throw new ArgumentNullException(nameof(psi));
        ThrowIfDisposed();

        // Ensure the child process does not inherit a handle to a Job Object
        // that would prevent our AssignProcessToJobObject call from succeeding.
        // Setting CreateNoWindow suppresses UI for headless subprocesses.
        // The actual assignment happens in AttachToProcess after Process.Start.
        psi.UseShellExecute = false;
        psi.CreateNoWindow = _profile.DenyDesktopAccess;
    }

    /// <inheritdoc/>
    public void AttachToProcess(Process process)
    {
        if (process == null) throw new ArgumentNullException(nameof(process));
        ThrowIfDisposed();

        IntPtr hProcess = process.Handle;

        bool assigned = Kernel32Interop.AssignProcessToJobObject(_jobHandle, hProcess);
        if (!assigned)
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_ACCESS_DENIED (5) occurs when the process is already in a job on
            // older Windows.  On Win8+ nested jobs are supported, so this is fatal.
            throw new Win32Exception(err, $"AssignProcessToJobObject failed for PID {process.Id} (error {err}).");
        }

        System.Threading.Interlocked.Increment(ref _activeProcessCount);
    }

    /// <inheritdoc/>
    public void Kill()
    {
        ThrowIfDisposed();

        // Closing the last handle with KILL_ON_JOB_CLOSE set will terminate all
        // processes; alternatively, TerminateJobObject is not exposed here to
        // keep the API surface minimal.  We simply dispose which triggers the kill.
        Dispose();
    }

    /// <inheritdoc/>
    public SandboxHealthStatus GetHealth()
    {
        if (_disposed || _jobHandle.IsInvalid)
        {
            return new SandboxHealthStatus
            {
                IsHealthy = false,
                Reason = "Job Object has been closed.",
                MemoryUsageBytes = 0,
                ActiveProcessCount = 0
            };
        }

        long memUsage = QueryJobMemoryUsage();
        bool healthy = true;
        string reason = string.Empty;

        if (_profile.MaxMemoryBytes != long.MaxValue && memUsage > _profile.MaxMemoryBytes)
        {
            healthy = false;
            reason = $"Memory usage {memUsage} bytes exceeds limit {_profile.MaxMemoryBytes} bytes.";
        }

        return new SandboxHealthStatus
        {
            IsHealthy = healthy,
            Reason = reason,
            MemoryUsageBytes = memUsage,
            ActiveProcessCount = _activeProcessCount
        };
    }

    // =========================================================================
    //  IDisposable
    // =========================================================================

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Disposing the SafeJobObjectHandle calls CloseHandle.
        // Because KILL_ON_JOB_CLOSE is set, closing the last handle automatically
        // terminates all processes inside the job.
        _jobHandle?.Dispose();
        _jobHandle = null;
    }

    // =========================================================================
    //  Private helpers
    // =========================================================================

    /// <summary>
    /// Applies extended limits: kill-on-close, die-on-exception, memory cap, and
    /// optionally a CPU rate hard-cap.
    /// </summary>
    private void ApplyExtendedLimits()
    {
        var extInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();

        extInfo.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT.KILL_ON_JOB_CLOSE |
            JOB_OBJECT_LIMIT.DIE_ON_UNHANDLED_EXCEPTION;

        if (_profile.MaxMemoryBytes != long.MaxValue)
        {
            extInfo.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT.JOB_MEMORY;
            extInfo.JobMemoryLimit = (UIntPtr)(ulong)_profile.MaxMemoryBytes;
        }

        SetJobInformation(
            JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
            extInfo);

        // Apply CPU rate hard cap when the profile requests less than 100%.
        if (_profile.MaxCpuPercent > 0 && _profile.MaxCpuPercent < 100)
        {
            ApplyCpuRateCap(_profile.MaxCpuPercent);
        }
    }

    /// <summary>
    /// Applies CPU rate control to the job object as a hard cap.
    /// The CPU rate is expressed in units of 1/100 of a percent (1–10000).
    /// </summary>
    private void ApplyCpuRateCap(int maxCpuPercent)
    {
        // CpuRate is in units of 1/100th of a percent: 100% = 10000.
        uint cpuRate = (uint)(maxCpuPercent * 100);

        var cpuControl = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL.Enable | JOB_OBJECT_CPU_RATE_CONTROL.HardCap,
            CpuRate = cpuRate
        };

        // Best-effort: CPU rate control is not available on all SKUs.
        try
        {
            SetJobInformation(
                JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation,
                cpuControl);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 87 /* ERROR_INVALID_PARAMETER */ ||
                                        ex.NativeErrorCode == 1 /* ERROR_INVALID_FUNCTION */)
        {
            // Silently ignore: CPU rate control not supported on this Windows edition.
        }
    }

    /// <summary>
    /// Applies UI restrictions for profiles that require desktop/clipboard isolation.
    /// </summary>
    private void ApplyUiRestrictions()
    {
        if (!_profile.DenyDesktopAccess && !_profile.DenyWindowEnumeration)
            return;

        var uiFlags = JOB_OBJECT_UILIMIT.HANDLES |
                      JOB_OBJECT_UILIMIT.SYSTEMPARAMETERS |
                      JOB_OBJECT_UILIMIT.DISPLAYSETTINGS |
                      JOB_OBJECT_UILIMIT.EXITWINDOWS |
                      JOB_OBJECT_UILIMIT.GLOBALATOMS;

        if (_profile.DenyDesktopAccess)
            uiFlags |= JOB_OBJECT_UILIMIT.DESKTOP;

        // Deny clipboard access unless the profile grants those capabilities.
        if ((_profile.Capabilities & OsSandboxCapabilities.ReadClipboard) == 0)
            uiFlags |= JOB_OBJECT_UILIMIT.READCLIPBOARD;

        if ((_profile.Capabilities & OsSandboxCapabilities.WriteClipboard) == 0)
            uiFlags |= JOB_OBJECT_UILIMIT.WRITECLIPBOARD;

        var uiRestrictions = new JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            UIRestrictionsClass = uiFlags
        };

        SetJobInformation(
            JOBOBJECTINFOCLASS.JobObjectBasicUIRestrictions,
            uiRestrictions);
    }

    /// <summary>
    /// Marshals a managed structure to unmanaged memory and calls
    /// <c>SetInformationJobObject</c>.
    /// </summary>
    private void SetJobInformation<T>(JOBOBJECTINFOCLASS infoClass, T info) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
            bool ok = Kernel32Interop.SetInformationJobObject(
                _jobHandle,
                infoClass,
                ptr,
                (uint)size);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"SetInformationJobObject({infoClass}) failed (error {err}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Queries the current committed memory usage of the entire job in bytes.
    /// Returns 0 on failure.
    /// </summary>
    private long QueryJobMemoryUsage()
    {
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = Kernel32Interop.QueryInformationJobObject(
                _jobHandle,
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                ptr,
                (uint)size,
                IntPtr.Zero);

            if (!ok) return 0;

            var extInfo = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(ptr);
            return (long)(ulong)extInfo.PeakJobMemoryUsed;
        }
        catch
        {
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsJobObjectSandbox));
    }
}
