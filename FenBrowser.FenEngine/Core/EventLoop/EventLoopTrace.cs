using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Core.EventLoop
{
    internal static class EventLoopTrace
    {
        private static long s_nextTraceId;

        public static string NextId(string prefix)
        {
            var id = Interlocked.Increment(ref s_nextTraceId);
            return (string.IsNullOrWhiteSpace(prefix) ? "event-loop" : prefix) + "-" +
                   id.ToString(CultureInfo.InvariantCulture);
        }

        public static void Write(
            string eventName,
            LogSeverity severity,
            string message,
            string taskId = null,
            IReadOnlyDictionary<string, object> fields = null,
            LogMarker marker = LogMarker.None,
            [CallerFilePath] string sourceFile = "",
            [CallerLineNumber] int sourceLine = 0,
            [CallerMemberName] string sourceMember = "")
        {
            var payload = fields != null
                ? new Dictionary<string, object>(fields, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            payload["event"] = eventName ?? string.Empty;
            payload["eventName"] = eventName ?? string.Empty;
            payload["traceCategory"] = "EventLoop";
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                payload["taskId"] = taskId;
            }

            EngineLog.Write(
                LogSubsystem.Event,
                severity,
                message,
                marker,
                new EngineLogContext(
                    NavigationId: LogContext.CurrentCorrelationId,
                    TaskId: taskId),
                payload,
                sourceFile,
                sourceLine,
                sourceMember);
        }
    }
}
