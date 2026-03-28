using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;

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
            "getResponseBody" => GetResponseBodyAsync(request),
            "getRequestPostData" => GetRequestPostDataAsync(request),
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

    private Task<ProtocolResponse> GetResponseBodyAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        }

        try
        {
            var requestId = request.Params.Value.GetProperty("requestId").GetString();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "requestId required"));
            }

            var networkRequest = _host.GetNetworkRequests().FirstOrDefault(r => r.Id == requestId);
            if (networkRequest == null)
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "Request not found"));
            }

            if (networkRequest.ResponseBody == null)
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "Response body unavailable"));
            }

            return Task.FromResult(ProtocolResponse.Success(request.Id, new GetResponseBodyResult
            {
                Body = networkRequest.ResponseBody,
                Base64Encoded = false
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, $"Network error: {ex.Message}"));
        }
    }

    private Task<ProtocolResponse> GetRequestPostDataAsync(ProtocolRequest request)
    {
        if (request.Params == null)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, "Params required"));
        }

        try
        {
            var requestId = request.Params.Value.GetProperty("requestId").GetString();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "requestId required"));
            }

            var networkRequest = _host.GetNetworkRequests().FirstOrDefault(r => r.Id == requestId);
            if (networkRequest == null)
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "Request not found"));
            }

            if (networkRequest.RequestBody == null)
            {
                return Task.FromResult(ProtocolResponse.Failure(request.Id, "Request body unavailable"));
            }

            return Task.FromResult(ProtocolResponse.Success(request.Id, new GetRequestPostDataResult
            {
                PostData = networkRequest.RequestBody
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProtocolResponse.Failure(request.Id, $"Network error: {ex.Message}"));
        }
    }
}
