using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
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
    public void RenderFrame_ForcesLayoutForLateIframeMissingFromPreviousSnapshot()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 360;
        var root = new Element("div");
        var styles = new System.Collections.Generic.Dictionary<Node, CssComputed>
        {
            [root] = new CssComputed { Display = "block", Width = 400, Height = 200 }
        };
        var renderer = new SkiaDomRenderer();

        RenderFrame(renderer, root, styles, viewportWidth, viewportHeight);

        var iframe = new Element("iframe");
        iframe.SetAttribute("width", "304");
        iframe.SetAttribute("height", "78");
        root.AppendChild(iframe);
        styles[iframe] = new CssComputed { Display = "inline-block", Width = 304, Height = 78 };
        root.ClearDirty(InvalidationKind.Style | InvalidationKind.Layout);
        iframe.ClearDirty(InvalidationKind.Style | InvalidationKind.Layout);

        using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
        using var canvas = new SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
            BaseUrl = "https://fen.test/late-iframe",
            InvalidationReason = RenderFrameInvalidationReason.None,
            RequestedBy = "IframeInputRetargetingTests",
            EmitVerificationReport = false
        });

        Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var rect));
        Assert.Equal(304f, rect.Width, 1f);
        Assert.Equal(78f, rect.Height, 1f);
        Assert.True(renderer.CreateRenderContext().Boxes.ContainsKey(iframe));
    }

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
    public async Task DynamicallyInsertedInlineIframe_ContributesToFollowingBlockFlow()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 360;
        const string html = """
<!doctype html>
<html><body style="margin:0;padding:20px"><div style="max-width:400px"><form>
<div id="frame-slot"></div></form><div id="following">About this page</div></div>
<script>setTimeout(function(){
  var slot=document.getElementById('frame-slot');
  var outer=document.createElement('div'); outer.style.width='304px'; outer.style.height='78px';
  var inner=document.createElement('div');
  var frame=document.createElement('iframe'); frame.id='frame'; frame.width='304'; frame.height='78';
  inner.appendChild(frame); outer.appendChild(inner); slot.appendChild(outer);
  var doc=frame.contentDocument; doc.open();
  doc.write('<!doctype html><html><body style="margin:0"><button id="target" style="width:40px;height:40px">check</button></body></html>');
  doc.close();
},0);</script></body></html>
""";

        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);

        await host.Engine.RenderAsync(
            html,
            new Uri("https://fen.test/dynamic-inline-iframe"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth,
            viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        await WaitForAsync(
            () => root.OwnerDocument?.GetElementById("frame") is Element frame &&
                  frame.FirstChild is Document document &&
                  document.GetElementById("target") != null,
            "dynamic iframe target to be written");
        await host.FlushPendingLayoutAsync();
        var iframe = Assert.IsType<Element>(root.OwnerDocument?.GetElementById("frame"));
        var following = Assert.IsType<Element>(root.OwnerDocument?.GetElementById("following"));
        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);
        var target = Assert.IsType<Element>(frameDocument.GetElementById("target"));

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var iframeRect));
        Assert.True(renderer.LastLayout.TryGetElementRect(following, out var followingRect));
        Assert.True(renderer.LastLayout.TryGetElementRect(target, out var targetRect));
        Assert.True(followingRect.Top >= iframeRect.Bottom - 0.5f,
            $"Following block started at {followingRect.Top:F1} before iframe bottom {iframeRect.Bottom:F1}.");

        var x = targetRect.Left + targetRect.Width / 2f;
        var y = targetRect.Top + targetRect.Height / 2f;
        Assert.True(HitTester.HitTestInput(renderer.CreateRenderContext(), x, y, out var input));
        Assert.Same(target, input.Target);
    }

    [Fact]
    public async Task HitTestInput_RetargetsAncestorPaintHitIntoDescendantIframe()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 360;
        const string html = """
<!doctype html>
<html><head><style>html,body{margin:0}#wrapper,iframe{display:block;width:304px;height:78px}iframe{border:0}</style></head>
<body><div id="wrapper"><iframe id="frame"></iframe></div><script>
var frame=document.getElementById('frame');
var doc=frame.contentDocument;
doc.open();
doc.write('<!doctype html><html><head><style>html,body{margin:0}#target{display:block;width:40px;height:40px}</style></head><body><button id="target">check</button></body></html>');
doc.close();
</script></body></html>
""";

        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);

        await host.Engine.RenderAsync(
            html,
            new Uri("https://fen.test/iframe-ancestor-hit"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth,
            viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var wrapper = FindById(root, "wrapper");
        var iframe = FindById(root, "frame");
        await WaitForAsync(
            () => iframe.FirstChild is Document document && document.GetElementById("target") != null,
            "iframe target to be written");
        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);
        var target = Assert.IsType<Element>(frameDocument.GetElementById("target"));

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var iframeRect));
        Assert.True(renderer.LastLayout.TryGetElementRect(target, out var targetRect));

        var context = renderer.CreateRenderContext();
        context.Boxes.Remove(iframe);
        context.PaintTreeRoots = new[]
        {
            new BackgroundPaintNode
            {
                SourceNode = iframe,
                Bounds = new SKRect(iframeRect.Left, iframeRect.Top, iframeRect.Right, iframeRect.Bottom),
                Color = SKColors.White
            },
            new BackgroundPaintNode
            {
                SourceNode = wrapper,
                Bounds = new SKRect(iframeRect.Left, iframeRect.Top, iframeRect.Right, iframeRect.Bottom),
                Color = SKColors.Transparent
            }
        };

        var x = targetRect.Left + targetRect.Width / 2f;
        var y = targetRect.Top + targetRect.Height / 2f;
        Assert.True(HitTester.HitTestInput(context, x, y, out var input));
        Assert.Same(target, input.Target);
        Assert.True(input.RetargetedIntoFrame);
        Assert.Equal(x - iframeRect.Left, input.ClientX, 1f);
        Assert.Equal(y - iframeRect.Top, input.ClientY, 1f);
    }

    [Fact]
    public async Task BrowserHostClick_CompletesIframeAsyncMutationAndNextFramePipeline()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 360;
        const string html = """
<!doctype html>
<html><head><style>body{margin:0}iframe{display:block;width:300px;height:120px;margin:50px 0 0 40px;border:0}</style></head>
<body><iframe id="challenge-frame"></iframe><script>
var frame = document.getElementById('challenge-frame');
var doc = frame.contentDocument;
doc.open();
doc.write('<!doctype html><html><head><style>body{margin:0}#anchor{display:block;width:120px;height:40px;margin:10px 0 0 20px}</style></head><body data-events=""><button id="anchor">check</button></body></html>');
doc.close();
doc.body.setAttribute('data-events', '');
var anchor = doc.getElementById('anchor');
['pointerdown','mousedown','pointerup','mouseup','click'].forEach(function(type) {
  anchor.addEventListener(type, function() {
    doc.body.setAttribute('data-events', doc.body.getAttribute('data-events') + type + ',');
  });
});
anchor.addEventListener('click', function() {
  Promise.resolve().then(function() {
    doc.body.setAttribute('data-microtask', 'done');
    doc.body.style.backgroundColor = 'rgb(10, 20, 30)';
  });
  var channel = new MessageChannel();
  channel.port1.onmessage = function() { doc.body.setAttribute('data-message', 'done'); };
  channel.port2.postMessage('go');
  setTimeout(function() { doc.body.setAttribute('data-timer', 'done'); }, 0);
});
</script></body></html>
""";

        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);

        await host.Engine.RenderAsync(
            html,
            new Uri("https://fen.test/iframe-click-pipeline"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth,
            viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var iframe = FindById(root, "challenge-frame");
        await WaitForAsync(
            () => iframe.FirstChild is Document document && document.GetElementById("anchor") != null,
            "iframe click pipeline content");
        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);
        var anchor = Assert.IsType<Element>(frameDocument.GetElementById("anchor"));
        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(renderer.LastLayout.TryGetElementRect(anchor, out var anchorRect));
        var repaintRequests = 0;
        host.RepaintReady += (_, _) => Interlocked.Increment(ref repaintRequests);

        var x = anchorRect.Left + anchorRect.Width / 2f;
        var y = anchorRect.Top + anchorRect.Height / 2f;
        host.OnMouseDown(x, y, 0);
        host.OnMouseUp(x, y, 0);
        await host.DispatchClickAndActivate(x, y, 0);

        await WaitForAsync(
            () => frameDocument.Body?.GetAttribute("data-message") == "done" &&
                  frameDocument.Body?.GetAttribute("data-timer") == "done",
            "iframe async click work");
        Assert.Equal("pointerdown,mousedown,pointerup,mouseup,click,", frameDocument.Body?.GetAttribute("data-events"));
        Assert.Equal("done", frameDocument.Body?.GetAttribute("data-microtask"));
        Assert.True(Volatile.Read(ref repaintRequests) > 0);

        var beforeFrame = RenderPipeline.FrameSequence;
        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(RenderPipeline.FrameSequence > beforeFrame);
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
    public async Task BrowserHostWheel_ScrollsOwnedIframeBeforeTopLevel()
    {
        const int viewportWidth = 320;
        const int viewportHeight = 220;
        const string html = """
<!doctype html>
<html><head><style>html,body{margin:0}iframe{display:block;width:180px;height:80px;margin:20px;border:0}</style></head>
<body><iframe id="frame"></iframe><script>
var frame=document.getElementById('frame');
var doc=frame.contentDocument;
doc.open();
doc.write('<!doctype html><html><body style="margin:0;height:300px"><div id="surface" style="height:300px">surface</div></body></html>');
doc.close();
</script></body></html>
""";

        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);

        await host.Engine.RenderAsync(
            html,
            new Uri("https://fen.test/iframe-wheel"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth,
            viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var iframe = FindById(root, "frame");
        await WaitForAsync(
            () => iframe.FirstChild is Document document && document.GetElementById("surface") != null,
            "iframe wheel surface to be written");
        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);
        var surface = Assert.IsType<Element>(frameDocument.GetElementById("surface"));

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var iframeRect));
        Assert.True(renderer.LastLayout.TryGetElementRect(surface, out var surfaceRect));
        renderer.ScrollManager.SetScrollBounds(
            iframe,
            iframeRect.Width,
            300f,
            iframeRect.Width,
            iframeRect.Height);

        var defaultAllowed = host.OnMouseWheel(
            surfaceRect.Left + 20f,
            surfaceRect.Top + 20f,
            deltaX: 0,
            deltaY: -1);

        Assert.False(defaultAllowed);
        Assert.Equal(60f, renderer.ScrollManager.GetScrollOffset(iframe).y, 1f);
        Assert.Equal(
            "60",
            host.Engine.Evaluate(
                "String(document.getElementById('frame').contentWindow.scrollY)")?.ToString());
    }

    [Fact]
    public async Task BrowserHostMouseMove_DispatchesBoundaryEventsInsideIframe()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 360;
        const string html = """
<!doctype html>
<html><head><style>html,body{margin:0}iframe{display:block;width:304px;height:78px;margin:20px;border:0}</style></head>
<body><iframe id="challenge-frame"></iframe><script>
var frame = document.getElementById('challenge-frame');
var doc = frame.contentDocument;
doc.open();
doc.write('<!doctype html><html><head><style>body{margin:0}#target{display:block;width:40px;height:40px;margin:10px}</style></head><body><div id="target"></div></body></html>');
doc.close();
var target = doc.getElementById('target');
target.addEventListener('pointerover', function () { target.setAttribute('data-pointerover', '1'); });
target.addEventListener('mouseover', function () { target.classList.add('hovered'); });
</script></body></html>
""";

        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);

        await host.Engine.RenderAsync(
            html,
            new Uri("https://fen.test/iframe-hover"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth,
            viewportHeight,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var iframe = FindById(root, "challenge-frame");
        await WaitForAsync(
            () => iframe.FirstChild is Document document && document.GetElementById("target") != null,
            "iframe hover target to be written");
        var frameDocument = Assert.IsType<Document>(iframe.FirstChild);
        var target = Assert.IsType<Element>(frameDocument.GetElementById("target"));

        RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
        Assert.True(renderer.LastLayout.TryGetElementRect(target, out var targetRect), "Missing iframe hover target layout rect.");

        var x = targetRect.Left + targetRect.Width / 2f;
        var y = targetRect.Top + targetRect.Height / 2f;
        var context = renderer.CreateRenderContext();
        Assert.True(
            HitTester.HitTestInput(context, x, y, out var input),
            HitTester.DescribeFrameCandidates(context, iframe, x, y));
        Assert.Same(target, input.Target);

        host.OnMouseMove(x, y);

        await WaitForAsync(
            () => target.ClassList.Contains("hovered") && target.GetAttribute("data-pointerover") == "1",
            "iframe target did not receive pointerover and mouseover boundary events");
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
