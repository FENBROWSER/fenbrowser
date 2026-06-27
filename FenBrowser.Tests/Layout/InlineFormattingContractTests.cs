using System;
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

        [Fact]
        public void FlexItem_WithNestedInlineSpans_KeepsInlineRunHorizontal()
        {
            var header = new Element("div");
            var logo = new Element("span");
            var firstO = new Element("span");
            var secondO = new Element("span");
            var l = new Element("span");
            var sorry = new Element("span");

            var gText = new Text("G");
            var firstOText = new Text("o");
            var secondOText = new Text("o");
            var middleText = new Text("g");
            var lText = new Text("l");
            var eText = new Text("e");

            firstO.AppendChild(firstOText);
            secondO.AppendChild(secondOText);
            l.AppendChild(lText);

            logo.AppendChild(gText);
            logo.AppendChild(firstO);
            logo.AppendChild(secondO);
            logo.AppendChild(middleText);
            logo.AppendChild(l);
            logo.AppendChild(eText);

            sorry.AppendChild(new Text(" - Sorry"));
            header.AppendChild(logo);
            header.AppendChild(sorry);

            var logoStyle = new CssComputed
            {
                Display = "inline",
                FontSize = 22,
                LineHeight = 28,
                FontFamilyName = "Arial"
            };
            var nestedStyle = new CssComputed
            {
                Display = "inline",
                FontSize = 22,
                LineHeight = 28,
                FontFamilyName = "Arial"
            };

            var styles = new Dictionary<Node, CssComputed>
            {
                [header] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    AlignItems = "center",
                    Width = 400,
                    Height = 64
                },
                [logo] = logoStyle,
                [firstO] = nestedStyle,
                [secondO] = nestedStyle,
                [l] = nestedStyle,
                [sorry] = new CssComputed
                {
                    Display = "inline",
                    FontSize = 12,
                    LineHeight = 16,
                    FontFamilyName = "Arial"
                }
            };

            var rootBox = LayoutRoot(header, styles, 400, 64);
            var textBoxes = new List<TextLayoutBox>();
            CollectTextBoxes(rootBox, textBoxes);

            var logoTextBoxes = new[]
            {
                FindTextBox(textBoxes, gText),
                FindTextBox(textBoxes, firstOText),
                FindTextBox(textBoxes, secondOText),
                FindTextBox(textBoxes, middleText),
                FindTextBox(textBoxes, lText),
                FindTextBox(textBoxes, eText),
            };

            Assert.All(logoTextBoxes, Assert.NotNull);

            var top = logoTextBoxes[0].Geometry.MarginBox.Top;
            for (var i = 1; i < logoTextBoxes.Length; i++)
            {
                Assert.True(
                    Math.Abs(logoTextBoxes[i].Geometry.MarginBox.Top - top) <= 1.5f,
                    $"Expected logo text to share one line. first={logoTextBoxes[0].Geometry.MarginBox} current={logoTextBoxes[i].Geometry.MarginBox}");
                Assert.True(
                    logoTextBoxes[i].Geometry.MarginBox.Left > logoTextBoxes[i - 1].Geometry.MarginBox.Left,
                    $"Expected logo text to advance horizontally. previous={logoTextBoxes[i - 1].Geometry.MarginBox} current={logoTextBoxes[i].Geometry.MarginBox}");
            }
        }

        [Fact]
        public void InlineBr_ForcesNextTextRunOntoNewLine()
        {
            var root = new Element("div");
            var paragraph = new Element("p");
            var before = new Text("before");
            var br = new Element("br");
            var after = new Text("after");

            paragraph.AppendChild(before);
            paragraph.AppendChild(br);
            paragraph.AppendChild(after);
            root.AppendChild(paragraph);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 400, Height = 200 },
                [paragraph] = new CssComputed { Display = "block", Width = 400, FontSize = 16, LineHeight = 20 },
                [br] = new CssComputed { Display = "inline", FontSize = 16, LineHeight = 20 },
            };

            var rootBox = LayoutRoot(root, styles, 400, 200);
            var textBoxes = new List<TextLayoutBox>();
            CollectTextBoxes(rootBox, textBoxes);

            var beforeBox = FindTextBox(textBoxes, before);
            var afterBox = FindTextBox(textBoxes, after);

            Assert.NotNull(beforeBox);
            Assert.NotNull(afterBox);
            Assert.True(
                afterBox.Geometry.MarginBox.Top > beforeBox.Geometry.MarginBox.Top + 1f,
                $"Expected <br> to force the following text onto a new line. before={beforeBox.Geometry.MarginBox} after={afterBox.Geometry.MarginBox}");
            Assert.True(
                afterBox.Geometry.MarginBox.Left <= beforeBox.Geometry.MarginBox.Left + 1f,
                $"Expected text after <br> to restart near line start. before={beforeBox.Geometry.MarginBox} after={afterBox.Geometry.MarginBox}");
        }

        [Fact]
        public void BlockSpan_WithText_InFlexContainer_GetsNonZeroHeight()
        {
            // Reproduction of GitHub sidebar: flex container → li → span(display:block) with text
            var nav = new Element("nav");
            var ul = new Element("ul");
            var li = new Element("li");
            var span = new Element("span");
            span.AppendChild(new Text("Repositories"));

            li.AppendChild(span);
            ul.AppendChild(li);
            nav.AppendChild(ul);

            var root = new Element("div");
            root.AppendChild(nav);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 300 },
                [nav] = new CssComputed { Display = "flex", FlexDirection = "column", Width = 256 },
                [ul] = new CssComputed { Display = "block" },
                [li] = new CssComputed { Display = "list-item", FontSize = 14, LineHeight = 20 },
                [span] = new CssComputed { Display = "block", FontSize = 14, LineHeight = 20 },
            };

            var rootBox = LayoutRoot(root, styles, 400, 800);
            var spanBox = FindBox(rootBox, span);
            var liBox = FindBox(rootBox, li);

            Assert.NotNull(spanBox);
            Assert.NotNull(liBox);

            float spanH = spanBox!.Geometry.ContentBox.Height;
            float liH = liBox!.Geometry.ContentBox.Height;
            Assert.True(spanH > 1f, $"Expected span to have non-zero height, got ContentBox.Height={spanH:F2}. MarginBox={spanBox.Geometry.MarginBox}");
            Assert.True(liH > 1f, $"Expected li to have non-zero height, got ContentBox.Height={liH:F2}. MarginBox={liBox.Geometry.MarginBox}");
        }

        [Fact]
        public void ColumnFlexGroup_WithBlockTitleAndNestedFlexList_DerivesNonZeroHeight()
        {
            var root = new Element("div");
            var nav = new Element("nav");
            var gridList = new Element("ul");
            var gridItem = new Element("li");
            var group = new Element("div");
            var title = new Element("span");
            var nestedList = new Element("ul");
            var nestedItem = new Element("li");
            var link = new Element("a");
            var linkTitle = new Element("span");

            var titleText = new Text("AI CODE CREATION");
            var linkText = new Text("GitHub Copilot");
            title.AppendChild(titleText);
            linkTitle.AppendChild(linkText);
            link.AppendChild(linkTitle);
            nestedItem.AppendChild(link);
            nestedList.AppendChild(nestedItem);
            group.AppendChild(title);
            group.AppendChild(nestedList);
            gridItem.AppendChild(group);
            gridList.AppendChild(gridItem);
            nav.AppendChild(gridList);
            root.AppendChild(nav);

            var groupStyle = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                Width = 200,
                FontSize = 16,
                LineHeight = 1.5,
            };
            groupStyle.Map["height"] = "100%";

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 800 },
                [nav] = new CssComputed { Display = "block", Width = 0 },
                [gridList] = new CssComputed { Display = "grid", Width = 0 },
                [gridItem] = new CssComputed { Display = "list-item", FontSize = 16, LineHeight = 1.5 },
                [group] = groupStyle,
                [title] = new CssComputed { Display = "block", FontSize = 12, LineHeight = 1.5 },
                [nestedList] = new CssComputed { Display = "flex", FlexDirection = "column", FontSize = 16, LineHeight = 1.5 },
                [nestedItem] = new CssComputed { Display = "list-item", FontSize = 16, LineHeight = 1.5 },
                [link] = new CssComputed { Display = "flex", FontSize = 16, LineHeight = 1.5 },
                [linkTitle] = new CssComputed { Display = "block", FontSize = 16, LineHeight = 1.5 },
            };

            var rootBox = LayoutRoot(root, styles, 800, 600);
            var groupBox = FindBox(rootBox, group);
            var titleBox = FindBox(rootBox, title);
            var gridListBox = FindBox(rootBox, gridList);
            var gridItemBox = FindBox(rootBox, gridItem);
            var nestedListBox = FindBox(rootBox, nestedList);
            var textBoxes = new List<TextLayoutBox>();
            CollectTextBoxes(rootBox, textBoxes);
            var titleTextBox = FindTextBox(textBoxes, titleText);

            Assert.NotNull(groupBox);
            Assert.NotNull(titleBox);
            Assert.NotNull(gridListBox);
            Assert.NotNull(gridItemBox);
            Assert.NotNull(nestedListBox);

            Assert.True(
                titleBox!.Geometry.ContentBox.Height >= 17f,
                $"Expected unitless line-height to resolve against title font-size. title={titleBox.Geometry.MarginBox} titleText={titleTextBox?.Geometry.MarginBox} group={groupBox!.Geometry.MarginBox} li={gridItemBox!.Geometry.MarginBox} groupDisplay={groupBox.ComputedStyle?.Display} groupRawHeight={GetRawHeight(groupBox.ComputedStyle)} groupHeightPercent={groupBox.ComputedStyle?.HeightPercent}");
            Assert.True(
                string.IsNullOrEmpty(groupBox!.ComputedStyle?.HeightExpression),
                $"Expected raw percentage height to stay typed as HeightPercent, not HeightExpression={groupBox.ComputedStyle?.HeightExpression}.");
            Assert.True(
                groupBox!.Geometry.ContentBox.Height < 600f,
                $"Expected unresolved percent-height flex group to size from content, not viewport. grid={gridListBox!.Geometry.MarginBox} li={gridItemBox!.Geometry.MarginBox} group={groupBox.Geometry.MarginBox} title={titleBox.Geometry.MarginBox} nested={nestedListBox!.Geometry.MarginBox} titleText={titleTextBox?.Geometry.MarginBox} groupParent={(groupBox.Parent?.SourceNode as Element)?.TagName}/{groupBox.Parent?.ComputedStyle?.Display} groupRawHeight={GetRawHeight(groupBox.ComputedStyle)} groupHeightPercent={groupBox.ComputedStyle?.HeightPercent} liHeightPercent={gridItemBox.ComputedStyle?.HeightPercent}");
            Assert.True(groupBox!.Geometry.ContentBox.Height >= titleBox.Geometry.ContentBox.Height, $"Expected column flex group to include its title height. group={groupBox.Geometry.MarginBox} title={titleBox.Geometry.MarginBox}");
            Assert.True(gridItemBox!.Geometry.ContentBox.Height >= groupBox.Geometry.ContentBox.Height, $"Expected list item to include flex group height. li={gridItemBox.Geometry.MarginBox} group={groupBox.Geometry.MarginBox}");
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

        private static string GetRawHeight(CssComputed style)
        {
            return style?.Map != null && style.Map.TryGetValue("height", out var value)
                ? value
                : null;
        }

        private static void CollectTextBoxes(LayoutBox box, List<TextLayoutBox> result)
        {
            if (box == null)
            {
                return;
            }

            if (box is TextLayoutBox textBox)
            {
                result.Add(textBox);
            }

            foreach (var child in box.Children)
            {
                CollectTextBoxes(child, result);
            }
        }

        private static TextLayoutBox FindTextBox(IEnumerable<TextLayoutBox> textBoxes, Text text)
        {
            foreach (var box in textBoxes)
            {
                if (ReferenceEquals(box.SourceNode, text))
                {
                    return box;
                }
            }

            return null;
        }
    }
}
