using FenBrowser.DevTools.Core.Protocol;

namespace FenBrowser.DevTools.Domains;

public sealed class OverlayDomain : IProtocolHandler
{
    public string Domain => "Overlay";

    public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
    {
        return method switch
        {
            "enable" or "disable" or "hideHighlight" or "highlightNode" or "highlightRect" or
            "setShowViewportSizeOnResize" or "setShowAdHighlights" or "setShowDebugBorders" or
            "setShowFPSCounter" or "setShowPaintRects" or "setShowLayoutShiftRegions" or
            "setShowScrollBottleneckRects" or "setShowHitTestBorders" or "setInspectMode" =>
                Task.FromResult(ProtocolResponse.Success(request.Id, new { })),
            _ => Task.FromResult(ProtocolResponse.Failure(request.Id, $"Unknown method: Overlay.{method}", -32601))
        };
    }
}
