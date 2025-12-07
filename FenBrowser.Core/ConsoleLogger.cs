using System;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core;

public class ConsoleLogger : ILogger
{
    public void Log(LogLevel level, string message)
    {
        var color = Console.ForegroundColor;
        switch (level)
        {
            case LogLevel.Debug: Console.ForegroundColor = ConsoleColor.Gray; break;
            case LogLevel.Info: Console.ForegroundColor = ConsoleColor.White; break;
            case LogLevel.Warn: Console.ForegroundColor = ConsoleColor.Yellow; break;
            case LogLevel.Error: Console.ForegroundColor = ConsoleColor.Red; break;
        }
        Console.WriteLine($"[{level}] {message}");
        Console.ForegroundColor = color;
    }

    public void LogError(string message, Exception ex)
    {
        Log(LogLevel.Error, $"{message}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
}
