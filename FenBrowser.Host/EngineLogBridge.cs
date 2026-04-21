using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host;

internal static class EngineLogBridge
{
    public static IDisposable BeginScope(
        string component = null,
        string correlationId = null,
        IReadOnlyDictionary<string, object> data = null)
    {
        return LogContext.Push(component, correlationId, data);
    }

    public static void Debug(
        string message,
        LogCategory category = LogCategory.General,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        Write(LogSeverity.Debug, message, category, null, memberName, sourceFile, sourceLine);
    }

    public static void Info(
        string message,
        LogCategory category = LogCategory.General,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        Write(LogSeverity.Info, message, category, null, memberName, sourceFile, sourceLine);
    }

    public static void Warn(
        string message,
        LogCategory category = LogCategory.General,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        Write(LogSeverity.Warn, message, category, null, memberName, sourceFile, sourceLine);
    }

    public static void Error(
        string message,
        LogCategory category = LogCategory.General,
        Exception ex = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        Write(LogSeverity.Error, message, category, ex, memberName, sourceFile, sourceLine);
    }

    private static void Write(
        LogSeverity severity,
        string message,
        LogCategory category,
        Exception ex,
        string memberName,
        string sourceFile,
        int sourceLine)
    {
        var fields = LogContext.CaptureData() ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (ex != null)
        {
            fields["exception"] = ex.ToString();
        }

        var context = new EngineLogContext(
            NavigationId: LogContext.CurrentCorrelationId,
            Source: LogContext.CurrentComponent);

        EngineLog.Write(
            EngineLogCompatibility.FromLegacyCategory(category),
            severity,
            message ?? string.Empty,
            LogMarker.None,
            context,
            fields,
            sourceFile: string.IsNullOrWhiteSpace(sourceFile) ? null : System.IO.Path.GetFileName(sourceFile),
            sourceLine: sourceLine,
            sourceMember: memberName);
    }
}

