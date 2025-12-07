using FenBrowser.Core.Logging;

namespace FenBrowser.Core;

/// <summary>
/// Simple logging abstraction.
/// </summary>
public interface ILogger
{
    void Log(LogLevel level, string message);
    void LogError(string message, System.Exception ex);
}
