using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;

namespace FenBrowser.Tests.Core;

public sealed class IframeInputRetargetingTests
{
    [Fact]
    public async Task BrowserHostClick_RetargetsVisualPointIntoIframeDocument()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 360;
        const string html = """
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    iframe {
      display: block;
      width: 300px;
      height: 120px;
      margin-left: 40px;
      margin-top: 50px;
      border: 0;
    }
  </style>
</head>
<body>
  <iframe id="challenge-frame"></iframe>
  <script>
    var frame = document.getElementById('challenge-frame');
    var doc = frame.contentDocument;
    doc.open();
    doc.write('<!doctype html><html><head><style>body{margin:0}#anchor{display:block;width:120px;height:40px;margin-left:20px;margin-top:10px}</style></head><body><button id="anchor" type="button">check</button></body></html>');
    doc.close();
    doc.getElementById('anchor').addEventListener('click', function () {
      doc.body.setAttribute('data-clicked', 'yes');
    });
  </script>
</body>
</html>
""";

        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);

        await host.Engine.RenderAsync(
            html,
            new Uri("https://fen.test/iframe-click"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth: viewportWidth,
            viewportHeight: viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var iframe = FindById(root, "challenge-frame");
        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);
        var button = frameDocument.GetElementById("anchor");
        Assert.NotNull(button);

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);

        Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var iframeRect), "Missing iframe layout rect.");
        Assert.True(renderer.LastLayout.TryGetElementRect(button, out var buttonRect), "Missing iframe button layout rect.");

        var visualX = iframeRect.Left + buttonRect.Left + (buttonRect.Width / 2f);
        var visualY = iframeRect.Top + buttonRect.Top + (buttonRect.Height / 2f);

        host.OnClick(visualX, visualY, button: 0);

        Assert.Equal("yes", frameDocument.Body?.GetAttribute("data-clicked"));
    }

    private static void RenderFrame(
        SkiaDomRenderer renderer,
        Element root,
        System.Collections.Generic.Dictionary<Node, CssComputed> styles,
        int viewportWidth,
        int viewportHeight)
    {
        using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
        using var canvas = new SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
            BaseUrl = "https://fen.test/iframe-click",
            InvalidationReason = RenderFrameInvalidationReason.Navigation,
            RequestedBy = "IframeInputRetargetingTests",
            EmitVerificationReport = false
        });
        canvas.Flush();
    }

    private static Element FindById(Element root, string id)
    {
        return root.Descendants()
            .OfType<Element>()
            .First(e => string.Equals(e.GetAttribute("id"), id, StringComparison.Ordinal));
    }
}
