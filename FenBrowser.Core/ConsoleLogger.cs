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
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static string NormalizeMessage(string message)
    {
        return string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
    }
}
