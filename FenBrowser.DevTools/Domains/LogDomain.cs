using FenBrowser.Core.Logging;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FenBrowser.DevTools.Domains;

public class LogDomain : IProtocolHandler
{
    public string Domain => "Log";

    private readonly Action<string, object>? _broadcastEvent;
    private readonly Action<EngineLogEvent> _handler;
    private bool _enabled;
    private LogSubscriptionFilter? _filter;

    public LogDomain(Action<string, object>? broadcastEvent)
    {
        _broadcastEvent = broadcastEvent;
        _handler = OnEngineLogEvent;
    }

    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "enable" => EnableAsync(request),
            "disable" => DisableAsync(request),
            "clear" => ClearAsync(request),
            "getCounters" => GetCountersAsync(request),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Unknown method: Log.{method}"))
        };
    }

    private Task<ProtocolResponse> EnableAsync(ProtocolRequest request)
    {
        if (_enabled)
        {
            _filter = ParseFilter(request.Params);
            return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
        }

        _filter = ParseFilter(request.Params);
        _enabled = true;
        EngineLog.EngineEventWritten += _handler;
        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }

    private Task<ProtocolResponse> DisableAsync(ProtocolRequest request)
    {
        if (_enabled)
        {
            EngineLog.EngineEventWritten -= _handler;
        }

        _enabled = false;
        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }

    private Task<ProtocolResponse> ClearAsync(ProtocolRequest request)
    {
        EngineLog.ClearCompatibilityBuffer();
        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }

    private Task<ProtocolResponse> GetCountersAsync(ProtocolRequest request)
    {
        var snapshots = EngineLog.GetPerDocumentCounters();
        var payload = new LogCountersPayload
        {
            Documents = snapshots.Select(counter => new LogDocumentCounterEntry
            {
                DocumentKey = counter.DocumentKey,
                TotalCount = counter.TotalCount,
                WarnCount = counter.WarnCount,
                ErrorCount = counter.ErrorCount,
                FatalCount = counter.FatalCount,
                LastSequence = counter.LastSequence,
                LastTimestampUtc = counter.LastTimestampUtc.ToString("O"),
                LastUrl = counter.LastUrl,
                LastTabId = counter.LastTabId,
                LastFrameId = counter.LastFrameId
            }).ToArray()
        };

        return Task.FromResult(ProtocolResponse.Success(request.Id, payload));
    }

    private void OnEngineLogEvent(EngineLogEvent evt)
    {
        if (!_enabled || _broadcastEvent == null)
        {
            return;
        }

        if (_filter != null && !_filter.Matches(evt))
        {
            return;
        }

        var payload = new LogEntryPayload
        {
            TimestampUtc = evt.Header.TimestampUtc.ToString("O"),
            Sequence = evt.Header.Sequence,
            ProcessId = evt.Header.ProcessId,
            ThreadId = evt.Header.ThreadId,
            Subsystem = evt.Header.Subsystem.ToString(),
            Severity = evt.Header.Severity.ToString(),
            Marker = evt.Header.Marker.ToString(),
            Message = evt.Payload?.MessageTemplate ?? string.Empty,
            SourceFile = evt.Payload?.SourceFile,
            SourceLine = evt.Payload?.SourceLine ?? 0,
            SourceMember = evt.Payload?.SourceMember,
            Context = evt.Header.Context,
            Fields = evt.Payload?.Fields
        };

        _broadcastEvent("Log.entryAdded", new LogEntryAddedEvent { Entry = payload });
    }

    private static LogSubscriptionFilter? ParseFilter(JsonElement? rawParams)
    {
        if (!rawParams.HasValue || rawParams.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new LogSubscriptionFilter();
        var filterFound = false;
        var paramsObj = rawParams.Value;

        if (paramsObj.TryGetProperty("filter", out var filterElement) && filterElement.ValueKind == JsonValueKind.Object)
        {
            if (filterElement.TryGetProperty("subsystems", out var subsystemsElement) && subsystemsElement.ValueKind == JsonValueKind.Array)
            {
                var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in subsystemsElement.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        values.Add(value);
                    }
                }

                if (values.Count > 0)
                {
                    result.Subsystems = values;
                    filterFound = true;
                }
            }

            if (filterElement.TryGetProperty("tabId", out var tabIdElement))
            {
                var tabId = JsonToString(tabIdElement);
                if (!string.IsNullOrWhiteSpace(tabId))
                {
                    result.TabId = tabId;
                    filterFound = true;
                }
            }

            if (filterElement.TryGetProperty("frameId", out var frameIdElement))
            {
                var frameId = JsonToString(frameIdElement);
                if (!string.IsNullOrWhiteSpace(frameId))
                {
                    result.FrameId = frameId;
                    filterFound = true;
                }
            }
        }

        return filterFound ? result : null;
    }

    private static string? JsonToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private sealed class LogSubscriptionFilter
    {
        public HashSet<string>? Subsystems { get; set; }
        public string? TabId { get; set; }
        public string? FrameId { get; set; }

        public bool Matches(in EngineLogEvent evt)
        {
            if (Subsystems != null && Subsystems.Count > 0 && !Subsystems.Contains(evt.Header.Subsystem.ToString()))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(TabId) &&
                !string.Equals(TabId, evt.Header.Context.TabId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(FrameId) &&
                !string.Equals(FrameId, evt.Header.Context.FrameId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
    }
}
