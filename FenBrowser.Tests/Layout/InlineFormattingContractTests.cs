using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class InlineFormattingContractTests
    {
        [Fact]
        public void InlineRuns_WrapToNextLine_WhenContainerWidthIsExceeded()
        {
            var root = new Element("div");
            var paragraph = new Element("p");
            var firstInline = new Element("span");
            var secondInline = new Element("span");

            firstInline.AppendChild(new Text("LLLLLLLLLLLLLLLL"));
            secondInline.AppendChild(new Text("LLLLLLLLLLLLLLLL"));
            paragraph.AppendChild(firstInline);
            paragraph.AppendChild(secondInline);
            root.AppendChild(paragraph);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 140, Height = 200 },
                [paragraph] = new CssComputed { Display = "block", Width = 140 },
                [firstInline] = new CssComputed { Display = "inline" },
                [secondInline] = new CssComputed { Display = "inline" }
            };

            var rootBox = LayoutRoot(root, styles, 140, 200);
            var firstBox = FindBox(rootBox, firstInline);
            var secondBox = FindBox(rootBox, secondInline);

            Assert.NotNull(firstBox);
            Assert.NotNull(secondBox);
            Assert.True(
                secondBox.Geometry.MarginBox.Top > firstBox.Geometry.MarginBox.Top + 0.1f,
                $"Expected second inline to wrap to the next line. first={firstBox.Geometry.MarginBox} second={secondBox.Geometry.MarginBox}");
            Assert.True(
                secondBox.Geometry.MarginBox.Left <= firstBox.Geometry.MarginBox.Left + 1f,
                $"Expected wrapped inline to restart near line start. first={firstBox.Geometry.MarginBox} second={secondBox.Geometry.MarginBox}");
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
