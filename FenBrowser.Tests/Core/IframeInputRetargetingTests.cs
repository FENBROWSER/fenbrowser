using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Interaction;
using SkiaSharp;

namespace FenBrowser.Tests.Core;

public sealed class IframeInputRetargetingTests
{
    [Fact]
    public async Task AttachedIframeDocument_ClipsPaintToFrameContentBox()
    {
        const int viewportWidth = 320;
        const int viewportHeight = 200;
        const string html = """
<!doctype html>
<html>
<head><style>html,body{margin:0;background:#fff} iframe{display:block;margin:20px;border:0}</style></head>
<body>
  <iframe id="frame" width="100" height="60"></iframe>
  <script>
    var doc = document.getElementById('frame').contentDocument;
    doc.open();
    doc.write('<!doctype html><html><body style="margin:0"><div style="width:100px;height:60px;background:#000"></div></body></html>');
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
            new Uri("https://fen.test/iframe-clip"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth: viewportWidth,
            viewportHeight: viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var iframe = FindById(root, "frame");
        await WaitForAsync(
            () => iframe.FirstChild is Document document && document.Body != null,
            "iframe document body to be written");

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var iframeRect));

        var context = renderer.CreateRenderContext();
        var paintTree = NewPaintTreeBuilder.Build(
            root,
            context.Boxes,
            host.ComputedStyles,
            viewportWidth,
            viewportHeight,
            new ScrollManager());
        var frameClip = FlattenPaintNodes(paintTree.Roots)
            .OfType<ClipPaintNode>()
            .FirstOrDefault(clip =>
                clip.ClipRect is SKRect rect &&
                Math.Abs(rect.Left - iframeRect.Left) < 1f &&
                Math.Abs(rect.Top - iframeRect.Top) < 1f &&
                Math.Abs(rect.Width - iframeRect.Width) < 1f &&
                Math.Abs(rect.Height - iframeRect.Height) < 1f);

        Assert.NotNull(frameClip);
    }

    [Fact]
    public async Task AttachedIframeDocument_UsesFrameContentBoxAsLayoutViewport()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 360;
        const string html = """
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    iframe { display: block; margin: 40px 0 0 50px; border: 0; }
  </style>
</head>
<body>
  <iframe id="challenge-frame" width="300" height="120"></iframe>
  <script>
    var frame = document.getElementById('challenge-frame');
    var doc = frame.contentDocument;
    doc.open();
    doc.write('<!doctype html><html><head><style>html,body{margin:0;height:100vh}</style></head><body><div>frame</div></body></html>');
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
            new Uri("https://fen.test/iframe-viewport"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth: viewportWidth,
            viewportHeight: viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var iframe = FindById(root, "challenge-frame");
        await WaitForAsync(
            () => iframe.FirstChild is Document document && document.Body != null,
            "iframe document body to be written");
        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);

        Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var iframeRect));
        Assert.True(renderer.LastLayout.TryGetElementRect(frameDocument.DocumentElement, out var frameRootRect));
        Assert.Equal(300f, iframeRect.Width, 1f);
        Assert.Equal(120f, iframeRect.Height, 1f);
        Assert.Equal(iframeRect.Left, frameRootRect.Left, 1f);
        Assert.Equal(iframeRect.Top, frameRootRect.Top, 1f);
        Assert.InRange(frameRootRect.Width, 299f, 301f);
        Assert.InRange(frameRootRect.Height, 119f, 121f);
    }

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
        await WaitForAsync(
            () => iframe.FirstChild is Document document && document.GetElementById("anchor") != null,
            "iframe document content to be written");
        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);
        var button = frameDocument.GetElementById("anchor");
        Assert.NotNull(button);

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);

        Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var iframeRect), "Missing iframe layout rect.");
        Assert.True(renderer.LastLayout.TryGetElementRect(button, out var buttonRect), "Missing iframe button layout rect.");

        var visualX = buttonRect.Left + (buttonRect.Width / 2f);
        var visualY = buttonRect.Top + (buttonRect.Height / 2f);

        host.OnClick(visualX, visualY, button: 0);

        Assert.Equal("yes", frameDocument.Body?.GetAttribute("data-clicked"));
        Assert.Same(button, frameDocument.ActiveElement);
        Assert.Same(iframe, root.OwnerDocument?.ActiveElement);
    }

    [Fact]
    public async Task ScrolledIframe_HitTestMatchesPaintedChildPosition()
    {
        const int viewportWidth = 320;
        const int viewportHeight = 220;
        const string html = """
<!doctype html>
<html>
<head><style>html,body{margin:0}iframe{display:block;width:180px;height:60px;margin:20px;border:0}</style></head>
<body>
  <iframe id="frame"></iframe>
  <script>
    var frame = document.getElementById('frame');
    var doc = frame.contentDocument;
    doc.open();
    doc.write('<!doctype html><html><body style="margin:0;height:260px"><div style="height:140px"></div><button id="target" style="display:block;width:100px;height:30px">target</button></body></html>');
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
            new Uri("https://fen.test/iframe-scroll"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth,
            viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var iframe = FindById(root, "frame");
        await WaitForAsync(
            () => iframe.FirstChild is Document document && document.GetElementById("target") != null,
            "iframe scroll target to be written");
        await host.FlushPendingLayoutAsync();
        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);
        var target = Assert.IsType<Element>(frameDocument.GetElementById("target"));

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var iframeRect));
        Assert.True(renderer.LastLayout.TryGetElementRect(target, out var targetRect));

        renderer.ScrollManager.SetScrollBounds(iframe, iframeRect.Width, 260f, iframeRect.Width, iframeRect.Height);
        renderer.ScrollManager.SetScrollPosition(iframe, 0, 120f);
        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);

        var visualX = targetRect.Left + (targetRect.Width / 2f);
        var visualY = targetRect.Top + (targetRect.Height / 2f) - 120f;
        Assert.InRange(visualY, iframeRect.Top, iframeRect.Bottom);
        Assert.True(HitTester.HitTestInput(renderer.CreateRenderContext(), visualX, visualY, out var input));
        Assert.Same(target, input.Target);
        Assert.True(input.RetargetedIntoFrame);
    }

    [Fact]
    public async Task BrowserHostClick_UsesFenJsDefaultPreventionForFallbackActivation()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 360;
        var baseUri = new Uri("https://fen.test/prevent-default");
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
      border: 0;
      margin-left: 40px;
      margin-top: 50px;
    }
  </style>
</head>
<body>
  <iframe id="challenge-frame"></iframe>
  <script>
    var frame = document.getElementById('challenge-frame');
    var doc = frame.contentDocument;
    doc.open();
    doc.write('<!doctype html><html><head><style>body{margin:0}#target{display:block;width:160px;height:40px;margin-left:20px;margin-top:10px}</style></head><body data-clicks="0"><a id="target" href="#blocked">Target</a></body></html>');
    doc.close();
    doc.body.addEventListener('click', function (event) {
      doc.body.setAttribute('data-clicks', '1');
      event.preventDefault();
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
        SetCurrentUri(host, baseUri);

        await host.Engine.RenderAsync(
            html,
            baseUri,
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth: viewportWidth,
            viewportHeight: viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var iframe = FindById(root, "challenge-frame");
        await WaitForAsync(
            () => iframe.FirstChild is Document document && document.GetElementById("target") != null,
            "iframe document target to be written");
        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);
        var anchor = frameDocument.GetElementById("target");
        Assert.NotNull(anchor);

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var iframeRect), "Missing iframe layout rect.");
        Assert.True(renderer.LastLayout.TryGetElementRect(anchor, out var anchorRect), "Missing anchor layout rect.");

        var visualX = anchorRect.Left + (anchorRect.Width / 2f);
        var visualY = anchorRect.Top + (anchorRect.Height / 2f);

        Assert.True(HitTester.HitTestInput(renderer.CreateRenderContext(), visualX, visualY, out var input));
        Assert.Same(anchor, input.Target);
        Assert.Same(anchor, host.HitTestElementAtViewportPoint(visualX, visualY));

        host.OnClick(visualX, visualY, button: 0);
        await host.HandleElementClick(anchor);

        Assert.Equal("1", frameDocument.Body?.GetAttribute("data-clicks"));
        Assert.Equal(baseUri, host.CurrentUri);
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

    private static System.Collections.Generic.List<PaintNodeBase> FlattenPaintNodes(
        System.Collections.Generic.IEnumerable<PaintNodeBase> nodes)
    {
        var flattened = new System.Collections.Generic.List<PaintNodeBase>();
        foreach (var node in nodes ?? Enumerable.Empty<PaintNodeBase>())
        {
            flattened.Add(node);
            flattened.AddRange(FlattenPaintNodes(node.Children));
        }

        return flattened;
    }

    private static Element FindById(Element root, string id)
    {
        return root.Descendants()
            .OfType<Element>()
            .First(e => string.Equals(e.GetAttribute("id"), id, StringComparison.Ordinal));
    }

    private static async Task WaitForAsync(Func<bool> predicate, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate(), $"Timed out waiting for {description}.");
    }

    private static void SetCurrentUri(BrowserHost host, Uri uri)
    {
        var currentField = typeof(BrowserHost).GetField(
            "_current",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentField);
        currentField!.SetValue(host, uri);
    }
}
