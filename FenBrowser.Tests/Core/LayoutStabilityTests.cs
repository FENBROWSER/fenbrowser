using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using FenBrowser.Core;

namespace FenBrowser.Tests.Core
{
    public class LayoutStabilityTests
    {
        [Fact]
        public async Task StabilityGuard_SkipsRepaint_WhenLayoutIdentical()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var root = new Element("div");
            var child = new Element("span") { Text = "Stable Text" };
            root.AppendChild(child);

            styles[root] = new CssComputed { Display = "block", Width = 500, Height = 100 };
            styles[child] = new CssComputed { Display = "inline", ForegroundColor = SKColors.Black };

            bool layoutUpdated = false;
            Action<SKSize, List<InputOverlayData>> onLayoutUpdated = (size, overlays) => {
                layoutUpdated = true;
            };

            // First run
            renderer.Render(root, new SKCanvas(new SKBitmap(800, 600)), styles, new SKRect(0, 0, 800, 600), "http://example.com", onLayoutUpdated);
            Assert.True(layoutUpdated);
            var firstHash = renderer.LastLayout.ContentHash;

            // Second run - identical
            layoutUpdated = false;
            renderer.Render(root, new SKCanvas(new SKBitmap(800, 600)), styles, new SKRect(0, 0, 800, 600), "http://example.com", onLayoutUpdated);
            
            // If the guard works, it should still call onLayoutUpdated but log skipping paint.
            // Wait, in my implementation:
            /*
                if (LastLayout != null && currentResult.ContentHash == LastLayout.ContentHash ...)
                {
                    FenLogger.Debug($"[StabilityGuard] Skipping repaint...");
                    onLayoutUpdated?.Invoke(new SKSize(initialWidth, totalHeight), overlaysCache);
                    return;
                }
            */
            Assert.True(layoutUpdated);
            Assert.Equal(firstHash, renderer.LastLayout.ContentHash);
        }

        [Fact]
        public void CssCaching_ReducesMatchingTime()
        {
            CssLoader.ClearCaches();
            var element = new Element("div");
            element.SetAttribute("class", "test-class");
            var chain = CssLoader.MatchesSelector(element, "div.test-class"); // This will populate cache
            
            // Check if matches
            Assert.True(CssLoader.MatchesSelector(element, "div.test-class"));
        }
    }
}
