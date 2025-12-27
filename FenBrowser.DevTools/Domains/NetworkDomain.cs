using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;

namespace FenBrowser.DevTools.Domains;

/// <summary>
/// Handler for the Network domain.
/// </summary>
public class NetworkDomain : IProtocolHandler
{
    public string Domain => "Network";

    private readonly IDevToolsHost _host;
    private bool _enabled;

    public NetworkDomain(IDevToolsHost host)
    {
        _host = host;
    }

    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "enable" => EnableAsync(request),
            "disable" => DisableAsync(request),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Unknown method: Network.{method}"))
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
}
