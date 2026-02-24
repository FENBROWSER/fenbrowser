using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class GridFormattingContextIntegrationTests
    {
        [Fact]
        public void GridFormattingContext_UsesTypedTemplateColumns_ForTrackPlacement()
        {
            var root = new Element("div");
            var a = new Element("div");
            var b = new Element("div");
            var c = new Element("div");
            root.AppendChild(a);
            root.AppendChild(b);
            root.AppendChild(c);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed
                {
                    Display = "grid",
                    Width = 300,
                    GridTemplateColumns = "100px 100px 100px"
                },
                [a] = new CssComputed { Display = "block", Width = 10, Height = 10 },
                [b] = new CssComputed { Display = "block", Width = 10, Height = 10 },
                [c] = new CssComputed { Display = "block", Width = 10, Height = 10 }
            };

            var rootBox = LayoutRoot(root, styles, 300, 200);
            var aBox = FindBox(rootBox, a);
            var bBox = FindBox(rootBox, b);
            var cBox = FindBox(rootBox, c);

            Assert.NotNull(aBox);
            Assert.NotNull(bBox);
            Assert.NotNull(cBox);

            Assert.Equal(0f, aBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(100f, bBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(200f, cBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(0f, aBox.Geometry.MarginBox.Top, 1);
            Assert.Equal(0f, bBox.Geometry.MarginBox.Top, 1);
            Assert.Equal(0f, cBox.Geometry.MarginBox.Top, 1);
        }

        [Fact]
        public void GridFormattingContext_RespectsExplicitGridLinePlacement()
        {
            var root = new Element("div");
            var explicitItem = new Element("div");
            var autoItem = new Element("div");
            root.AppendChild(explicitItem);
            root.AppendChild(autoItem);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed
                {
                    Display = "grid",
                    Width = 300,
                    GridTemplateColumns = "100px 100px 100px",
                    GridTemplateRows = "40px"
                },
                [explicitItem] = new CssComputed
                {
                    Display = "block",
                    GridColumnStart = "3",
                    GridColumnEnd = "4",
                    GridRowStart = "1",
                    GridRowEnd = "2"
                },
                [autoItem] = new CssComputed
                {
                    Display = "block",
                    Width = 10,
                    Height = 10
                }
            };

            var rootBox = LayoutRoot(root, styles, 300, 200);
            var explicitBox = FindBox(rootBox, explicitItem);
            var autoBox = FindBox(rootBox, autoItem);

            Assert.NotNull(explicitBox);
            Assert.NotNull(autoBox);

            Assert.Equal(200f, explicitBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(0f, explicitBox.Geometry.MarginBox.Top, 1);
            Assert.Equal(0f, autoBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(0f, autoBox.Geometry.MarginBox.Top, 1);
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

            var context = FormattingContext.Resolve(rootBox);
            context.Layout(rootBox, state);
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
