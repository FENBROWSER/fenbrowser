using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FenBrowser.Core.Interop.Windows;

// ============================================================================
//  Safe handle wrapper
// ============================================================================

/// <summary>
/// A <see cref="SafeHandle"/> for a Windows Job Object HANDLE.
/// The handle is closed with <c>CloseHandle</c> on disposal.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SafeJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initialises a new instance with ownership of the supplied handle value.</summary>
    public SafeJobObjectHandle() : base(ownsHandle: true) { }

    /// <summary>Initialises a new instance wrapping an existing handle value.</summary>
    /// <param name="existingHandle">The raw HANDLE value returned by <c>CreateJobObject</c>.</param>
    /// <param name="ownsHandle"><c>true</c> if this instance should close the handle on disposal.</param>
    public SafeJobObjectHandle(IntPtr existingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(existingHandle);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        return Kernel32Interop.CloseHandle(handle);
    }
}

// ============================================================================
//  Job Object information class enumeration
// ============================================================================

/// <summary>
/// Values for the <c>JobObjectInfoClass</c> parameter of
/// <c>SetInformationJobObject</c> / <c>QueryInformationJobObject</c>.
/// </summary>
public enum JOBOBJECTINFOCLASS : int
{
    JobObjectBasicAccountingInformation = 1,
    JobObjectBasicLimitInformation = 2,
    JobObjectBasicProcessIdList = 3,
    JobObjectBasicUIRestrictions = 4,
    JobObjectSecurityLimitInformation = 5,
    JobObjectEndOfJobTimeInformation = 6,
    JobObjectAssociateCompletionPortInformation = 7,
    JobObjectBasicAndIoAccountingInformation = 8,
    JobObjectExtendedLimitInformation = 9,
    JobObjectJobSetInformation = 10,
    JobObjectGroupInformation = 11,
    JobObjectNotificationLimitInformation = 12,
    JobObjectLimitViolationInformation = 13,
    JobObjectGroupInformationEx = 14,
    JobObjectCpuRateControlInformation = 15,
    JobObjectCompletionFilter = 16,
    JobObjectCompletionCounter = 17,
    JobObjectReserved1Information = 18,
    JobObjectReserved2Information = 19,
    JobObjectReserved3Information = 20,
    JobObjectReserved4Information = 21,
    JobObjectReserved5Information = 22,
    JobObjectReserved6Information = 23,
    JobObjectReserved7Information = 24,
    JobObjectReserved8Information = 25,
    JobObjectReserved9Information = 26,
    JobObjectReserved10Information = 27,
    JobObjectNetRateControlInformation = 32,
}

// ============================================================================
//  JOB_OBJECT_LIMIT_* flags
// ============================================================================

/// <summary>Flags for <see cref="JOBOBJECT_BASIC_LIMIT_INFORMATION.LimitFlags"/>.</summary>
[Flags]
public enum JOB_OBJECT_LIMIT : uint
{
    /// <summary>Causes all associated processes to use the same minimum and maximum working sets.</summary>
    WORKINGSET = 0x00000001,
    /// <summary>Establishes a user-mode execution time limit for each job.</summary>
    PROCESS_TIME = 0x00000002,
    /// <summary>Establishes a user-mode execution time limit for the job.</summary>
    JOB_TIME = 0x00000004,
    /// <summary>Establishes a maximum number of simultaneously active processes for the job.</summary>
    ACTIVE_PROCESS = 0x00000008,
    /// <summary>Causes all processes associated with the job to use the same processor affinity.</summary>
    AFFINITY = 0x00000010,
    /// <summary>Causes all processes associated with the job to use the same priority class.</summary>
    PRIORITY_CLASS = 0x00000020,
    /// <summary>Preserves any job time limits you previously set.</summary>
    PRESERVE_JOB_TIME = 0x00000040,
    /// <summary>Establishes a minimum and maximum working set size for each process.</summary>
    SCHEDULING_CLASS = 0x00000080,
    /// <summary>Causes all processes associated with the job to limit the job-wide sum of their committed memory.</summary>
    PROCESS_MEMORY = 0x00000100,
    /// <summary>Causes all processes associated with the job to limit the virtual memory committed for the job.</summary>
    JOB_MEMORY = 0x00000200,
    /// <summary>Forces a call to the SetErrorMode function with the SE_ERR_NOUI flag for each process associated with the job.</summary>
    DIE_ON_UNHANDLED_EXCEPTION = 0x00000400,
    /// <summary>If any process associated with the job creates a child process using CREATE_BREAKAWAY_FROM_JOB, the child process does not break away from the job.</summary>
    BREAKAWAY_OK = 0x00000800,
    /// <summary>Allows any process associated with the job to create child processes that are not associated with the job.</summary>
    SILENT_BREAKAWAY_OK = 0x00001000,
    /// <summary>Causes all processes associated with the job to terminate when the last handle to the job object is closed.</summary>
    KILL_ON_JOB_CLOSE = 0x00002000,
    /// <summary>Allows processes to use a subset of the processor affinity for all processes associated with the job.</summary>
    SUBSET_AFFINITY = 0x00004000,
}

