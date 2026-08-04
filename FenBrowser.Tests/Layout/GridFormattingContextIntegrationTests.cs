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

        [Fact]
        public void GridFormattingContext_MinContentSidebar_LeavesRemainingWidthForFlexibleTrack()
        {
            var root = new Element("main");
            var content = new Element("article");
            var sidebar = new Element("aside");
            root.AppendChild(content);
            root.AppendChild(sidebar);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed
                {
                    Display = "grid",
                    Width = 1192,
                    GridTemplateColumns = "minmax(0,1fr) min-content"
                },
                [content] = new CssComputed
                {
                    Display = "block",
                    GridColumnStart = "1",
                    GridColumnEnd = "2",
                    Height = 40
                },
                [sidebar] = new CssComputed
                {
                    Display = "block",
                    Width = 196,
                    GridColumnStart = "2",
                    GridColumnEnd = "3",
                    Height = 40
                }
            };

            var rootBox = LayoutRoot(root, styles, 1192, 200);
            var contentBox = FindBox(rootBox, content);
            var sidebarBox = FindBox(rootBox, sidebar);

            Assert.NotNull(contentBox);
            Assert.NotNull(sidebarBox);
            Assert.Equal(996f, contentBox.Geometry.MarginBox.Width, 1);
            Assert.Equal(996f, sidebarBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(1192f, sidebarBox.Geometry.MarginBox.Right, 1);
        }

        [Fact]
        public void GridFormattingContext_CenteredFlexItem_UsesItsMaxContentWidth()
        {
            var root = new Element("nav");
            var logo = new Element("div");
            var menuWrapper = new Element("div");
            var menu = new Element("nav");
            var search = new Element("button");
            var actions = new Element("div");
            root.AppendChild(logo);
            root.AppendChild(menuWrapper);
            root.AppendChild(search);
            root.AppendChild(actions);
            menuWrapper.AppendChild(menu);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed
                {
                    Display = "grid",
                    Width = 1248,
                    GridTemplateColumns = "min-content 1fr min-content min-content",
                    JustifyItems = "center"
                },
                [logo] = new CssComputed { Display = "block", Width = 83, Height = 24 },
                [menuWrapper] = new CssComputed { Display = "block" },
                [menu] = new CssComputed { Display = "flex", FlexDirection = "row", Height = 36 },
                [search] = new CssComputed { Display = "block", Width = 80, Height = 32 },
                [actions] = new CssComputed { Display = "block", Width = 120, Height = 32 }
            };

            foreach (var labelText in new[] { "HTML", "CSS", "JavaScript", "Web APIs", "All", "Learn", "Tools" })
            {
                var tab = new Element("div");
                var button = new Element("button");
                var label = new Element("span");
                label.AppendChild(new Text(labelText));
                button.AppendChild(label);
                tab.AppendChild(button);
                menu.AppendChild(tab);

                styles[tab] = new CssComputed { Display = "block" };
                styles[button] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    Padding = new Thickness(11.2, 8, 11.2, 8)
                };
                styles[label] = new CssComputed { Display = "inline", FontSize = 16, LineHeight = 1 };
            }

            var rootBox = LayoutRoot(root, styles, 1248, 100);
            var menuBox = FindBox(rootBox, menu);

            Assert.NotNull(menuBox);
            Assert.True(menuBox.Geometry.MarginBox.Width >= 400f,
                $"Expected centered flex menu to keep its max-content width, got {menuBox.Geometry.MarginBox.Width}.");

            for (var index = 1; index < menuBox.Children.Count; index++)
            {
                Assert.True(
                    menuBox.Children[index].Geometry.MarginBox.Left >= menuBox.Children[index - 1].Geometry.MarginBox.Right - 0.5f,
                    $"Expected flex tabs not to overlap at index {index}.");
            }
        }

        [Fact]
        public void GridFormattingContext_NamedContentLineUsesRemTrack()
        {
            var root = new Element("main");
            var content = new Element("section");
            root.AppendChild(content);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed
                {
                    Display = "grid",
                    Width = 1280,
                    GridTemplateColumns = "[extended-full-start] max(1rem,calc(50vw - 720px + 1rem)) [full-start] 1fr [content-start] minmax(0,48rem) [content-end] 1fr [full-end] max(1rem,calc(50vw - 720px + 1rem)) [extended-full-end]"
                },
                [content] = new CssComputed
                {
                    Display = "block",
                    GridColumnStart = "content",
                    Height = 40
                }
            };

            var rootBox = LayoutRoot(root, styles, 1280, 200);
            var contentBox = FindBox(rootBox, content);

            Assert.NotNull(contentBox);
            Assert.Equal(256f, contentBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(768f, contentBox.Geometry.MarginBox.Width, 1);
        }

        [Fact]
        public void GridFormattingContext_NamedContentLineCanSpanToExtendedEnd()
        {
            var root = new Element("main");
            var content = new Element("section");
            root.AppendChild(content);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed
                {
                    Display = "grid",
                    Width = 1280,
                    GridTemplateColumns = "[extended-full-start] 16px [full-start] 1fr [content-start] minmax(0,48rem) [content-end] 1fr [full-end] 16px [extended-full-end]"
                },
                [content] = new CssComputed
                {
                    Display = "block",
                    GridColumnStart = "content",
                    GridColumnEnd = "extended-full-end",
                    Height = 40
                }
            };

            var rootBox = LayoutRoot(root, styles, 1280, 200);
            var contentBox = FindBox(rootBox, content);

            Assert.NotNull(contentBox);
            Assert.Equal(256f, contentBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(1024f, contentBox.Geometry.MarginBox.Width, 1);
        }

        [Fact]
        public void GridFormattingContext_AutoWidthNestedGridUsesContainingBlock()
        {
            var page = new Element("div");
            var grid = new Element("main");
            var content = new Element("section");
            page.AppendChild(grid);
            grid.AppendChild(content);

            var styles = new Dictionary<Node, CssComputed>
            {
                [page] = new CssComputed
                {
                    Display = "block",
                    Width = 1280
                },
                [grid] = new CssComputed
                {
                    Display = "grid",
                    GridTemplateColumns = "[extended-full-start] 16px [full-start] 1fr [content-start] minmax(0,48rem) [content-end] 1fr [full-end] 16px [extended-full-end]"
                },
                [content] = new CssComputed
                {
                    Display = "block",
                    GridColumnStart = "content",
                    GridColumnEnd = "extended-full-end",
                    Height = 40
                }
            };

            var rootBox = LayoutRoot(page, styles, 1280, 200);
            var gridBox = FindBox(rootBox, grid);
            var contentBox = FindBox(rootBox, content);

            Assert.NotNull(gridBox);
            Assert.NotNull(contentBox);
            Assert.Equal(1280f, gridBox.Geometry.MarginBox.Width, 1);
            Assert.Equal(256f, contentBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(1024f, contentBox.Geometry.MarginBox.Width, 1);
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
