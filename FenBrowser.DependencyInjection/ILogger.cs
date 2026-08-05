using System;

namespace FenBrowser.DependencyInjection;

/// <summary>
/// Minimal logger interface for dependency injection.
/// </summary>
public interface ILogger
{
    void Log(LogLevel level, string message);
    void Log(LogLevel level, string message, Exception exception);
}

/// <summary>
/// Log levels.
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}