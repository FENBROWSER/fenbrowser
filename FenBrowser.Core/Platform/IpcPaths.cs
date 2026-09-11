using System;
using System.IO;

namespace FenBrowser.Core.Platform;

/// <summary>
/// The on-disk locations that carry broker/child IPC on Unix, kept in one place so
/// the host that creates them and the sandbox that must expose them agree.
/// </summary>
/// <remarks>
/// On Windows named pipes and named mappings live in the object namespace and none
/// of this applies: <see cref="PipeName"/> returns the logical name unchanged and
/// the directories are only used by the file-backed shared memory fallback.
///
/// On Unix .NET implements a named pipe as a Unix-domain socket at
/// <c>$TMPDIR/CoreFxPipe_&lt;name&gt;</c>, or at the given path when the name is
/// rooted. A sandboxed child gets a private tmpfs <c>/tmp</c>, so a pipe created
/// under the host's temp directory is unreachable from inside it. The host
/// therefore creates every pipe as a rooted path inside <see cref="PipeDirectory"/>
/// and the sandbox bind-mounts exactly that directory (read-only: connecting to a
/// socket needs no write permission on the mount). Frame and network shared
/// memory is file-backed under <see cref="SharedMemoryDirectory"/>, which the
/// child writes, so the sandbox binds that one read-write.
/// </remarks>
public static class IpcPaths
{
    private const int UnixSocketPathLimit = 108;

    /// <summary>Root of everything FenBrowser puts in the temp directory for IPC.</summary>
    public static string RootDirectory => Path.Combine(Path.GetTempPath(), "FenBrowser");

    /// <summary>Backing files for cross-process shared memory regions.</summary>
    public static string SharedMemoryDirectory => Path.Combine(RootDirectory, "SharedMemory");

    /// <summary>Pipe sockets owned by this host process.</summary>
    public static string PipeDirectory => Path.Combine(RootDirectory, "Ipc", $"host-{Environment.ProcessId}");

    /// <summary>
    /// Turns a logical pipe name into the name to hand <c>NamedPipeServerStream</c>
    /// and, via the child's environment, <c>NamedPipeClientStream</c>.
    /// </summary>
    public static string PipeName(string logicalName)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
            throw new ArgumentException("Pipe name must not be empty.", nameof(logicalName));

        if (OperatingSystem.IsWindows())
            return logicalName;

        var directory = PipeDirectory;
        EnsurePrivateDirectory(directory);

        var path = Path.Combine(directory, logicalName);
        if (path.Length >= UnixSocketPathLimit)
        {
            // sun_path is 108 bytes; a long TMPDIR plus a GUID-suffixed name can
            // exceed it and bind() would fail with ENAMETOOLONG. Fall back to the
            // default CoreFxPipe location so the pipe at least works unsandboxed,
            // and say why the sandboxed path was not taken.
            FenLogger.Warn(
                $"[IpcPaths] Pipe path '{path}' exceeds the {UnixSocketPathLimit}-byte Unix socket limit; using the default pipe location instead.",
                Logging.LogCategory.General);
            return logicalName;
        }

        return path;
    }

    /// <summary>Creates the shared memory directory (owner-only) and returns it.</summary>
    public static string EnsureSharedMemoryDirectory()
    {
        var directory = SharedMemoryDirectory;
        EnsurePrivateDirectory(directory);
        return directory;
    }

    /// <summary>Removes this host's pipe directory; sockets are unlinked when their servers dispose.</summary>
    public static void TryRemovePipeDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var directory = PipeDirectory;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        if (Directory.Exists(directory))
            return;

        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