// ============================================================================
//  JOB_OBJECT_UILIMIT_* flags
// ============================================================================

/// <summary>Flags for <see cref="JOBOBJECT_BASIC_UI_RESTRICTIONS.UIRestrictionsClass"/>.</summary>
[Flags]
public enum JOB_OBJECT_UILIMIT : uint
{
    /// <summary>Prevents processes associated with the job from using USER handles owned by processes not associated with the same job.</summary>
    HANDLES = 0x00000001,
    /// <summary>Prevents processes associated with the job from reading data from the clipboard.</summary>
    READCLIPBOARD = 0x00000002,
    /// <summary>Prevents processes associated with the job from writing data to the clipboard.</summary>
    WRITECLIPBOARD = 0x00000004,
    /// <summary>Prevents processes associated with the job from changing system parameters by using the SystemParametersInfo function.</summary>
    SYSTEMPARAMETERS = 0x00000008,
    /// <summary>Prevents processes associated with the job from calling the ChangeDisplaySettings function.</summary>
    DISPLAYSETTINGS = 0x00000010,
    /// <summary>Prevents processes associated with the job from accessing global atoms.</summary>
    GLOBALATOMS = 0x00000020,
    /// <summary>Prevents processes associated with the job from creating desktops and switching desktops using the CreateDesktop and SwitchDesktop functions.</summary>
    DESKTOP = 0x00000040,
    /// <summary>Prevents processes associated with the job from calling the ExitWindows or ExitWindowsEx function.</summary>
    EXITWINDOWS = 0x00000080,
}

// ============================================================================
//  Structures
// ============================================================================

/// <summary>
/// Contains basic limit information for a job object.
/// Corresponds to the Win32 <c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c> structure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    /// <summary>Per-process user-mode execution time limit (100-nanosecond ticks). 0 = unlimited.</summary>
    public long PerProcessUserTimeLimit;
    /// <summary>Per-job user-mode execution time limit (100-nanosecond ticks). 0 = unlimited.</summary>
    public long PerJobUserTimeLimit;
    /// <summary>Limit flags — see <see cref="JOB_OBJECT_LIMIT"/>.</summary>
    public JOB_OBJECT_LIMIT LimitFlags;
    /// <summary>Minimum working set size in bytes.</summary>
    public UIntPtr MinimumWorkingSetSize;
    /// <summary>Maximum working set size in bytes.</summary>
    public UIntPtr MaximumWorkingSetSize;
    /// <summary>Active process limit.</summary>
    public uint ActiveProcessLimit;
    /// <summary>Processor affinity mask.</summary>
    public UIntPtr Affinity;
    /// <summary>Priority class for all processes in the job.</summary>
    public uint PriorityClass;
    /// <summary>Scheduling class for all processes in the job.</summary>
    public uint SchedulingClass;
}

/// <summary>
/// Contains I/O accounting information for a job object.
/// Corresponds to <c>IO_COUNTERS</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

/// <summary>
/// Contains extended limit information for a job object.
/// Corresponds to <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    /// <summary>Basic limit information.</summary>
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    /// <summary>I/O counters (ignored by <c>SetInformationJobObject</c>).</summary>
    public IO_COUNTERS IoInfo;
    /// <summary>Maximum process virtual memory size in bytes.</summary>
    public UIntPtr ProcessMemoryLimit;
    /// <summary>Maximum committed memory for the job in bytes.</summary>
    public UIntPtr JobMemoryLimit;
    /// <summary>Peak process virtual memory usage (output only).</summary>
    public UIntPtr PeakProcessMemoryUsed;
    /// <summary>Peak job committed memory usage (output only).</summary>
    public UIntPtr PeakJobMemoryUsed;
}

/// <summary>
/// Contains UI restriction flags for a job object.
/// Corresponds to <c>JOBOBJECT_BASIC_UI_RESTRICTIONS</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_BASIC_UI_RESTRICTIONS
{
    /// <summary>UI restriction flags — see <see cref="JOB_OBJECT_UILIMIT"/>.</summary>
    public JOB_OBJECT_UILIMIT UIRestrictionsClass;
}

/// <summary>
/// CPU rate control flags for a job object.
/// </summary>
[Flags]
public enum JOB_OBJECT_CPU_RATE_CONTROL : uint
{
    Enable = 0x1,
    WeightBased = 0x2,
    HardCap = 0x4,
    Notify = 0x8,
    MinMaxRate = 0x10,
}

