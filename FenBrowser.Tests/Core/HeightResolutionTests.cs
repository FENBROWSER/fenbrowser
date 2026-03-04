using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using FenBrowser.Core;
using System.Linq;

namespace FenBrowser.Tests.Core
{
    public class HeightResolutionTests
    {
        [Fact]
        public void Body_Height_AtLeastViewport()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var html = new Element("html");
            var body = new Element("body");
            html.AppendChild(body);

            // Empty body
            styles[html] = new CssComputed { Display = "block" };
            styles[body] = new CssComputed { Display = "block" };

            float viewportHeight = 600;
            renderer.Render(html, new SKCanvas(new SKBitmap(800, (int)viewportHeight)), styles, new SKRect(0, 0, 800, viewportHeight), "http://example.com", (size, overlays) => {});

            renderer.LastLayout.TryGetElementRect(body, out var bodyRect);
            Assert.Equal(viewportHeight, bodyRect.Height);
            
            renderer.LastLayout.TryGetElementRect(html, out var htmlRect);
            // HTML should be auto (intrinsic), wrapping BODY. 
            // Default margin/padding on BODY might push it slightly beyond 600.
            // Logs show 616.
            Assert.True(htmlRect.Height >= viewportHeight);
        }

        [Fact]
        public void Body_Height_ExpandsWithContent()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var html = new Element("html");
            var body = new Element("body");
            var tallChild = new Element("div");
            html.AppendChild(body);
            body.AppendChild(tallChild);

            styles[html] = new CssComputed { Display = "block" };
            styles[body] = new CssComputed { Display = "block" };
            styles[tallChild] = new CssComputed { Display = "block", Height = 1000 };

            float viewportHeight = 600;
            renderer.Render(html, new SKCanvas(new SKBitmap(800, (int)viewportHeight)), styles, new SKRect(0, 0, 800, viewportHeight), "http://example.com", (size, overlays) => {});

            renderer.LastLayout.TryGetElementRect(body, out var bodyRect);
            Assert.Equal(1000, bodyRect.Height);
        }

        [Fact]
        public void FlexColumn_Height_IsIntrinsic()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var root = new Element("div");
            var child1 = new Element("div");
            var child2 = new Element("div");
            root.AppendChild(child1);
            root.AppendChild(child2);

            styles[root] = new CssComputed { Display = "flex", FlexDirection = "column" };
            styles[child1] = new CssComputed { Display = "block", Height = 100 };
            styles[child2] = new CssComputed { Display = "block", Height = 150 };

            float viewportHeight = 600;
            renderer.Render(root, new SKCanvas(new SKBitmap(800, (int)viewportHeight)), styles, new SKRect(0, 0, 800, viewportHeight), "http://example.com", (size, overlays) => {});

            renderer.LastLayout.TryGetElementRect(root, out var rootRect);
            // Height should be 100 + 150 = 250, NOT clamped to viewport (600) or inherited.
            Assert.Equal(250, rootRect.Height);
        }

        [Fact]
        public void ScrollHeight_CanExceed10xViewport()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var html = new Element("html");
            var body = new Element("body");
            var giant = new Element("div");
            html.AppendChild(body);
            body.AppendChild(giant);

            styles[html] = new CssComputed { Display = "block" };
            styles[body] = new CssComputed { Display = "block" };
            // 20x viewport height
            styles[giant] = new CssComputed { Display = "block", Height = 12000 };

            float viewportHeight = 600;
            float totalHeightResult = 0;
            renderer.Render(html, new SKCanvas(new SKBitmap(800, (int)viewportHeight)), styles, new SKRect(0, 0, 800, viewportHeight), "http://example.com", (size, overlays) => {
                totalHeightResult = size.Height;
            });

            // Should be at least 12000, not clamped to 6000 (10x 600)
            Assert.True(totalHeightResult >= 12000);
        }
    }
}
