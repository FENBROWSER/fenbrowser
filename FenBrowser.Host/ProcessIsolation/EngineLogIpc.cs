using System.Collections.Generic;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation;

public sealed class EngineLogBatchPayload
{
    public string ProcessKind { get; set; }
    public int TabId { get; set; }
    public List<EngineLogEvent> Events { get; set; } = new();
}

