using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;

namespace FenBrowser.DevTools.Domains;

/// <summary>
/// FenBrowser-native protocol domain for diagnostics that do not map cleanly to CDP.
/// </summary>
public sealed class FenBrowserDomain : IProtocolHandler
{
    private readonly IDevToolsHost _host;

    public FenBrowserDomain(IDevToolsHost host)
    {
        _host = host;
    }

    public string Domain => "FenBrowser";

    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "getNodeDiagnostics" => GetNodeDiagnosticsAsync(request),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Unknown method: FenBrowser.{method}"))
        };
    }

    private Task<ProtocolResponse> GetNodeDiagnosticsAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "nodeId required"));
        }

        try
        {
            var nodeId = request.Params.Value.GetProperty("nodeId").GetInt32();
            var diagnostics = _host.GetNodeDiagnostics(nodeId);
            if (diagnostics == null)
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "Node not found"));
            }

            return Task.FromResult(ProtocolResponse.Success(request.Id, diagnostics));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, $"Diagnostics error: {ex.Message}"));
        }
    }
}
