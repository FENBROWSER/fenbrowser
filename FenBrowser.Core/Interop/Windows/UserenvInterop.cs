using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FenBrowser.Core.Interop.Windows;

/// <summary>
/// P/Invoke declarations for userenv.dll and advapi32.dll functions required by
/// the Windows AppContainer sandbox implementation.
/// </summary>
/// <remarks>
/// AppContainer profiles are stored per-user in the registry under
/// <c>HKCU\SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings\</c>.
/// All members are <c>static extern</c> and must not be called on non-Windows hosts.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class UserenvInterop
{
    private const string Userenv = "userenv.dll";
    private const string Advapi32 = "advapi32.dll";

    // =========================================================================
    //  AppContainer profile management (userenv.dll)
    // =========================================================================

    /// <summary>
    /// Creates a new AppContainer profile and returns a SID for it.
    /// </summary>
    /// <param name="pszAppContainerName">
    /// A unique moniker for the container, up to 64 characters (no backslashes).
    /// </param>
    /// <param name="pszDisplayName">Display name shown in security UI.</param>
    /// <param name="pszDescription">Human-readable description.</param>
    /// <param name="pCapabilities">
    /// Pointer to an array of SID_AND_ATTRIBUTES structures describing capabilities.
    /// Pass <see cref="IntPtr.Zero"/> for no capabilities.
    /// </param>
    /// <param name="dwCapabilityCount">Number of elements in <paramref name="pCapabilities"/>.</param>
    /// <param name="ppSidAppContainerSid">
    /// Receives the AppContainer SID on success.  The caller must free this with
    /// <see cref="FreeSid"/> when done.
    /// </param>
    /// <returns>
    /// S_OK (0) on success; HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS) (0x800700B7) if the
    /// profile already exists; a negative HRESULT on error.
    /// </returns>
    [DllImport(Userenv, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int CreateAppContainerProfile(
        string pszAppContainerName,
        string pszDisplayName,
        string pszDescription,
        IntPtr pCapabilities,
        uint dwCapabilityCount,
        out IntPtr ppSidAppContainerSid);

    /// <summary>
    /// Looks up an existing AppContainer profile by name and returns its SID.
    /// </summary>
    /// <param name="pszAppContainerName">The container profile name to look up.</param>
    /// <param name="ppsidAppContainerSid">
    /// Receives the AppContainer SID on success.  The caller must free this with
    /// <see cref="FreeSid"/> when done.
    /// </param>
    /// <returns>
    /// S_OK (0) on success; a negative HRESULT (e.g. E_INVALIDARG / 0x80070057) when
    /// the profile does not exist.
    /// </returns>
    [DllImport(Userenv, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int DeriveAppContainerSidFromAppContainerName(
        string pszAppContainerName,
        out IntPtr ppsidAppContainerSid);

    /// <summary>
    /// Deletes an AppContainer profile from the registry.
    /// </summary>
    /// <param name="pszAppContainerName">The container profile name to delete.</param>
    /// <returns>S_OK (0) on success; a negative HRESULT on error.</returns>
    [DllImport(Userenv, SetLastError = true)]
    internal static extern int DeleteAppContainerProfile(
        [MarshalAs(UnmanagedType.LPWStr)] string pszAppContainerName);

    // =========================================================================
    //  SID helpers (advapi32.dll)
    // =========================================================================

    /// <summary>
    /// Frees a SID previously allocated by the system (e.g. by
    /// <see cref="CreateAppContainerProfile"/> or
    /// <see cref="DeriveAppContainerSidFromAppContainerName"/>).
    /// </summary>
    /// <param name="pSid">Pointer to the SID to free.</param>
    [DllImport(Advapi32, SetLastError = true)]
    internal static extern void FreeSid(IntPtr pSid);

    /// <summary>
    /// Compares two security identifiers (SIDs).
    /// </summary>
    /// <param name="pSid1">Pointer to the first SID.</param>
    /// <param name="pSid2">Pointer to the second SID.</param>
    /// <returns><c>true</c> if the SIDs are equal; <c>false</c> otherwise.</returns>
    [DllImport(Advapi32, SetLastError = true)]
    internal static extern bool EqualSid(IntPtr pSid1, IntPtr pSid2);

    // =========================================================================
    //  HRESULT helpers
    // =========================================================================

    /// <summary>
    /// HRESULT returned by <see cref="CreateAppContainerProfile"/> when the profile
    /// already exists.  This is treated as a success condition.
    /// </summary>
    internal const int HRESULT_ALREADY_EXISTS = unchecked((int)0x800700B7);

    /// <summary>
    /// E_INVALIDARG — returned by <see cref="DeriveAppContainerSidFromAppContainerName"/>
    /// when the named profile does not exist.
    /// </summary>
    internal const int E_INVALIDARG = unchecked((int)0x80070057);
}
