using System;
using System.Collections.Generic;
using FenBrowser.Core.Logging;
using FenBrowser.Media.Diagnostics;

namespace FenBrowser.FenEngine.Media
{
    /// <summary>
    /// Routes media engine events into the engine log under <see cref="LogSubsystem.Media"/>
    /// (ADR-0006), keeping the event kind, player and fields as structured data.
    /// </summary>
    public sealed class EngineLogMediaSink : IMediaLogSink
    {
        public static readonly EngineLogMediaSink Instance = new EngineLogMediaSink();

        private EngineLogMediaSink()
        {
        }

        public bool IsEnabled(MediaLogLevel level) =>
            EngineLog.IsEnabled(LogSubsystem.Media, ToSeverity(level));

        public void Log(in MediaLogEvent logEvent)
        {
            var severity = ToSeverity(logEvent.Level);
            if (!EngineLog.IsEnabled(LogSubsystem.Media, severity))
                return;

            int count = logEvent.Fields?.Count ?? 0;
            var fields = new Dictionary<string, object>(count + 2, StringComparer.Ordinal)
            {
                ["mediaEvent"] = logEvent.Kind.ToString(),
                ["playerId"] = logEvent.Player.Value,
            };
            if (logEvent.Fields != null)
            {
                foreach (var pair in logEvent.Fields)
                {
                    // Event fields never overwrite the identity keys above.
                    fields.TryAdd(pair.Key, pair.Value);
                }
            }

            EngineLog.Write(
                LogSubsystem.Media,
                severity,
                $"[Media] {logEvent.Player} {logEvent.Kind}: {logEvent.Message}",
                LogMarker.None,
                default,
                fields);
        }

        internal static LogSeverity ToSeverity(MediaLogLevel level) => level switch
        {
            MediaLogLevel.Debug => LogSeverity.Debug,
            MediaLogLevel.Info => LogSeverity.Info,
            MediaLogLevel.Warn => LogSeverity.Warn,
            MediaLogLevel.Error => LogSeverity.Error,
            _ => LogSeverity.Info,
        };
    }
}
