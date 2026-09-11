using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using FenBrowser.Core.Platform;
using FenBrowser.Core.Logging;

#pragma warning disable CS8632

namespace FenBrowser.Core.Security.Sandbox.Posix;

/// <summary>
/// POSIX sandbox implementation that launches child processes through native
/// OS helpers (`bwrap` on Linux, `sandbox-exec` on macOS).
/// </summary>
public sealed class PosixCommandSandbox : ISandbox
{
    private readonly OsSandboxProfile _profile;
    private readonly OSPlatformKind _platform;
    private readonly string _helperPath;
    private readonly object _processLock = new();
    private readonly List<Process> _activeProcesses = new();
    private readonly string _homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _tempDirectory = Path.GetTempPath();
    private bool _disposed;

    public PosixCommandSandbox(OsSandboxProfile profile, OSPlatformKind platform, string helperPath)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _platform = platform;
        _helperPath = string.IsNullOrWhiteSpace(helperPath)
            ? throw new ArgumentException("Helper path is required.", nameof(helperPath))
            : helperPath;
    }

    public string ProfileName => $"Posix({_profile.Kind})";
    public OsSandboxCapabilities Capabilities => _profile.Capabilities;
    public bool IsActive => !_disposed;
    public bool RequiresCustomSpawn => true;

    public void ApplyToProcessStartInfo(ProcessStartInfo psi)
    {
        if (psi == null) throw new ArgumentNullException(nameof(psi));
        ThrowIfDisposed();

        psi.UseShellExecute = false;
        if (_profile.DenyDesktopAccess)
            psi.CreateNoWindow = true;
    }

    public void AttachToProcess(Process process)
    {
        if (process == null) throw new ArgumentNullException(nameof(process));
        ThrowIfDisposed();

        lock (_processLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PosixCommandSandbox));
            _activeProcesses.Add(process);
        }
    }

    public void Kill()
    {
        ThrowIfDisposed();
        var processes = TakeTrackedProcesses(markDisposed: false);
        TerminateAndDispose(processes);
    }

    public SandboxHealthStatus GetHealth()
    {
        List<Process> processes;
        lock (_processLock)
        {
            processes = _activeProcesses.ToList();
        }

        long memoryBytes = 0;
        var aliveCount = 0;
        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited)
                    continue;

                process.Refresh();
                memoryBytes += process.WorkingSet64;
                aliveCount++;
            }
            catch
            {
            }
        }

        return new SandboxHealthStatus
        {
            IsHealthy = !_disposed,
            Reason = _disposed ? "POSIX command sandbox disposed." : string.Empty,
            MemoryUsageBytes = memoryBytes,
            ActiveProcessCount = aliveCount
        };
    }

    public Process SpawnProcess(ProcessStartInfo psi)
    {
        if (psi == null) throw new ArgumentNullException(nameof(psi));
        ThrowIfDisposed();

        var wrapped = BuildWrappedStartInfo(psi);
        var process = Process.Start(wrapped);
        if (process != null)
        {
            try
            {
                AttachToProcess(process);
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                process.Dispose();
                throw;
            }
        }

        return process;
    }

    public void Dispose()
    {
        var processes = TakeTrackedProcesses(markDisposed: true);
        if (processes == null)
            return;

        TerminateAndDispose(processes);
    }

    private List<Process> TakeTrackedProcesses(bool markDisposed)
    {
        lock (_processLock)
        {
            if (markDisposed)
            {
                if (_disposed)
                    return null;
                _disposed = true;
            }
            else if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PosixCommandSandbox));
            }

            var processes = _activeProcesses.ToList();
            _activeProcesses.Clear();
            return processes;
        }
    }

    private static void TerminateAndDispose(IEnumerable<Process> processes)
    {
        if (processes == null)
            return;

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[PosixSandbox] Failed to terminate tracked process: {ex.Message}", LogCategory.Security);
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Debug($"[PosixSandbox] Failed to dispose tracked process: {ex.Message}", LogCategory.Security);
                }
            }
        }
    }

    internal static string TryResolveHelper(OSPlatformKind platform)
    {
        var helperName = platform switch
        {
            OSPlatformKind.Linux => "bwrap",
            OSPlatformKind.MacOS => "sandbox-exec",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(helperName))
            return null;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var path in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(path, helperName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private ProcessStartInfo BuildWrappedStartInfo(ProcessStartInfo childStartInfo)
    {
        var resolvedExecutable = ResolveExecutablePath(childStartInfo);
        var requestedWorkingDirectory = NormalizeDirectoryPath(string.IsNullOrWhiteSpace(childStartInfo.WorkingDirectory)
            ? Environment.CurrentDirectory
            : childStartInfo.WorkingDirectory);
        var sandboxWorkingDirectory = ResolveSandboxWorkingDirectory(requestedWorkingDirectory);

        var wrapped = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = false,
            CreateNoWindow = childStartInfo.CreateNoWindow,
            WorkingDirectory = sandboxWorkingDirectory,
            RedirectStandardInput = childStartInfo.RedirectStandardInput,
            RedirectStandardOutput = childStartInfo.RedirectStandardOutput,
            RedirectStandardError = childStartInfo.RedirectStandardError,
            WindowStyle = childStartInfo.WindowStyle
        };

        if (childStartInfo.StandardInputEncoding != null)
            wrapped.StandardInputEncoding = childStartInfo.StandardInputEncoding;
        if (childStartInfo.StandardOutputEncoding != null)
            wrapped.StandardOutputEncoding = childStartInfo.StandardOutputEncoding;
        if (childStartInfo.StandardErrorEncoding != null)
            wrapped.StandardErrorEncoding = childStartInfo.StandardErrorEncoding;

        wrapped.Environment.Clear();
        foreach (var kvp in BuildSanitizedEnvironment(childStartInfo.Environment))
        {
            wrapped.Environment[kvp.Key] = kvp.Value;
        }

        var helperArguments = _platform switch
        {
            OSPlatformKind.Linux => BuildBubblewrapArguments(childStartInfo, resolvedExecutable, sandboxWorkingDirectory),
            OSPlatformKind.MacOS => BuildSandboxExecArguments(childStartInfo, resolvedExecutable, sandboxWorkingDirectory),
            _ => throw new PlatformNotSupportedException($"Unsupported POSIX sandbox platform '{_platform}'.")
        };

        foreach (var argument in helperArguments)
        {
            wrapped.ArgumentList.Add(argument);
        }

        return wrapped;
    }

    private IReadOnlyList<string> BuildBubblewrapArguments(
        ProcessStartInfo childStartInfo,
        string resolvedExecutable,
        string sandboxWorkingDirectory)
    {
        var args = new List<string>
        {
            "--die-with-parent",
            "--new-session",
            "--unshare-all",
            "--hostname", "fen-sandbox",
            "--proc", "/proc",
            "--dev", "/dev",
            "--tmpfs", "/tmp",
            "--chdir", sandboxWorkingDirectory
        };

        foreach (var systemPath in GetSystemReadOnlyPaths())
        {
            AddBind(args, systemPath, writable: false);
        }

        // The broker's pipes are Unix sockets under the host temp directory and
        // frame/network shared memory is file-backed beside them; both sit below
        // the private tmpfs mounted above, so expose exactly those two
        // directories. Connecting to a socket needs no write access, so the pipe
        // directory stays read-only; the child publishes frames, so the shared
        // memory directory is writable (owner-only, same uid as the broker).
        AddBind(args, IpcPaths.PipeDirectory, writable: false);
        AddBind(args, IpcPaths.EnsureSharedMemoryDirectory(), writable: true);

        // fontconfig's system cache. Without it every child rescans the font
        // directories on start-up instead of loading the prebuilt cache.
        AddBind(args, "/var/cache/fontconfig", writable: false);

        if ((_profile.Capabilities & (OsSandboxCapabilities.NetworkOutbound | OsSandboxCapabilities.NetworkListen)) != 0)
        {
            // /etc/resolv.conf is routinely a symlink out of /etc - to
            // /run/systemd/resolve/stub-resolv.conf under systemd-resolved, to
            // /mnt/wsl/resolv.conf under WSL - and a read-only /etc alone leaves the
            // child with a dangling link and no resolver. Bind the file it points at.
            foreach (var resolverFile in GetResolverFilesOutsideSystemPaths())
            {
                AddBindFile(args, resolverFile);
            }
        }

        BindWorkingDirectory(args, sandboxWorkingDirectory);

        var executableDirectory = NormalizeDirectoryPath(Path.GetDirectoryName(resolvedExecutable));
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            AddBind(args, executableDirectory, writable: false);
        }

        if ((_profile.Capabilities & OsSandboxCapabilities.FileReadUser) != 0)
        {
            AddBind(args, _homeDirectory, writable: false);
        }

        if ((_profile.Capabilities & OsSandboxCapabilities.FileWrite) != 0)
        {
            AddBind(args, NormalizeDirectoryPath(_tempDirectory), writable: true);
        }

        if ((_profile.Capabilities & (OsSandboxCapabilities.NetworkOutbound | OsSandboxCapabilities.NetworkListen)) != 0)
        {
            args.Add("--share-net");
        }

        foreach (var kvp in BuildSanitizedEnvironment(childStartInfo.Environment))
        {
            args.Add("--setenv");
            args.Add(kvp.Key);
            args.Add(kvp.Value ?? string.Empty);
        }

        args.Add("--");
        args.Add(resolvedExecutable);
        args.AddRange(GetChildArguments(childStartInfo));
        return args;
    }

    private IReadOnlyList<string> BuildSandboxExecArguments(
        ProcessStartInfo childStartInfo,
        string resolvedExecutable,
        string sandboxWorkingDirectory)
    {
        var profile = new StringBuilder();
        profile.Append("(version 1) ");
        profile.Append("(deny default) ");
        profile.Append("(allow process-exec) ");

        if ((_profile.Capabilities & OsSandboxCapabilities.SpawnChildProcess) == 0)
        {
            profile.Append("(deny process-fork) ");
        }

        foreach (var path in GetSystemReadOnlyPaths())
        {
            AppendSandboxPathRule(profile, "file-read*", path);
        }

        if (ShouldExposeWorkingDirectoryToSandbox(sandboxWorkingDirectory))
        {
            AppendSandboxPathRule(profile, "file-read*", sandboxWorkingDirectory);
        }

        AppendSandboxPathRule(profile, "file-read*", NormalizeDirectoryPath(Path.GetDirectoryName(resolvedExecutable)));

        // Broker IPC: see BuildBubblewrapArguments for why these two are exposed
        // regardless of profile capabilities.
        AppendSandboxPathRule(profile, "file-read*", IpcPaths.PipeDirectory);
        AppendSandboxPathRule(profile, "file-read*", IpcPaths.EnsureSharedMemoryDirectory());
        AppendSandboxPathRule(profile, "file-write*", IpcPaths.SharedMemoryDirectory);

        if ((_profile.Capabilities & OsSandboxCapabilities.FileReadUser) != 0)
        {
            AppendSandboxPathRule(profile, "file-read*", _homeDirectory);
        }

        if ((_profile.Capabilities & OsSandboxCapabilities.FileWrite) != 0)
        {
            AppendSandboxPathRule(profile, "file-write*", NormalizeDirectoryPath(_tempDirectory));
        }

        if ((_profile.Capabilities & OsSandboxCapabilities.NetworkOutbound) != 0)
        {
            profile.Append("(allow network-outbound) ");
        }

        if ((_profile.Capabilities & OsSandboxCapabilities.NetworkListen) != 0)
        {
            profile.Append("(allow network-inbound) ");
        }

        var args = new List<string>
        {
            "-p",
            profile.ToString().Trim(),
            resolvedExecutable
        };
        args.AddRange(GetChildArguments(childStartInfo));
        return args;
    }

    private static IReadOnlyList<string> GetChildArguments(ProcessStartInfo childStartInfo)
    {
        if (!string.IsNullOrWhiteSpace(childStartInfo.Arguments))
        {
            throw new InvalidOperationException(
                "Sandboxed children must use ProcessStartInfo.ArgumentList instead of the legacy Arguments string.");
        }

        return childStartInfo.ArgumentList.ToArray();
    }

    private IDictionary<string, string> BuildSanitizedEnvironment(IDictionary<string, string?> source)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowList = new HashSet<string>(StringComparer.Ordinal)
        {
            "PATH",
            "LANG",
            "LC_ALL",
            "LC_CTYPE",
            "TZ"
        };

        if ((_profile.Capabilities & OsSandboxCapabilities.FileReadUser) != 0)
        {
            allowList.Add("HOME");
        }

        if ((_profile.Capabilities & OsSandboxCapabilities.FileWrite) != 0)
        {
            allowList.Add("TMPDIR");
            allowList.Add("TMP");
            allowList.Add("TEMP");
        }

        foreach (var kvp in source)
        {
            var key = kvp.Key;
            if (string.IsNullOrWhiteSpace(key) || !(allowList.Contains(key) || IsChildWiringVariable(key)))
            {
                continue;
            }

            environment[key] = kvp.Value ?? string.Empty;
        }

        if (!environment.ContainsKey("PATH"))
        {
            environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        }

        // Both sides derive the IPC directories from the temp path, so the child
        // must see the same one even when it may not write anywhere else in it.
        if (!environment.ContainsKey("TMPDIR"))
        {
            environment["TMPDIR"] = NormalizeDirectoryPath(_tempDirectory) ?? "/tmp";
        }

        if ((_profile.Capabilities & OsSandboxCapabilities.FileReadUser) != 0 &&
            !environment.ContainsKey("HOME") &&
            !string.IsNullOrWhiteSpace(_homeDirectory))
        {
            environment["HOME"] = _homeDirectory;
        }

        if ((_profile.Capabilities & OsSandboxCapabilities.FileWrite) != 0)
        {
            var normalizedTemp = NormalizeDirectoryPath(_tempDirectory) ?? "/tmp";
            environment["TMPDIR"] = normalizedTemp;
            environment["TMP"] = normalizedTemp;
            environment["TEMP"] = normalizedTemp;
        }

        return environment;
    }

    // The broker hands a child its identity through the environment: which pipe
    // to connect to, its auth token, tab id and parent pid (FEN_RENDERER_*,
    // FEN_NETWORK_*, FEN_TARGET_*), plus the diagnostic knobs
    // RendererChildEnvironment forwards on purpose. The apphost needs DOTNET_*
    // to find a runtime installed outside /usr, and a renderer on Linux needs
    // the display variables the host already chose to pass. The caller has
    // reset the child environment to a safe base before we see it; dropping
    // these here left the child with no way to reach the broker at all.
    private static bool IsChildWiringVariable(string key)
    {
        if (key.StartsWith("FEN_", StringComparison.Ordinal) ||
            key.StartsWith("DOTNET_", StringComparison.Ordinal))
        {
            return true;
        }

        return key is "DISPLAY" or "WAYLAND_DISPLAY" or "XDG_RUNTIME_DIR" or "XAUTHORITY";
    }

    private IEnumerable<string> GetSystemReadOnlyPaths()
    {
        if (_platform == OSPlatformKind.MacOS)
        {
            return new[]
            {
                "/System",
                "/usr",
                "/bin",
                "/sbin",
                "/Library",
                "/private/etc"
            };
        }

        return new[]
        {
            "/usr",
            "/bin",
            "/sbin",
            "/lib",
            "/lib64",
            "/etc"
        };
    }

    private IEnumerable<string> GetResolverFilesOutsideSystemPaths()
    {
        const string resolvConf = "/etc/resolv.conf";
        string target;
        try
        {
            target = File.ResolveLinkTarget(resolvConf, returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        if (string.IsNullOrEmpty(target) || !File.Exists(target))
        {
            yield break;
        }

        foreach (var systemPath in GetSystemReadOnlyPaths())
        {
            if (target.StartsWith(systemPath.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                yield break;
            }
        }

        yield return target;
    }

    private static void AddBindFile(List<string> args, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        args.Add("--ro-bind");
        args.Add(path);
        args.Add(path);
    }

    private static void AddBind(List<string> args, string path, bool writable)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        args.Add(writable ? "--bind" : "--ro-bind");
        args.Add(path);
        args.Add(path);
    }

    private static void AppendSandboxPathRule(StringBuilder profile, string operation, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        profile.Append("(allow ");
        profile.Append(operation);
        profile.Append(" (subpath ");
        profile.Append(QuoteSandboxString(path));
        profile.Append(")) ");
    }

    private static string QuoteSandboxString(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string ResolveExecutablePath(ProcessStartInfo psi)
    {
        if (Path.IsPathRooted(psi.FileName))
        {
            return psi.FileName;
        }

        var workingDirectory = string.IsNullOrWhiteSpace(psi.WorkingDirectory)
            ? Environment.CurrentDirectory
            : psi.WorkingDirectory;
        var directCandidate = Path.Combine(workingDirectory, psi.FileName);
        if (File.Exists(directCandidate))
        {
            return Path.GetFullPath(directCandidate);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var path in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(path, psi.FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return psi.FileName;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PosixCommandSandbox));
    }

    private void BindWorkingDirectory(List<string> args, string sandboxWorkingDirectory)
    {
        if ((_profile.Capabilities & OsSandboxCapabilities.FileReadUser) == 0)
        {
            return;
        }

        AddBind(args, sandboxWorkingDirectory, writable: false);
    }

    private string ResolveSandboxWorkingDirectory(string requestedWorkingDirectory)
    {
        if ((_profile.Capabilities & OsSandboxCapabilities.FileReadUser) == 0)
        {
            return "/tmp";
        }

        return requestedWorkingDirectory ?? "/tmp";
    }

    private bool ShouldExposeWorkingDirectoryToSandbox(string sandboxWorkingDirectory)
    {
        return ((_profile.Capabilities & OsSandboxCapabilities.FileReadUser) != 0) &&
               !string.IsNullOrWhiteSpace(sandboxWorkingDirectory) &&
               !string.Equals(sandboxWorkingDirectory, "/tmp", StringComparison.Ordinal);
    }
}
