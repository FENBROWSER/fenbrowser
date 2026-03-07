using System.Text.Json;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;

namespace FenBrowser.DevTools.Domains;

/// <summary>
/// Handler for the Runtime domain (Console, evaluating scripts).
/// </summary>
public class RuntimeDomain : IProtocolHandler
{
    public string Domain => "Runtime";

    private readonly IDevToolsHost _host;
    private bool _enabled;

    public RuntimeDomain(IDevToolsHost host)
    {
        _host = host;
    }

    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "enable" => EnableAsync(request),
            "disable" => DisableAsync(request),
            "evaluate" => EvaluateAsync(request),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Unknown method: Runtime.{method}"))
        };
    }

    private Task<ProtocolResponse> EnableAsync(ProtocolRequest request)
    {
        _enabled = true;
        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }

    private Task<ProtocolResponse> DisableAsync(ProtocolRequest request)
    {
        _enabled = false;
        return Task.FromResult(ProtocolResponse.Success(request.Id, new { }));
    }

    private async Task<ProtocolResponse> EvaluateAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return ProtocolResponse.Failure(request.Id, "Params required");
        }

        try
        {
            var expression = request.Params.Value.GetProperty("expression").GetString();
            if (string.IsNullOrEmpty(expression))
            {
                return ProtocolResponse.Failure(request.Id, "Expression required");
            }

            var result = await _host.EvaluateScriptAsync(expression).ConfigureAwait(false);

            var remoteObject = new RemoteObject
            {
                Type = GetTypeString(result),
                Value = result,
                Description = result?.ToString() ?? "undefined"
            };

            return ProtocolResponse.Success(request.Id, new EvaluateResult
            {
                Result = remoteObject
            });
        }
        catch (Exception ex)
        {
            return ProtocolResponse.Failure(request.Id, $"Eval error: {ex.Message}");
        }
    }

    private string GetTypeString(object? obj)
    {
        if (obj == null) return "undefined";
        if (obj is string) return "string";
        if (obj is bool) return "boolean";
        if (obj is int || obj is long || obj is double || obj is float || obj is decimal) return "number";
        return "object";
    }
}