/// <summary>
/// Contains CPU rate control information for a job object.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
{
    /// <summary>Control flags — see <see cref="JOB_OBJECT_CPU_RATE_CONTROL"/>.</summary>
    public JOB_OBJECT_CPU_RATE_CONTROL ControlFlags;
    /// <summary>
    /// When <see cref="JOB_OBJECT_CPU_RATE_CONTROL.Enable"/> and
    /// <see cref="JOB_OBJECT_CPU_RATE_CONTROL.WeightBased"/> are set this is a weight value
    /// (1–9); otherwise it is the maximum CPU rate in units of 1/100 of a percent (1–10000).
    /// </summary>
    public uint CpuRate;
}

// ============================================================================
//  P/Invoke declarations
// ============================================================================

/// <summary>
/// Managed P/Invoke declarations for Kernel32.dll functions required by the
/// Windows Job Object sandbox implementation.
/// </summary>
/// <remarks>
/// All members are <c>static extern</c> and must not be called on non-Windows hosts.
/// Use <see cref="System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform"/> to
/// guard call sites.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class Kernel32Interop
{
    private const string Kernel32 = "kernel32.dll";

    // -----------------------------------------------------------------------
    //  Job Object API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates or opens a job object.
    /// </summary>
    /// <param name="lpJobAttributes">Security attributes; pass <see cref="IntPtr.Zero"/> for default.</param>
    /// <param name="lpName">Optional name; <c>null</c> creates an anonymous job object.</param>
    /// <returns>A handle to the job object, or an invalid handle on failure.</returns>
    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    public static extern SafeJobObjectHandle CreateJobObjectW(
        IntPtr lpJobAttributes,
        string lpName);

    /// <summary>
    /// Sets limits and other information about a job object.
    /// </summary>
    /// <param name="hJob">A handle to the job object.</param>
    /// <param name="JobObjectInfoClass">The information class to set.</param>
    /// <param name="lpJobObjectInfo">A pointer to the structure that contains the information.</param>
    /// <param name="cbJobObjectInfoLength">The size of the structure, in bytes.</param>
    /// <returns><c>true</c> on success; <c>false</c> on failure (call <see cref="Marshal.GetLastWin32Error"/>).</returns>
    [DllImport(Kernel32, SetLastError = true, EntryPoint = "SetInformationJobObject")]
    public static extern bool SetInformationJobObject(
        SafeJobObjectHandle hJob,
        JOBOBJECTINFOCLASS JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    /// <summary>
    /// Assigns a process to an existing job object.
    /// </summary>
    /// <param name="hJob">A handle to the job object.</param>
    /// <param name="hProcess">A handle to the process to assign.</param>
    /// <returns><c>true</c> on success; <c>false</c> on failure.</returns>
    [DllImport(Kernel32, SetLastError = true, EntryPoint = "AssignProcessToJobObject")]
    public static extern bool AssignProcessToJobObject(
        SafeJobObjectHandle hJob,
        IntPtr hProcess);

    /// <summary>
    /// Retrieves limit and job state information from the job object.
    /// </summary>
    /// <param name="hJob">A handle to the job object.</param>
    /// <param name="JobObjectInfoClass">The information class to query.</param>
    /// <param name="lpJobObjectInfo">A pointer to the buffer that receives the information.</param>
    /// <param name="cbJobObjectInfoLength">The size of the buffer, in bytes.</param>
    /// <param name="lpReturnLength">
    /// Receives the number of bytes written to the buffer; may be <see cref="IntPtr.Zero"/>.
    /// </param>
    /// <returns><c>true</c> on success; <c>false</c> on failure.</returns>
    [DllImport(Kernel32, SetLastError = true, EntryPoint = "QueryInformationJobObject")]
    public static extern bool QueryInformationJobObject(
        SafeJobObjectHandle hJob,
        JOBOBJECTINFOCLASS JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        IntPtr lpReturnLength);

    // -----------------------------------------------------------------------
    //  Handle management
    // -----------------------------------------------------------------------

    /// <summary>Closes an open object handle.</summary>
    /// <param name="hObject">A valid handle to an open object.</param>
    /// <returns><c>true</c> on success; <c>false</c> on failure.</returns>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    // -----------------------------------------------------------------------
    //  Process handle duplication (used to pass child process handle to job)
    // -----------------------------------------------------------------------

    /// <summary>Retrieves a pseudo handle for the current process.</summary>
    /// <returns>A pseudo handle to the current process (value <c>-1</c>).</returns>
    [DllImport(Kernel32)]
    public static extern IntPtr GetCurrentProcess();

    /// <summary>Opens an existing local process object.</summary>
    /// <param name="dwDesiredAccess">Access to the process object.</param>
    /// <param name="bInheritHandle">If <c>true</c>, the returned handle is inheritable.</param>
    /// <param name="dwProcessId">The PID of the process to open.</param>
    /// <returns>
    /// A handle to the process, or <see cref="IntPtr.Zero"/> on failure.
    /// The caller is responsible for closing this handle.
    /// </returns>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwProcessId);

    /// <summary>
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> access right — sufficient to read
    /// working-set size and call <c>AssignProcessToJobObject</c>.
    /// </summary>
    public const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
}
