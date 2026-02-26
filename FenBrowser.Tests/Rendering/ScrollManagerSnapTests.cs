using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Interaction;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class ScrollManagerSnapTests
    {
        [Fact]
        public void PerformSnap_BothAxes_UsesAxisSpecificCandidates()
        {
            var manager = new ScrollManager();
            var container = new Element("div");
            var child = new Element("section");
            container.AppendChild(child);

            var containerStyle = new CssComputed { ScrollSnapType = "both mandatory" };
            var childStyle = new CssComputed { ScrollSnapAlign = "start" };
            var styleMap = new Dictionary<Element, CssComputed>
            {
                [child] = childStyle
            };

            var boxMap = new Dictionary<Element, SKRect>
            {
                [container] = new SKRect(0, 0, 200, 200),
                [child] = new SKRect(50, 300, 150, 400)
            };

            manager.SetScrollBounds(container, contentWidth: 1200, contentHeight: 1200, viewportWidth: 200, viewportHeight: 200);
            manager.SetScrollPosition(container, 280, 280);

            manager.PerformSnap(
                container,
                containerStyle,
                e => boxMap.TryGetValue(e, out var b) ? b : SKRect.Empty,
                e => styleMap.TryGetValue(e, out var s) ? s : null);

            var state = manager.GetScrollState(container);
            Assert.Equal(50f, state.SmoothScrollTarget.x, precision: 1);
            Assert.Equal(300f, state.SmoothScrollTarget.y, precision: 1);
        }

        [Fact]
        public void PerformSnap_AppliesScrollPaddingAndScrollMargin()
        {
            var manager = new ScrollManager();
            var container = new Element("div");
            var child = new Element("article");
            container.AppendChild(child);

            var containerStyle = new CssComputed { ScrollSnapType = "y mandatory" };
            containerStyle.Map["scroll-padding-top"] = "20px";

            var childStyle = new CssComputed { ScrollSnapAlign = "start" };
            childStyle.Map["scroll-margin-top"] = "10px";

            var styleMap = new Dictionary<Element, CssComputed>
            {
                [child] = childStyle
            };

            var boxMap = new Dictionary<Element, SKRect>
            {
                [container] = new SKRect(0, 0, 300, 300),
                [child] = new SKRect(100, 100, 200, 200)
            };

            manager.SetScrollBounds(container, contentWidth: 300, contentHeight: 1200, viewportWidth: 300, viewportHeight: 300);
            manager.SetScrollPosition(container, 0, 90);

            manager.PerformSnap(
                container,
                containerStyle,
                e => boxMap.TryGetValue(e, out var b) ? b : SKRect.Empty,
                e => styleMap.TryGetValue(e, out var s) ? s : null);

            var state = manager.GetScrollState(container);
            Assert.Equal(70f, state.SmoothScrollTarget.y, precision: 1);
        }

        [Fact]
        public void PerformSnap_MandatoryWithPositiveInputBias_PrefersForwardTarget()
        {
            var manager = new ScrollManager();
            var container = new Element("div");
            var first = new Element("section");
            var second = new Element("section");
            container.AppendChild(first);
            container.AppendChild(second);

            var containerStyle = new CssComputed { ScrollSnapType = "y mandatory" };
            var childStyle = new CssComputed { ScrollSnapAlign = "start" };
            var styleMap = new Dictionary<Element, CssComputed>
            {
                [first] = childStyle,
                [second] = childStyle
            };

            var boxMap = new Dictionary<Element, SKRect>
            {
                [container] = new SKRect(0, 0, 300, 300),
                [first] = new SKRect(0, 100, 300, 200),
                [second] = new SKRect(0, 400, 300, 500)
            };

            manager.SetScrollBounds(container, contentWidth: 300, contentHeight: 1800, viewportWidth: 300, viewportHeight: 300);
            manager.SetScrollPosition(container, 0, 250);
            manager.Scroll(container, 0, 1); // positive direction hint

            manager.PerformSnap(
                container,
                containerStyle,
                e => boxMap.TryGetValue(e, out var b) ? b : SKRect.Empty,
                e => styleMap.TryGetValue(e, out var s) ? s : null);

            var state = manager.GetScrollState(container);
            Assert.Equal(400f, state.SmoothScrollTarget.y, precision: 1);
        }
    }
}
