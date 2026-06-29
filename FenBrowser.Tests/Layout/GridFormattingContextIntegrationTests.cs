using System.Collections.Generic;
using FenBrowser.Core;
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

        [Fact]
        public void GridFormattingContext_PercentageHeightItemInAutoTrack_DoesNotUseViewportAsIntrinsicHeight()
        {
            var root = new Element("div");
            var item = new Element("div");
            var image = new Element("img");
            root.AppendChild(item);
            item.AppendChild(image);

            image.SetAttribute("width", "500");
            image.SetAttribute("height", "200");

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed
                {
                    Display = "grid",
                    Width = 600,
                    GridTemplateColumns = "600px",
                    GridTemplateRows = "minmax(0,1fr)",
                    AlignItems = "center",
                    JustifyItems = "center"
                },
                [item] = new CssComputed
                {
                    Display = "block",
                    HeightPercent = 100,
                    MaxHeight = 230,
                    GridArea = "1/1",
                    Position = "relative"
                },
                [image] = new CssComputed
                {
                    Display = "inline-block",
                    Width = 500,
                    Height = 200,
                    MaxHeightPercent = 100,
                    MaxWidthPercent = 100,
                    ObjectFit = "contain"
                }
            };

            var rootBox = LayoutRoot(root, styles, 600, 900);
            var itemBox = FindBox(rootBox, item);
            var imageBox = FindBox(rootBox, image);

            Assert.NotNull(itemBox);
            Assert.NotNull(imageBox);

            Assert.InRange(rootBox.Geometry.MarginBox.Height, 190f, 260f);
            Assert.True(
                imageBox.Geometry.MarginBox.Top >= rootBox.Geometry.MarginBox.Top - 1f &&
                imageBox.Geometry.MarginBox.Bottom <= rootBox.Geometry.MarginBox.Bottom + 1f,
                $"Expected percentage-height grid item descendants to stay inside the auto-sized grid container. root={rootBox.Geometry.MarginBox} item={itemBox.Geometry.MarginBox} image={imageBox.Geometry.MarginBox}");
        }

        [Fact]
        public void GridFormattingContext_AnonymousInlineRun_DoesNotInheritItemMinHeight()
        {
            var root = new Element("div");
            var item = new Element("div");
            var small = new Element("small");

            item.AppendChild(new Text("Named colors"));
            small.AppendChild(new Text("short note"));
            item.AppendChild(small);
            root.AppendChild(item);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed
                {
                    Display = "grid",
                    Width = 220,
                    GridTemplateColumns = "repeat(auto-fit, minmax(180px, 1fr))",
                    GridAutoRows = "auto"
                },
                [item] = new CssComputed
                {
                    Display = "block",
                    MinHeight = 100,
                    BoxSizing = "border-box",
                    Padding = new Thickness(14, 14, 14, 14),
                    BorderThickness = new Thickness(6, 6, 6, 6),
                    GridColumnStart = "auto",
                    GridRowStart = "auto"
                },
                [small] = new CssComputed
                {
                    Display = "block",
                    Margin = new Thickness(0, 8, 0, 0)
                }
            };

            var rootBox = LayoutRoot(root, styles, 220, 400);
            var itemBox = FindBox(rootBox, item);
            var smallBox = FindBox(rootBox, small);

            Assert.NotNull(itemBox);
            Assert.NotNull(smallBox);

            Assert.InRange(itemBox.Geometry.MarginBox.Height, 99f, 105f);
            Assert.InRange(smallBox.Geometry.BorderBox.Top - itemBox.Geometry.ContentBox.Top, 25f, 40f);
        }

        [Fact]
        public void GridFormattingContext_TextNodeGridItem_StacksBeforeFormControl()
        {
            var label = new Element("label");
            var text = new Text("Text input");
            var input = new Element("input");

            label.AppendChild(text);
            label.AppendChild(input);

            var styles = new Dictionary<Node, CssComputed>
            {
                [label] = new CssComputed
                {
                    Display = "grid",
                    Width = 220,
                    Gap = 6
                },
                [text] = new CssComputed
                {
                    Display = "inline"
                },
                [input] = new CssComputed
                {
                    Display = "inline-block",
                    WidthPercent = 100,
                    Height = 42,
                    BoxSizing = "border-box"
                }
            };

            var rootBox = LayoutRoot(label, styles, 220, 200);
            var textBox = FindBox(rootBox, text);
            var inputBox = FindBox(rootBox, input);

            Assert.NotNull(textBox);
            Assert.NotNull(inputBox);

            Assert.True(
                inputBox.Geometry.MarginBox.Top >= textBox.Geometry.MarginBox.Bottom + 5f,
                $"Expected label text to occupy its own grid row before the input. text={textBox.Geometry.MarginBox} input={inputBox.Geometry.MarginBox}");
            Assert.True(
                rootBox.Geometry.MarginBox.Height >= inputBox.Geometry.MarginBox.Bottom - rootBox.Geometry.MarginBox.Top - 0.5f,
                $"Expected label grid height to include both text and input rows. root={rootBox.Geometry.MarginBox} input={inputBox.Geometry.MarginBox}");
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
