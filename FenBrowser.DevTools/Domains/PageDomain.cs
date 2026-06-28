using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;

namespace FenBrowser.DevTools.Domains;

public sealed class PageDomain : IProtocolHandler
{
    private readonly IDevToolsHost _host;

    public PageDomain(IDevToolsHost host)
    {
        _host = host;
    }

    public string Domain => "Page";

    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "enable" or "disable" or "setLifecycleEventsEnabled" => Ok(request),
            "getFrameTree" => Task.FromResult(ProtocolResponse.Success(request.Id, new
            {
                frameTree = new
                {
                    frame = new
                    {
                        id = "fen-main-frame",
                        loaderId = "fen-main-loader",
                        url = _host.CurrentUrl ?? "about:blank",
                        securityOrigin = GetSecurityOrigin(_host.CurrentUrl),
                        mimeType = "text/html"
                    }
                }
            })),
            "getResourceTree" => Task.FromResult(ProtocolResponse.Success(request.Id, new
            {
                frameTree = new
                {
                    frame = new
                    {
                        id = "fen-main-frame",
                        loaderId = "fen-main-loader",
                        url = _host.CurrentUrl ?? "about:blank",
                        securityOrigin = GetSecurityOrigin(_host.CurrentUrl),
                        mimeType = "text/html"
                    },
                    resources = Array.Empty<object>()
                }
            })),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Unknown method: Page.{method}", -32601))
        };
    }

    private static Task<ProtocolResponse> Ok(ProtocolRequest request) =>
        Task.FromResult(ProtocolResponse.Success(request.Id, new { }));

    private static string GetSecurityOrigin(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.GetLeftPart(UriPartial.Authority);

        return "://";
    }
}
