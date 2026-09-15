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
    /// An iframe's document is styled against its own stylesheets after the parent cascade,
    /// then laid out inside the frame's box. Both passes found iframes by walking the light
    /// tree, so a frame inside a shadow root got a host box while its document got no styles
    /// and no layout, and painted nothing. Cloudflare Turnstile's challenge frame sits in a
    /// closed shadow root.
    /// </summary>
    public sealed class ShadowRootIframeDocumentStyleTests
    {
        [Theory]
        [InlineData(ShadowRootMode.Open)]
        [InlineData(ShadowRootMode.Closed)]
        public async Task IframeDocumentInsideAShadowRoot_IsStyled(ShadowRootMode mode)
        {
            var (doc, frameDocument) = CreatePage(mode);
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");

            var styles = await CssLoader.ComputeAsync(root, BaseUri, null, viewportWidth: 800, viewportHeight: 600);

            Assert.True(styles.TryGetValue(frameDocument.Body, out var bodyStyle), $"iframe document in a {mode} shadow root was not styled");
            Assert.Equal(0d, bodyStyle.Margin.Top);
        }

        [Theory]
        [InlineData(ShadowRootMode.Open)]
        [InlineData(ShadowRootMode.Closed)]
        public async Task IframeDocumentInsideAShadowRoot_IsLaidOutInsideTheFrame(ShadowRootMode mode)
        {
            const int width = 800;
            const int height = 600;
            var (doc, frameDocument) = CreatePage(mode);
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, BaseUri, null, viewportWidth: width, viewportHeight: height);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            renderer.Render(root, canvas, styles, new SKRect(0, 0, width, height), BaseUri.AbsoluteUri, (_, _) => { });

            Assert.True(
                renderer.LastLayout.TryGetElementRect(frameDocument.Body, out var bodyRect),
                $"iframe document in a {mode} shadow root was not laid out");
            Assert.Equal(300f, bodyRect.Width, 1);
        }

        private static readonly Uri BaseUri = new("https://parent.test/page");

        private static (Document Doc, Document FrameDocument) CreatePage(ShadowRootMode mode)
        {
            var doc = new HtmlParser(
                "<html><head><style>body { margin: 0; }</style></head><body><div id='host'></div></body></html>",
                BaseUri).Parse();
            var host = doc.GetElementById("host");
            var shadowRoot = host.AttachShadow(new ShadowRootInit { Mode = mode });
            var frame = doc.CreateElement("iframe");
            frame.SetAttribute("style", "display: block; width: 300px; height: 65px; border: none");
            shadowRoot.AppendChild(frame);

            var frameDocument = new HtmlParser(
                "<html><head><style>body { margin: 0; background: rgb(0, 0, 255); }</style></head><body>inner</body></html>",
                new Uri("https://frame.test/inner")).Parse();
            frame.AppendChild(frameDocument);
            return (doc, frameDocument);
        }
    }
}
