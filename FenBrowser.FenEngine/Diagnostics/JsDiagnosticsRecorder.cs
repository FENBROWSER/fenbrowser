// SpecRef: internal diagnostics — no spec. Captures per-load JS console
// output and uncaught script exceptions to a single human-readable file so
// failures on bot-fenced sites (x.com's "something went wrong") can be
// diagnosed without attaching a CDP client.
// CapabilityId: DIAGNOSTICS-JS-01

using System;
using System.Globalization;
using System.IO;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Diagnostics
{
    /// <summary>
    /// Process-wide sink for JS console output and uncaught script exceptions.
    /// Writes one line per event to <c>logs/js_diagnostics.log</c>. Cheap and
    /// best-effort — failures inside the recorder are swallowed, never
    /// allowed to break a navigation.
    /// </summary>
    public static class JsDiagnosticsRecorder
    {
        private const string LogFileName = "js_diagnostics.log";
        private static readonly object s_lock = new();
        private static bool s_announcedSession;

        public static string LogFilePath => DiagnosticPaths.GetLogArtifactPath(LogFileName);

        public static void RecordConsole(string level, string message, string source)
        {
            if (string.IsNullOrEmpty(message)) return;
            Write("console." + (level ?? "log"), source, message);
        }

        public static void RecordException(FenValue thrown, string source, string contextUrl)
        {
            string detail;
            try
            {
                detail = thrown.IsString ? thrown.ToString()
                       : thrown.IsError ? (thrown.AsError() ?? thrown.ToString())
                       : thrown.ToString();
            }
            catch
            {
                detail = "<unprintable value>";
            }

            string stack = null;
            try
            {
                if (thrown.IsObject && thrown.AsObject() is FenObject obj)
                {
                    var stackVal = obj.Get("stack", null);
                    if (stackVal.IsString) stack = stackVal.ToString();
                }
            }
            catch
            {
                // Stack extraction is best-effort.
            }

            var combined = string.IsNullOrEmpty(stack)
                ? detail
                : detail + Environment.NewLine + stack;

            Write("uncaught", source ?? contextUrl, combined);
        }

        private static void Write(string kind, string source, string message)
        {
            try
            {
                lock (s_lock)
                {
                    if (!s_announcedSession)
                    {
                        AppendLine($"==== session start {DateTimeOffset.UtcNow:O} pid={System.Diagnostics.Process.GetCurrentProcess().Id} ====");
                        s_announcedSession = true;
                    }

                    var timestamp = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    var safeSource = string.IsNullOrEmpty(source) ? "-" : source;
                    var safeMessage = (message ?? string.Empty).Replace('\r', ' ');
                    AppendLine($"{timestamp} [{kind}] {safeSource} | {safeMessage}");
                }
            }
            catch
            {
                // Diagnostics must never break a navigation.
            }
        }

        private static void AppendLine(string line)
        {
            // ResilientFileWriter exists but pulls in extra dependencies — for
            // the diagnostics path we keep things minimal: a single File.AppendAllText.
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
    }
}
