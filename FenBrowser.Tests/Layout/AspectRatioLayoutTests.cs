using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class AspectRatioLayoutTests
    {
        [Fact]
        public void FlexAutoHeight_WithDefiniteWidthAndAspectRatio_ReservesRatioHeight()
        {
            var root = new Element("div");
            var carousel = new Element("div");
            var visual = new Element("div");

            carousel.AppendChild(visual);
            root.AppendChild(carousel);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 1246, Height = 900 },
                [carousel] = new CssComputed { Display = "block", Width = 1204 },
                [visual] = new CssComputed
                {
                    Display = "flex",
                    WidthPercent = 100,
                    AspectRatio = 1206d / 684d,
                    LineHeight = 21
                }
            };

            var rootBox = LayoutRoot(root, styles, 1246, 900);
            var carouselBox = FindBox(rootBox, carousel);
            var visualBox = FindBox(rootBox, visual);

            Assert.NotNull(carouselBox);
            Assert.NotNull(visualBox);

            float expectedHeight = 1204f / (1206f / 684f);
            Assert.InRange(visualBox.Geometry.ContentBox.Height, expectedHeight - 1f, expectedHeight + 1f);
            Assert.InRange(carouselBox.Geometry.ContentBox.Height, expectedHeight - 1f, expectedHeight + 1f);
        }

        private static LayoutBox LayoutRoot(Element root, Dictionary<Node, CssComputed> styles, float width, float height)
        {
            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            Assert.NotNull(rootBox);

            var state = new LayoutState(
                new SKSize(width, height),
                width,
                height,
                width,
                height);

            FormattingContext.Resolve(rootBox).Layout(rootBox, state);
            return rootBox;
        }

        private static LayoutBox FindBox(LayoutBox box, Node target)
        {
            if (box == null)
            {
                return null;
            }

            if (ReferenceEquals(box.SourceNode, target))
            {
                return box;
            }

            foreach (var child in box.Children)
            {
                var found = FindBox(child, target);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
