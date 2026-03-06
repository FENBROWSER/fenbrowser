using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FenBrowser.Core.Interop.Windows;

/// <summary>
/// P/Invoke declarations for the Win32 process-thread attribute list API and
/// <c>CreateProcessW</c>, required to spawn a child process inside an AppContainer.
/// </summary>
/// <remarks>
/// <para>
/// .NET's <see cref="System.Diagnostics.Process.Start"/> cannot attach a
/// <c>PROC_THREAD_ATTRIBUTE_LIST</c> to the child's startup information, which means
/// it cannot be used to set the <c>PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES</c>
/// attribute required for AppContainer process creation.  We therefore call
/// <c>CreateProcessW</c> directly with an extended startup info (<see cref="STARTUPINFOEX"/>)
/// that carries the attribute list.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ProcessThreadsInterop
{
    private const string Kernel32 = "kernel32.dll";

    // =========================================================================
    //  PROC_THREAD_ATTRIBUTE_LIST management
    // =========================================================================

    /// <summary>
    /// Initialises the specified list of attributes for process and thread creation.
    /// Call with <paramref name="lpAttributeList"/> == <see cref="IntPtr.Zero"/> and
    /// <paramref name="lpSize"/> == 0 to query the required buffer size.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        uint dwAttributeCount,
        uint dwFlags,
        ref IntPtr lpSize);

    /// <summary>
    /// Updates the specified attribute in a list of attributes for process and thread creation.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr Attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    /// <summary>
    /// Frees the attribute list and releases associated resources.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    // =========================================================================
    //  CreateProcessW
    // =========================================================================

    /// <summary>
    /// Creates a new process and its primary thread.
    /// </summary>
    /// <remarks>
    /// When using an extended startup info (<see cref="STARTUPINFOEX"/>), pass
    /// <see cref="EXTENDED_STARTUPINFO_PRESENT"/> in <paramref name="dwCreationFlags"/>
    /// and ensure <see cref="STARTUPINFOEX.StartupInfo"/>.cb is set to
    /// <c>sizeof(STARTUPINFOEX)</c>.
    /// </remarks>
    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessW(
        string lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    // =========================================================================
    //  Structures
    // =========================================================================

    /// <summary>
    /// Extended process startup information structure that carries a
    /// <c>PROC_THREAD_ATTRIBUTE_LIST</c> pointer.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOEX
    {
        /// <summary>The base STARTUPINFO structure.</summary>
        public STARTUPINFO StartupInfo;

        /// <summary>
        /// Pointer to a <c>PROC_THREAD_ATTRIBUTE_LIST</c> created by
        /// <see cref="InitializeProcThreadAttributeList"/>.
        /// </summary>
        public IntPtr lpAttributeList;
    }

    /// <summary>
    /// Base process startup information.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        /// <summary>Size of this structure, in bytes.  Must be set by the caller.</summary>
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        /// <summary>Flags that control which fields are used. See STARTF_* constants.</summary>
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    /// <summary>
    /// Receives identification information for the newly created process and its primary thread.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        /// <summary>Handle to the newly created process.</summary>
        public IntPtr hProcess;

        /// <summary>Handle to the primary thread of the newly created process.</summary>
        public IntPtr hThread;

        /// <summary>Process identifier of the newly created process.</summary>
        public int dwProcessId;

        /// <summary>Thread identifier of the primary thread.</summary>
        public int dwThreadId;
    }

    /// <summary>
    /// Specifies the security capabilities of an AppContainer process.
    /// Maps to the Win32 <c>SECURITY_CAPABILITIES</c> structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_CAPABILITIES
    {
        /// <summary>Pointer to the AppContainer SID.</summary>
        public IntPtr AppContainerSid;

        /// <summary>
        /// Pointer to an array of <c>SID_AND_ATTRIBUTES</c> structures that
        /// specify the capabilities to grant.  <see cref="IntPtr.Zero"/> for none.
        /// </summary>
        public IntPtr Capabilities;

        /// <summary>Number of entries in <see cref="Capabilities"/>.</summary>
        public uint CapabilityCount;

        /// <summary>Reserved; must be zero.</summary>
        public uint Reserved;
    }

    // =========================================================================
    //  Constants
    // =========================================================================

    /// <summary>
    /// The attribute number for <c>PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES</c>.
    /// Value: <c>0x00020009</c> == 131081.
    /// </summary>
    internal const int PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = 0x00020009;

    /// <summary>
    /// Creation flag indicating that <c>lpStartupInfo</c> points to a
    /// <see cref="STARTUPINFOEX"/> structure rather than a <c>STARTUPINFO</c>.
    /// </summary>
    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

    /// <summary>Process creation flag: create the process suspended.</summary>
    internal const uint CREATE_SUSPENDED = 0x00000004;

    /// <summary>Process creation flag: create the process without a console window.</summary>
    internal const uint CREATE_NO_WINDOW = 0x08000000;

    /// <summary>
    /// <see cref="STARTUPINFO.dwFlags"/> bit indicating that stdin/stdout/stderr handles
    /// in <see cref="STARTUPINFO"/> should be used.
    /// </summary>
    internal const uint STARTF_USESTDHANDLES = 0x00000100;

    /// <summary>
    /// Flag for <c>DuplicateHandle</c>: the duplicate handle has the same access rights
    /// as the source.
    /// </summary>
    internal const uint DUPLICATE_SAME_ACCESS = 0x00000002;
}
