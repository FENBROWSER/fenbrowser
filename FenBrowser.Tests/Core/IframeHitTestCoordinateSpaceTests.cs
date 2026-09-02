using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Interaction;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

// LayoutEngine lays a frame's document out at (0,0) and then shifts every box by
// the frame's content origin, so frame content is stored in PARENT-absolute space.
// Hit testing subtracted that origin again before matching against those boxes,
// which is off by the full frame offset. It only ever connected through a
// second-chance retry with the untranslated point — and where the shifted point
// happened to land on something, it picked the wrong element.
public sealed class IframeHitTestCoordinateSpaceTests
{
    [Fact]
    public async Task PointInsideFrame_HitsTheElementActuallyUnderIt()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 480;

        // The frame is offset down the page, and the frame document stacks two
        // targets. The offset is chosen so that subtracting the frame origin from
        // the click lands squarely on the FIRST target instead of the second.
        const string html = """
<!doctype html>
<html>
<head><style>
  body { margin: 0 }
  iframe { display: block; width: 200px; height: 200px; margin-left: 0; margin-top: 100px; border: 0 }
</style></head>
<body>
  <iframe id="frame"></iframe>
  <script>
    var frame = document.getElementById('frame');
    var doc = frame.contentDocument;
    doc.open();
    doc.write('<!doctype html><html><head><style>body{margin:0}' +
              '#top{display:block;width:200px;height:50px}' +
              '#spacer{display:block;width:200px;height:50px}' +
              '#bottom{display:block;width:200px;height:50px}' +
              '</style></head><body>' +
              '<div id="top"></div><div id="spacer"></div><div id="bottom"></div>' +
              '</body></html>');
    doc.close();
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
            new Uri("https://fen.test/frame-hit"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth: viewportWidth,
            viewportHeight: viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var iframe = root.Descendants().OfType<Element>()
            .First(e => e.GetAttribute("id") == "frame");

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline &&
               !(iframe.FirstChild is Document d && d.GetElementById("bottom") != null))
        {
            await Task.Delay(25);
        }

        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);
        var bottom = frameDocument.GetElementById("bottom");
        Assert.NotNull(bottom);

        using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
        using var canvas = new SKCanvas(bitmap);

        void Render() => renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = host.ComputedStyles,
            Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
            BaseUrl = "https://fen.test/frame-hit",
            InvalidationReason = RenderFrameInvalidationReason.None,
            RequestedBy = nameof(IframeHitTestCoordinateSpaceTests),
            EmitVerificationReport = false
        });

        // The frame document arrives from a script, so an early pass can still be
        // moving the frame's boxes. Render until the target's rect stops changing
        // before reading a click point off it — waiting for layout to converge,
        // not for any particular answer.
        ElementGeometry bottomRect = default;
        var previous = default(ElementGeometry);
        var havePrevious = false;
        var settled = false;
        var settleDeadline = DateTime.UtcNow.AddSeconds(5);
        while (!settled && DateTime.UtcNow < settleDeadline)
        {
            Render();
            if (renderer.LastLayout.TryGetElementRect(bottom, out bottomRect) && bottomRect.Height > 0)
            {
                settled = havePrevious &&
                          bottomRect.X == previous.X &&
                          bottomRect.Y == previous.Y &&
                          bottomRect.Width == previous.Width &&
                          bottomRect.Height == previous.Height;
                previous = bottomRect;
                havePrevious = true;
            }

            if (!settled)
            {
                await Task.Delay(25);
            }
        }

        Assert.True(settled, "Layout for the frame's bottom target never converged.");

        var ctx = renderer.CreateRenderContext();
        var x = bottomRect.Left + (bottomRect.Width / 2f);
        var y = bottomRect.Top + (bottomRect.Height / 2f);

        var hit = HitTester.HitTest(ctx, x, y);

        Assert.NotNull(hit);
        Assert.Equal("bottom", hit.GetAttribute("id"));
    }
}
