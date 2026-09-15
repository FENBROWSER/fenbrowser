using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// DOM §4.8: a closed shadow root is only hidden from script's shadowRoot getter. Its
    /// content is still part of the rendered tree, like an open root's. Layout, paint and the
    /// cascade read the open-only getter, so a closed root rendered as an empty host. That hid
    /// Cloudflare Turnstile, which puts its challenge iframe inside a closed shadow root.
    /// </summary>
    public sealed class ClosedShadowRootLayoutTests
    {
        [Theory]
        [InlineData(ShadowRootMode.Open)]
        [InlineData(ShadowRootMode.Closed)]
        public async Task ShadowContent_IsLaidOutForBothModes(ShadowRootMode mode)
        {
            const int width = 800;
            const int height = 600;
            var baseUri = new Uri("https://test.local/");
            var doc = new HtmlParser(
                "<!doctype html><html><head><style>body { margin: 0; }</style></head>" +
                "<body><div id='host'></div></body></html>",
                baseUri).Parse();

            var host = doc.GetElementById("host");
            var shadowRoot = host.AttachShadow(new ShadowRootInit { Mode = mode });
            var box = doc.CreateElement("div");
            box.SetAttribute("style", "width: 200px; height: 50px; background: red;");
            shadowRoot.AppendChild(box);

            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: width, viewportHeight: height);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            renderer.Render(root, canvas, styles, new SKRect(0, 0, width, height), baseUri.AbsoluteUri, (_, _) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(host, out var hostRect), "shadow host has no layout rect");
            Assert.True(renderer.LastLayout.TryGetElementRect(box, out var boxRect), $"{mode} shadow content has no layout rect");
            Assert.Equal(200f, boxRect.Width, 1);
            Assert.Equal(50f, hostRect.Height, 1);
        }
    }
}
