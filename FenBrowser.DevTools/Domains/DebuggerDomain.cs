using System.Security.Cryptography;
using System.Text;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;

namespace FenBrowser.DevTools.Domains;

/// <summary>
/// Handler for the Debugger domain.
/// </summary>
public class DebuggerDomain : IProtocolHandler
{
    public string Domain => "Debugger";

    private readonly IDevToolsHost _host;
    private readonly Action<string, object>? _broadcastEvent;
    private bool _enabled;

    public DebuggerDomain(IDevToolsHost host, Action<string, object>? broadcastEvent = null)
    {
        _host = host;
        _broadcastEvent = broadcastEvent;
    }

    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "enable" => EnableAsync(request),
            "disable" => DisableAsync(request),
            "getScriptSource" => GetScriptSourceAsync(request),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Unknown method: Debugger.{method}"))
        };
    }

    private Task<ProtocolResponse> EnableAsync(ProtocolRequest request)
    {
        _enabled = true;

        foreach (var script in _host.GetScriptSources())
        {
            BroadcastScriptParsed(script);
        }

        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }

    private Task<ProtocolResponse> DisableAsync(ProtocolRequest request)
    {
        _enabled = false;
        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }

    private Task<ProtocolResponse> GetScriptSourceAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        }

        try
        {
            var scriptId = request.Params.Value.GetProperty("scriptId").GetString();
            if (string.IsNullOrWhiteSpace(scriptId))
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "scriptId required"));
            }

            var source = _host.GetScriptSources().FirstOrDefault(script => string.Equals(script.ScriptId, scriptId, StringComparison.Ordinal));
            if (source == null)
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "Script not found"));
            }

            return Task.FromResult(ProtocolResponse.Success(request.Id, new GetScriptSourceResult
            {
                ScriptSource = source.Content
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, $"Debugger error: {ex.Message}"));
        }
    }

    private void BroadcastScriptParsed(ScriptSourceInfo script)
    {
        if (!_enabled || _broadcastEvent == null || string.IsNullOrWhiteSpace(script.ScriptId))
        {
            return;
        }

        var lines = script.Content?.Replace("\r\n", "\n").Split('\n') ?? Array.Empty<string>();
        var lastLineLength = lines.Length == 0 ? 0 : lines[^1].Length;

        _broadcastEvent("Debugger.scriptParsed", new ScriptParsedEvent
        {
            ScriptId = script.ScriptId,
            Url = script.Url ?? string.Empty,
            StartLine = script.StartLine,
            StartColumn = script.StartColumn,
            EndLine = Math.Max(0, lines.Length - 1),
            EndColumn = lastLineLength,
            Hash = ComputeContentHash(script.Content ?? string.Empty),
            HasSourceUrl = !string.IsNullOrWhiteSpace(script.Url),
            IsModule = false,
            Length = script.Content?.Length ?? 0
        });
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
