using System.Diagnostics;
using FenBrowser.Core.Security.Sandbox;

namespace FenBrowser.Core.Platform;

/// <summary>
/// Platform Abstraction Layer (PAL) — abstracts OS-specific platform operations so that
/// higher-level browser engine code remains portable across Windows, Linux, and macOS.
/// </summary>
/// <remarks>
/// <para>
/// Responsibilities covered by this interface:
/// <list type="bullet">
///   <item>Process launching and sandbox profile application.</item>
///   <item>Named shared memory region creation and opening.</item>
///   <item>Self-restriction (used by child renderer processes on startup).</item>
///   <item>Per-process memory usage queries.</item>
/// </list>
/// </para>
/// <para>
/// Obtain the singleton instance for the current host OS via
/// <see cref="PlatformLayerFactory.GetInstance"/>.
/// </para>
/// </remarks>
public interface IPlatformLayer
{
    /// <summary>Gets the operating system this instance targets.</summary>
    OSPlatformKind Platform { get; }

    /// <summary>
    /// Gets a value indicating whether all features of this platform layer are
    /// fully operational on the current host.
    /// </summary>
    /// <remarks>
    /// A layer may report <c>false</c> when running on a version of Windows that
    /// predates Job Object extended limits, or when kernel features required for
    /// sandboxing are absent.
    /// </remarks>
    bool IsSupported { get; }

    /// <summary>
    /// Creates a new named shared memory region backed by an OS primitive
    /// (<see cref="System.IO.MemoryMappedFiles.MemoryMappedFile"/> on all platforms).
    /// </summary>
    /// <param name="name">
    /// A globally unique name for the region.  On Windows the name is placed in the
    /// Global\ namespace automatically by the implementation; callers should supply a
    /// plain identifier such as <c>"FenBrowser_RenderPipe_1234"</c>.
    /// </param>
    /// <param name="sizeBytes">Size of the region in bytes.  Must be positive.</param>
    /// <returns>
    /// An <see cref="ISharedMemoryRegion"/> with <see cref="ISharedMemoryRegion.IsOwner"/>
    /// equal to <c>true</c>.  The caller must dispose the returned instance when done.
    /// </returns>
    /// <exception cref="System.IO.IOException">Thrown if the OS cannot allocate the region.</exception>
    ISharedMemoryRegion CreateSharedMemory(string name, int sizeBytes);

    /// <summary>
    /// Opens an existing named shared memory region that was created by another process
    /// via <see cref="CreateSharedMemory"/>.
    /// </summary>
    /// <param name="name">The same name that was passed to <see cref="CreateSharedMemory"/>.</param>
    /// <param name="sizeBytes">
    /// Expected size of the region.  Must match the size used at creation time.
    /// </param>
    /// <returns>
    /// An <see cref="ISharedMemoryRegion"/> with <see cref="ISharedMemoryRegion.IsOwner"/>
    /// equal to <c>false</c>.  The caller must dispose the returned instance when done.
    /// </returns>
    /// <exception cref="System.IO.FileNotFoundException">
    /// Thrown when no region with the given name exists.
    /// </exception>
    ISharedMemoryRegion OpenSharedMemory(string name, int sizeBytes);

    /// <summary>
    /// Applies a process sandbox profile to a <see cref="ProcessStartInfo"/> before the
    /// target process is spawned by the caller.
    /// </summary>
    /// <remarks>
    /// On Windows this sets Job Object metadata and — in future commits — AppContainer
    /// configuration on the <see cref="ProcessStartInfo"/> so that the spawned process
    /// is immediately constrained.  The returned <see cref="ProcessStartInfo"/> is the
    /// same object passed in (mutated in-place) and also returned for fluent chaining.
    /// </remarks>
    /// <param name="psi">The <see cref="ProcessStartInfo"/> to modify.</param>
    /// <param name="profile">The sandbox profile to enforce.</param>
    /// <returns>The modified <paramref name="psi"/>.</returns>
    ProcessStartInfo ApplySandbox(ProcessStartInfo psi, OsSandboxProfile profile);

    /// <summary>
    /// Restricts the <em>current</em> process to the given sandbox profile using
    /// OS self-restriction primitives (e.g. <c>SetThreadToken</c>, privilege drops,
    /// <c>prctl</c> on Linux).
    /// </summary>
    /// <remarks>
    /// This is called by child renderer/utility processes immediately after startup,
    /// before processing any untrusted input.  It is a one-way operation: once called
    /// the process cannot regain privileges.
    /// </remarks>
    /// <param name="profile">The profile to enforce on the calling process.</param>
    void SelfRestrictToProfile(OsSandboxProfile profile);

    /// <summary>
    /// Returns the OS-level private working-set memory usage in bytes for the process
    /// identified by <paramref name="pid"/>.
    /// </summary>
    /// <param name="pid">The target process identifier.</param>
    /// <returns>
    /// Memory usage in bytes, or <c>-1</c> if the information is unavailable (e.g. the
    /// process has exited or the caller lacks permission to query it).
    /// </returns>
    long GetProcessMemoryBytes(int pid);
}
