using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace FenBrowser.Tooling;

internal sealed class WebDriverLifecycleDiagnostics : IDisposable
{
    internal const string ArtifactDirectoryEnvironmentVariable = "FEN_WPT_ARTIFACT_DIR";

    private readonly object _sync = new();
    private readonly string _path;
    private readonly int _port;
    private readonly bool _globalHandlersRegistered;
    private int _terminalEventWritten;
    private bool _disposed;

    private WebDriverLifecycleDiagnostics(int port, string path, bool registerGlobalHandlers)
    {
        _port = port;
        _path = path;
        _globalHandlersRegistered = registerGlobalHandlers;

        if (registerGlobalHandlers)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            Console.CancelKeyPress += OnCancelKeyPress;
        }

        Record("process_started", new
        {
            commandLine = Environment.CommandLine,
            workingDirectory = SafeGetCurrentDirectory(),
            processPath = Environment.ProcessPath,
            runtime = Environment.Version.ToString()
        });
    }

    public string Path => _path;

    public static WebDriverLifecycleDiagnostics Start(
        int port,
        string? artifactDirectory = null,
        bool registerGlobalHandlers = true)
    {
        var root = artifactDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetEnvironmentVariable(ArtifactDirectoryEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = FenBrowser.Core.Logging.DiagnosticPaths.GetLogsDirectory();
        }

        var directory = System.IO.Path.Combine(System.IO.Path.GetFullPath(root), "webdriver");
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // Diagnostics must never prevent the WebDriver remote end starting.
        }
        var path = System.IO.Path.Combine(
            directory,
            $"lifecycle-port{port}-pid{Environment.ProcessId}.jsonl");
        return new WebDriverLifecycleDiagnostics(port, path, registerGlobalHandlers);
    }

    public void Record(string eventName, object? data = null)
    {
        if (_disposed)
        {
            return;
        }

        var entry = new
        {
            schemaVersion = 1,
            timestampUtc = DateTimeOffset.UtcNow,
            eventName,
            processId = Environment.ProcessId,
            port = _port,
            threadId = Environment.CurrentManagedThreadId,
            data
        };
        try
        {
            var line = JsonSerializer.Serialize(entry);
            lock (_sync)
            {
                File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Crash diagnostics are best-effort and cannot become a second failure.
        }
    }

    public void RecordCleanStop()
    {
        if (Interlocked.Exchange(ref _terminalEventWritten, 1) == 0)
        {
            Record("process_stopped", new { exitCode = Environment.ExitCode });
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        Interlocked.Exchange(ref _terminalEventWritten, 1);
        Record(
            "unhandled_exception",
            ExceptionData(args.ExceptionObject as Exception, args.IsTerminating));
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Record("unobserved_task_exception", ExceptionData(args.Exception));
        args.SetObserved();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        Record("cancel_key_press", new { cancelKey = args.SpecialKey.ToString() });
    }

    private void OnProcessExit(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _terminalEventWritten, 1) == 0)
        {
            Record("process_exit", new { exitCode = Environment.ExitCode });
        }
    }

    private static object ExceptionData(Exception? exception, bool? isTerminating = null)
        => new
        {
            isTerminating,
            type = exception?.GetType().FullName,
            exception?.Message,
            exception?.StackTrace,
            innerException = exception?.InnerException?.ToString()
        };

    private static string SafeGetCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        RecordCleanStop();
        if (_globalHandlersRegistered)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        _disposed = true;
    }
}
