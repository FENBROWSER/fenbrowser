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
            // Console color APIs can throw when no real console is attached, output is
            // redirected, or the host has already torn the console down. Diagnostics
            // must never become a browser crash path.
            var hasOriginalColor = TryGetForegroundColor(out var originalColor);
            try
            {
                if (hasOriginalColor)
                {
                    TrySetForegroundColor(level switch
                    {
                        LogLevel.Debug => ConsoleColor.Gray,
                        LogLevel.Info => ConsoleColor.White,
                        LogLevel.Warn => ConsoleColor.Yellow,
                        LogLevel.Error => ConsoleColor.Red,
                        _ => originalColor
                    });
                }

                TryWriteLine($"{DateTime.UtcNow:O} [{level}] {normalizedMessage}");
            }
            finally
            {
                if (hasOriginalColor)
                {
                    TrySetForegroundColor(originalColor);
                }
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
            TryWriteLine(ex.StackTrace);
        }
    }

    private static bool TryGetForegroundColor(out ConsoleColor color)
    {
        try
        {
            color = Console.ForegroundColor;
            return true;
        }
        catch
        {
            color = ConsoleColor.Gray;
            return false;
        }
    }

    private static void TrySetForegroundColor(ConsoleColor color)
    {
        try
        {
            Console.ForegroundColor = color;
        }
        catch
        {
            // Best-effort decoration only.
        }
    }

    private static void TryWriteLine(string message)
    {
        try
        {
            Console.WriteLine(message);
        }
        catch
        {
            // Logging is best effort and must not take down a headless/closing host.
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
