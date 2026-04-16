using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class CanvasBackgroundResolutionTests
    {
        [Fact]
        public void ResolveCanvasBackgroundColor_UsesBodyBackgroundForDocumentRoots()
        {
            var document = new Document();
            var html = new Element("html");
            var body = new Element("body");
            document.AppendChild(html);
            html.AppendChild(body);

            var styles = new Dictionary<Node, CssComputed>
            {
                [html] = new CssComputed
                {
                    Display = "block",
                    BackgroundColor = SKColors.Transparent
                },
                [body] = new CssComputed
                {
                    Display = "block",
                    Width = 320,
                    Height = 600,
                    Margin = new Thickness(240, 0, 0, 0),
                    BackgroundColor = new SKColor(0xEE, 0xEE, 0xEE)
                }
            };

            var background = SkiaDomRenderer.ResolveCanvasBackgroundColor(document, styles);

            Assert.Equal(new SKColor(0xEE, 0xEE, 0xEE), background);
        }

        [Fact]
        public void Render_DocumentRootClearsCanvasWithResolvedBodyBackground()
        {
            var renderer = new SkiaDomRenderer();
            var document = new Document();
            var html = new Element("html");
            var body = new Element("body");
            var card = new Element("div");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(card);

            var styles = new Dictionary<Node, CssComputed>
            {
                [html] = new CssComputed
                {
                    Display = "block",
                    BackgroundColor = SKColors.Transparent
                },
                [body] = new CssComputed
                {
                    Display = "block",
                    Width = 320,
                    Height = 600,
                    Margin = new Thickness(240, 0, 0, 0),
                    BackgroundColor = new SKColor(0xEE, 0xEE, 0xEE)
                },
                [card] = new CssComputed
                {
                    Display = "block",
                    Width = 120,
                    Height = 80,
                    BackgroundColor = SKColors.White
                }
            };

            using var bitmap = new SKBitmap(800, 600);
            using var canvas = new SKCanvas(bitmap);

            renderer.Render(document, canvas, styles, new SKRect(0, 0, 800, 600), "https://example.com");

            var cornerPixel = bitmap.GetPixel(8, 8);
            Assert.Equal(new SKColor(0xEE, 0xEE, 0xEE), cornerPixel);
        }
    }
}
