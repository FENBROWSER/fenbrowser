namespace FenBrowser.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// Simple logging abstraction.
/// </summary>
public interface ILogger
{
    void Log(LogLevel level, string message);
    void LogError(string message, System.Exception ex);
}
