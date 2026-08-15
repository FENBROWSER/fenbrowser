using System;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core;

public class ConsoleLogger : ILogger
{
    private static readonly object Sync = new();

    public void Log(LogLevel level, string message)
    {
        string normalizedMessage = NormalizeMessage(message);
        lock (Sync)
        {
            var color = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = level switch
                {
                    LogLevel.Debug => ConsoleColor.Gray,
                    LogLevel.Info => ConsoleColor.White,
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Error => ConsoleColor.Red,
                    _ => color
                };

                Console.WriteLine($"{DateTime.UtcNow:O} [{level}] {normalizedMessage}");
            }
            finally
            {
                Console.ForegroundColor = color;
            }
        }
    }

    public void LogError(string message, Exception ex)
    {
        if (ex == null)
        {
            throw new ArgumentNullException(nameof(ex));
        }

        Log(LogLevel.Error, $"{NormalizeMessage(message)}: {ex.GetType().Name}: {NormalizeMessage(ex.Message)}");
        if (string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            return;
        }

        lock (Sync)
        {
            // Stack traces are intentionally multi-line engine-generated diagnostics.
            // User/page-controlled message fields are normalized above before they
            // can enter the structured prefix.
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        // Browser logs routinely include attacker-controlled URL/header/DOM text.
        // Do not let CR/LF/NUL characters manufacture additional timestamped-looking
        // records or truncate downstream text consumers.
        return message.Trim()
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\0", "\\0", StringComparison.Ordinal);
    }
}
