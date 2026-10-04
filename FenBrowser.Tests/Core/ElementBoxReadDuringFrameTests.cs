using System.Diagnostics;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// Script geometry reads (getBoundingClientRect, offsetWidth, IntersectionObserver)
/// resolve boxes through SkiaDomRenderer.GetElementBox. It used to take _stateLock,
/// which Render holds through layout, paint and raster, so every read waited for the
/// frame in progress: 200 reads took 1.4-2.3 s on YouTube's watch page. Reads now come
/// from the box table published when layout finished.
/// </summary>
public sealed class ElementBoxReadDuringFrameTests
{
    [Fact]
    public async Task GetElementBox_DoesNotWaitForAFrameHoldingTheRenderLock()
    {
        const string html = "<!doctype html><html><head><style>html,body{margin:0}#box{width:120px;height:40px}</style></head>" +
            "<body><div id='box'></div></body></html>";
        var baseUri = new Uri("https://element-box.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var box = doc.Descendants().OfType<Element>().First(e => e.Id == "box");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 400, viewportHeight: 300);
        var renderer = new SkiaDomRenderer();
        using (var bitmap = new SKBitmap(400, 300))
        using (var canvas = new SKCanvas(bitmap))
        {
            renderer.RenderFrame(new RenderFrameRequest
            {
                Root = root,
                Canvas = canvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 400, 300),
                SeparateLayoutViewport = new SKSize(400, 300),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom,
                RequestedBy = "element-box-test",
                EmitVerificationReport = false
            });
        }

        // Stand in for a frame in progress: another thread holds the render lock.
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (renderer._stateLock)
            {
                holding.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var watch = Stopwatch.StartNew();
            var read = Task.Run(() => renderer.GetElementBox(box));
            Assert.True(read.Wait(TimeSpan.FromSeconds(2)), "GetElementBox waited for the render lock");
            Assert.NotNull(read.Result);
            Assert.Equal(120f, read.Result!.BorderBox.Width, 1);
            Assert.True(watch.ElapsedMilliseconds < 2000);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }
}
