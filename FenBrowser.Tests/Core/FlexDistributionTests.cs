using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;
using System.Collections.Generic;

namespace FenBrowser.Tests.Core
{
    public class FlexDistributionTests
    {
        [Fact]
        public void FlexRow_GrowDistribution()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var root = new Element("div");
            var child1 = new Element("div");
            var child2 = new Element("div");
            root.AppendChild(child1);
            root.AppendChild(child2);

            // Container width 800. Children have base width 100 each.
            // Remaining space 600.
            // child1 grow=1, child2 grow=2. Total grow=3.
            // child1 share = 600 * 1/3 = 200. Final width = 100 + 200 = 300.
            // child2 share = 600 * 2/3 = 400. Final width = 100 + 400 = 500.

            styles[root] = new CssComputed { Display = "flex", FlexDirection = "row" };
            root.Attr["class"] = "main-columns";
            styles[child1] = new CssComputed { Display = "block", Width = 100, FlexGrow = 1 };
            styles[child2] = new CssComputed { Display = "block", Width = 100, FlexGrow = 2 };

            float viewportWidth = 800;
            renderer.Render(root, new SKCanvas(new SKBitmap((int)viewportWidth, 600)), styles, new SKRect(0, 0, viewportWidth, 600), "http://example.com", (size, overlays) => {});

            renderer.LastLayout.TryGetElementRect(child1, out var rect1);
            renderer.LastLayout.TryGetElementRect(child2, out var rect2);

            Assert.Equal(300, rect1.Width);
            Assert.Equal(500, rect2.Width);
        }

        [Fact]
        public void FlexColumn_GrowDistribution()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var root = new Element("div");
            var child1 = new Element("div");
            var child2 = new Element("div");
            root.AppendChild(child1);
            root.AppendChild(child2);

            // Container height 600. Children have base height 100 each.
            // Remaining space 400.
            // child1 grow=1, child2 grow=1. Total grow=2.
            // Each share = 200. Final height = 300.

            styles[root] = new CssComputed { Display = "flex", FlexDirection = "column", Height = 600 };
            styles[child1] = new CssComputed { Display = "block", Height = 100, FlexGrow = 1 };
            styles[child2] = new CssComputed { Display = "block", Height = 100, FlexGrow = 1 };

            renderer.Render(root, new SKCanvas(new SKBitmap(800, 600)), styles, new SKRect(0, 0, 800, 600), "http://example.com", (size, overlays) => {});

            renderer.LastLayout.TryGetElementRect(child1, out var rect1);
            renderer.LastLayout.TryGetElementRect(child2, out var rect2);

            Assert.Equal(300, rect1.Height);
            Assert.Equal(300, rect2.Height);
        }
    }
}
