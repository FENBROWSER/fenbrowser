using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using FenBrowser.Core.Platform;

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
            _activeProcesses.Add(process);
        }
    }

    public void Kill()
    {
        ThrowIfDisposed();

        List<Process> processes;
        lock (_processLock)
        {
            processes = _activeProcesses.ToList();
            _activeProcesses.Clear();
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
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
            AttachToProcess(process);
        }

        return process;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();

        lock (_processLock)
        {
            foreach (var process in _activeProcesses)
            {
                try { process.Dispose(); } catch { }
            }

            _activeProcesses.Clear();
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
            WorkingDirectory = sandboxWorkingDirectory
        };

        wrapped.Environment.Clear();
        foreach (var kvp in BuildSanitizedEnvironment(childStartInfo.Environment))
        {
            wrapped.Environment[kvp.Key] = kvp.Value;
        }

        wrapped.Arguments = _platform switch
        {
            OSPlatformKind.Linux => BuildBubblewrapArguments(childStartInfo, resolvedExecutable, sandboxWorkingDirectory),
            OSPlatformKind.MacOS => BuildSandboxExecArguments(childStartInfo, resolvedExecutable, sandboxWorkingDirectory),
            _ => throw new PlatformNotSupportedException($"Unsupported POSIX sandbox platform '{_platform}'.")
        };

        return wrapped;
    }

    private string BuildBubblewrapArguments(ProcessStartInfo childStartInfo, string resolvedExecutable, string sandboxWorkingDirectory)
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
            AddBind(args, _homeDirectory, writable: true);
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
        if (!string.IsNullOrWhiteSpace(childStartInfo.Arguments))
            args.Add(childStartInfo.Arguments);

        return JoinArguments(args);
    }

    private string BuildSandboxExecArguments(ProcessStartInfo childStartInfo, string resolvedExecutable, string sandboxWorkingDirectory)
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

        if ((_profile.Capabilities & OsSandboxCapabilities.FileReadUser) != 0)
        {
            AppendSandboxPathRule(profile, "file-read*", _homeDirectory);
        }

        if ((_profile.Capabilities & OsSandboxCapabilities.FileWrite) != 0)
        {
            AppendSandboxPathRule(profile, "file-write*", sandboxWorkingDirectory);
            AppendSandboxPathRule(profile, "file-write*", NormalizeDirectoryPath(_tempDirectory));
            AppendSandboxPathRule(profile, "file-write*", _homeDirectory);
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

        if (!string.IsNullOrWhiteSpace(childStartInfo.Arguments))
            args.Add(childStartInfo.Arguments);

        return JoinArguments(args);
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

        if ((_profile.Capabilities & (OsSandboxCapabilities.FileReadUser | OsSandboxCapabilities.FileWrite)) != 0)
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
            if (string.IsNullOrWhiteSpace(key) || !allowList.Contains(key))
            {
                continue;
            }

            environment[key] = kvp.Value ?? string.Empty;
        }

        if (!environment.ContainsKey("PATH"))
        {
            environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        }

        if ((_profile.Capabilities & (OsSandboxCapabilities.FileReadUser | OsSandboxCapabilities.FileWrite)) != 0 &&
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

    private static string JoinArguments(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        if (!value.Contains(' ') && !value.Contains('"'))
            return value;

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PosixCommandSandbox));
    }

    private void BindWorkingDirectory(List<string> args, string sandboxWorkingDirectory)
    {
        if ((_profile.Capabilities & (OsSandboxCapabilities.FileReadUser | OsSandboxCapabilities.FileWrite)) == 0)
        {
            return;
        }

        AddBind(args, sandboxWorkingDirectory, writable: (_profile.Capabilities & OsSandboxCapabilities.FileWrite) != 0);
    }

    private string ResolveSandboxWorkingDirectory(string requestedWorkingDirectory)
    {
        if ((_profile.Capabilities & (OsSandboxCapabilities.FileReadUser | OsSandboxCapabilities.FileWrite)) == 0)
        {
            return "/tmp";
        }

        return requestedWorkingDirectory ?? "/tmp";
    }

    private bool ShouldExposeWorkingDirectoryToSandbox(string sandboxWorkingDirectory)
    {
        return ((_profile.Capabilities & (OsSandboxCapabilities.FileReadUser | OsSandboxCapabilities.FileWrite)) != 0) &&
               !string.IsNullOrWhiteSpace(sandboxWorkingDirectory) &&
               !string.Equals(sandboxWorkingDirectory, "/tmp", StringComparison.Ordinal);
    }
}
