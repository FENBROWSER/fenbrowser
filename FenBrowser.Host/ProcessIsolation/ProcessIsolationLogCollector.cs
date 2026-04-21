using System;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation;

internal static class ProcessIsolationLogCollector
{
    public static void PublishBatch(EngineLogBatchPayload batch)
    {
        if (batch?.Events == null || batch.Events.Count == 0)
        {
            return;
        }

        var processKind = string.IsNullOrWhiteSpace(batch.ProcessKind)
            ? "child"
            : batch.ProcessKind;

        var tabId = batch.TabId > 0 ? batch.TabId.ToString() : null;

        foreach (var evt in batch.Events)
        {
            var context = evt.Header.Context;
            if (!string.IsNullOrWhiteSpace(tabId) && string.IsNullOrWhiteSpace(context.TabId))
            {
                context = context with { TabId = tabId };
            }

            if (string.IsNullOrWhiteSpace(context.Source))
            {
                context = context with { Source = processKind };
            }

            var normalized = evt with
            {
                Header = evt.Header with
                {
                    Context = context
                }
            };

            EngineLog.PublishExternalEvent(normalized);
        }
    }
}

