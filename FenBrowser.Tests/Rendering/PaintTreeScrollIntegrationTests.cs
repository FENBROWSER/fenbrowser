using System.Collections.Generic;
using System;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Interaction;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class PaintTreeScrollIntegrationTests
    {
        [Fact]
        public void Build_ScrollableContainer_SetsScrollBoundsFromDescendants()
        {
            var root = new Element("div");
            var child = new Element("section");
            root.AppendChild(child);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { OverflowY = "auto" },
                [child] = new CssComputed()
            };

            var boxes = new Dictionary<Node, BoxModel>
            {
                [root] = BoxModel.FromContentBox(0, 0, 300, 300),
                [child] = BoxModel.FromContentBox(0, 500, 300, 100)
            };

            var manager = new ScrollManager();
            _ = NewPaintTreeBuilder.Build(root, boxes, styles, 800, 600, manager);

            var state = manager.GetScrollState(root);
            Assert.True(state.MaxScrollY > 0);
            Assert.Equal(300f, state.ViewportHeight, precision: 1);
            Assert.Equal(600f, state.ContentHeight, precision: 1);
        }

        [Fact]
        public void Build_ScrollSnapStyle_TriggersSnapFromRecentInput()
        {
            var root = new Element("div");
            var first = new Element("section");
            var second = new Element("section");
            root.AppendChild(first);
            root.AppendChild(second);

            var rootStyle = new CssComputed { OverflowY = "auto", ScrollSnapType = "y mandatory" };
            var childStyle = new CssComputed { ScrollSnapAlign = "start" };

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = rootStyle,
                [first] = childStyle,
                [second] = childStyle
            };

            var boxes = new Dictionary<Node, BoxModel>
            {
                [root] = BoxModel.FromContentBox(0, 0, 300, 300),
                [first] = BoxModel.FromContentBox(0, 0, 300, 120),
                [second] = BoxModel.FromContentBox(0, 500, 300, 120)
            };

            var manager = new ScrollManager();
            manager.Scroll(root, 0, 1); // establishes a positive direction hint

            _ = NewPaintTreeBuilder.Build(root, boxes, styles, 800, 600, manager);

            var state = manager.GetScrollState(root);
            Assert.True(state.SmoothScrollStartTime.HasValue);
            Assert.Equal(320f, state.SmoothScrollTarget.y, precision: 1);
        }

        [Fact]
        public void Build_ScrollSnapStyle_IgnoresStaleInputHints()
        {
            var root = new Element("div");
            var child = new Element("section");
            root.AppendChild(child);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { OverflowY = "auto", ScrollSnapType = "y mandatory" },
                [child] = new CssComputed { ScrollSnapAlign = "start" }
            };

            var boxes = new Dictionary<Node, BoxModel>
            {
                [root] = BoxModel.FromContentBox(0, 0, 300, 300),
                [child] = BoxModel.FromContentBox(0, 500, 300, 120)
            };

            var manager = new ScrollManager();
            manager.Scroll(root, 0, 1);
            var state = manager.GetScrollState(root);
            state.LastScrollUpdateUtc = DateTime.UtcNow.AddSeconds(-2);

            _ = NewPaintTreeBuilder.Build(root, boxes, styles, 800, 600, manager);

            Assert.False(state.SmoothScrollStartTime.HasValue);
        }
    }
}
