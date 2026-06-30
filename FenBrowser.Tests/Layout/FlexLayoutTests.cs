using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class FlexLayoutTests
    {
        private LayoutEngineComputer CreateComputer(Element root, Dictionary<Node, CssComputed> styles)
        {
            return new LayoutEngineComputer(styles, 800, 600);
        }

        private Dictionary<Node, CssComputed> CreateStyles(Element container, CssComputed containerStyle)
        {
            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = containerStyle
            };
            
            foreach (var child in container.Descendants())
            {
                if (!styles.ContainsKey(child))
                {
                    styles[child] = new CssComputed();
                }
            }
            
            return styles;
        }

        private Element CreateFlexContainer(int childCount)
        {
            var container = new Element("div");
            for (int i = 0; i < childCount; i++)
            {
                var child = new Element("div");
                // Give children some size so they can be arranged
                child.SetAttribute("style", "width: 100px; height: 100px;"); 
                container.AppendChild(child);
            }
            return container;
        }

        [Fact]
        public void JustifyContent_FlexStart_Default()
        {
            // Container 500px wide, 3 items of 100px each
            // space remaining: 200px
            // flex-start: [100][100][100]...
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "flex-start",
                Width = 500
            };

            // Set child styles (width/height needs to be in CssComputed for layout engine if not parsing style attr)
            // Note: MinimalLayoutComputer might look at style attributes via GetStyle, but better to provide CssComputed
            var styles = CreateStyles(container, style);
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed { Width = 100, Height = 100 };
            }

            var computer = CreateComputer(container, styles);
            
            // Measure & Arrange
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var boxes = computer.GetAllBoxes();
            var child0 = computer.GetBox(container.Children[0]);
            var child1 = computer.GetBox(container.Children[1]);
            var child2 = computer.GetBox(container.Children[2]);

            // Assert X positions
            Assert.Equal(0, child0.ContentBox.Left);
            Assert.Equal(100, child1.ContentBox.Left);
            Assert.Equal(200, child2.ContentBox.Left);
        }

        [Fact]
        public void JustifyContent_FlexEnd()
        {
            // Container 500px, 3 items (300px total)
            // flex-end: ...[100][100][100]
            // Start position = 500 - 300 = 200
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "flex-end",
                Width = 500
            };

            var styles = CreateStyles(container, style);
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed { Width = 100, Height = 100 };
            }

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            
            Assert.Equal(200, child0.ContentBox.Left);
            Assert.Equal(300, computer.GetBox(container.Children[1]).ContentBox.Left);
            Assert.Equal(400, computer.GetBox(container.Children[2]).ContentBox.Left);
        }

        [Fact]
        public void FlexEnd_BorderBoxMinWidthPill_DoesNotOverlapAdjacentIcon()
        {
            var container = new Element("div");
            var icon = new Element("a");
            var signIn = new Element("a");
            container.AppendChild(icon);
            container.AppendChild(signIn);

            var styles = CreateStyles(container, new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "flex-end",
                AlignItems = "center",
                Width = 180,
                Height = 48
            });
            styles[icon] = new CssComputed
            {
                Display = "inline-block",
                Width = 40,
                Height = 40,
                BoxSizing = "border-box",
                Padding = new FenBrowser.Core.Thickness(4, 4, 4, 4)
            };
            styles[signIn] = new CssComputed
            {
                Display = "inline-block",
                MinWidth = 85,
                Height = 40,
                BoxSizing = "border-box",
                Padding = new FenBrowser.Core.Thickness(10, 12, 10, 12),
                FlexShrink = 1
            };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(180, 48));
            computer.Arrange(container, new SKRect(0, 0, 180, 48));

            var iconBox = computer.GetBox(icon);
            var signInBox = computer.GetBox(signIn);

            Assert.True(iconBox.MarginBox.Right <= signInBox.MarginBox.Left + 0.5f);
            Assert.True(signInBox.MarginBox.Width >= 85f);
        }

        [Fact]
        public void JustifyContent_Center()
        {
            // Container 500px, 3 items (300px total)
            // center: ..[100][100][100]..
            // Margin = (500 - 300) / 2 = 100
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "center",
                Width = 500
            };

            var styles = CreateStyles(container, style);
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed { Width = 100, Height = 100 };
            }

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            
            Assert.Equal(100, child0.ContentBox.Left);
            Assert.Equal(200, computer.GetBox(container.Children[1]).ContentBox.Left);
            Assert.Equal(300, computer.GetBox(container.Children[2]).ContentBox.Left);
        }
        [Fact]
        public void JustifyContent_SpaceBetween()
        {
            // Container 500px, 3 items (300px total)
            // space-between: [100]...[100]...[100]
            // Remaining 200px. 2 gaps. Gap = 100px.
            // Pos: 0, 200, 400
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "space-between",
                Width = 500
            };

            var styles = CreateStyles(container, style);
            foreach (var child in container.Descendants())
                styles[child] = new CssComputed { Width = 100, Height = 100 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            var child1 = computer.GetBox(container.Children[1]);
            var child2 = computer.GetBox(container.Children[2]);
            
            Assert.Equal(0, child0.ContentBox.Left);
            Assert.Equal(200, child1.ContentBox.Left);
            Assert.Equal(400, child2.ContentBox.Left);
        }

        [Fact]
        public void JustifyContent_SpaceEvenly()
        {
            // Container 500px, 3 items (300px total)
            // space-evenly: .[100].[100].[100].
            // Remaining 200px. 4 gaps. Gap = 50px.
            // Pos: 50, 250 (50+100+50+100? No. 50(gap)+100(item)+50(gap) = 200 start of next), wait.
            // Items at: 
            // 1: 50
            // 2: 50 + 100 + 50 = 200
            // 3: 200 + 100 + 50 = 350
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "space-evenly",
                Width = 500
            };

            var styles = CreateStyles(container, style);
            foreach (var child in container.Descendants())
                styles[child] = new CssComputed { Width = 100, Height = 100 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            var child1 = computer.GetBox(container.Children[1]);
            var child2 = computer.GetBox(container.Children[2]);
            
            Assert.Equal(50, child0.ContentBox.Left);
            Assert.Equal(200, child1.ContentBox.Left);
            Assert.Equal(350, child2.ContentBox.Left);
        }

        [Fact]
        public void AlignItems_Center()
        {
            // Container height 600px. Item height 100px.
            // Center Y = (600 - 100) / 2 = 250.
            
            var container = CreateFlexContainer(1);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "center",
                Width = 500,
                Height = 600
            };

            var styles = CreateStyles(container, style);
            styles[container.Children[0]] = new CssComputed { Width = 100, Height = 100 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            
            Assert.Equal(250, child0.ContentBox.Top);
        }

        [Fact]
        public void AlignItems_Baseline_UsesItemBaselinesInsteadOfFlexStart()
        {
            var container = CreateFlexContainer(2);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "baseline",
                Width = 400,
                Height = 200
            };

            var styles = CreateStyles(container, style);
            styles[container.Children[0]] = new CssComputed { Width = 100, Height = 100 };
            styles[container.Children[1]] = new CssComputed { Width = 100, Height = 50 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(400, 200));
            computer.Arrange(container, new SKRect(0, 0, 400, 200));

            var tall = computer.GetBox(container.Children[0]);
            var shortBox = computer.GetBox(container.Children[1]);

            Assert.Equal(0, tall.ContentBox.Top);
            Assert.Equal(50, shortBox.ContentBox.Top);
        }

        [Fact]
        public void AlignSelf_Baseline_OverridesContainerCrossAlignment()
        {
            var container = CreateFlexContainer(2);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "flex-end",
                Width = 400,
                Height = 200
            };

            var styles = CreateStyles(container, style);
            styles[container.Children[0]] = new CssComputed { Width = 100, Height = 100, AlignSelf = "baseline" };
            styles[container.Children[1]] = new CssComputed { Width = 100, Height = 50, AlignSelf = "baseline" };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(400, 200));
            computer.Arrange(container, new SKRect(0, 0, 400, 200));

            var tall = computer.GetBox(container.Children[0]);
            var shortBox = computer.GetBox(container.Children[1]);

            Assert.Equal(0, tall.ContentBox.Top);
            Assert.Equal(50, shortBox.ContentBox.Top);
        }

        [Fact]
        public void AlignItems_Stretch()
        {
            // Container height 600px. Item height auto.
            // Should stretch to 600px.
            
            var container = CreateFlexContainer(1);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "stretch",
                Width = 500,
                Height = 600
            };

            var styles = CreateStyles(container, style);
            // No explicit height set
            styles[container.Children[0]] = new CssComputed { Width = 100 }; // Height defaults to Auto?

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(500, 600));
            computer.Arrange(container, new SKRect(0, 0, 500, 600));

            var child0 = computer.GetBox(container.Children[0]);
            
            Assert.Equal(600, child0.ContentBox.Height);
        }

        [Fact]
        public void FlexWrap_Wrap()
        {
            // Container 300px. Items 150px. 3 items.
            // Row 1: Item 1, Item 2 (total 300px).
            // Row 2: Item 3.
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                FlexWrap = "wrap",
                Width = 300
            };

            var styles = CreateStyles(container, style);
            foreach (var child in container.Descendants())
                styles[child] = new CssComputed { Width = 150, Height = 100 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(300, 600));
            computer.Arrange(container, new SKRect(0, 0, 300, 600));

            var c0 = computer.GetBox(container.Children[0]);
            var c1 = computer.GetBox(container.Children[1]);
            var c2 = computer.GetBox(container.Children[2]);
            
            // Row 1
            Assert.Equal(0, c0.ContentBox.Top);
            Assert.Equal(0, c1.ContentBox.Top);
            
            // Row 2 (Top should be >= 100)
            Assert.True(c2.ContentBox.Top >= 100);
            Assert.Equal(0, c2.ContentBox.Left); // Should start new line
        }
        [Fact]
        public void FlexWrap_WrapReverse()
        {
            // Container 300px. Items 150px. 3 items.
            // wrap-reverse:
            // Row 1 (Bottom): Item 1, Item 2.
            // Row 2 (Top): Item 3.
            
            // Note: Since we don't have explicit line-height calculation easily exposed without digging deep,
            // we mainly check the Y order. Row 2 should be ABOVE Row 1.
            
            var container = CreateFlexContainer(3);
            var style = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                FlexWrap = "wrap-reverse",
                Width = 300,
                Height = 200 // Explicit height to see bottom alignment clearly
            };

            var styles = CreateStyles(container, style);
            // Items height 100px.
            foreach (var child in container.Descendants())
                styles[child] = new CssComputed { Width = 150, Height = 100 };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(300, 200));
            computer.Arrange(container, new SKRect(0, 0, 300, 200));

            var c0 = computer.GetBox(container.Children[0]); // Row 1 (Bottom, because it fills from Start in cross axis, but reverse means Start is Bottom?)
            // Wait: wrap-reverse means "cross-start" and "cross-end" are swapped?
            // "The cross-start line and cross-end line are swapped."
            // So lines are laid out from Bottom to Top (if column direction is down).
            
            // Line 1 contains Item 1, Item 2. It should be at the "Start" of the Block axis?
            // Cross axis direction: valid "row" means cross axis is vertical (top->bottom).
            // wrap-reverse means lines flow bottom->top.
            
            // So Line 1 (items 1, 2) is at Bottom. Y=100 (if height 200 and line height 100).
            // Line 2 (item 3) is at Top. Y=0.
            
            var c2 = computer.GetBox(container.Children[2]);
            
            // Assert Line 2 (Item 3) is ABOVE Line 1 (Item 0)
            Assert.True(c2.ContentBox.Top < c0.ContentBox.Top);
        }

        [Fact]
        public void NestedFlex_ParentAlignsChildFlexContainer()
        {
            // Parent: Flex, Center.
            // Child: Flex, 3 items.
            
            var parent = new Element("div");
            var childContainer = CreateFlexContainer(3); // 3 items of 100px = 300px width.
            parent.AppendChild(childContainer);
            
            var parentStyle = new CssComputed
            {
                Display = "flex",
                JustifyContent = "center",
                Width = 500,
                Height = 500
            };
            
            var childContainerStyle = new CssComputed
            {
                Display = "flex",
                Width = 300, // Explicit width matches content
                Height = 100
            };
            
            var styles = new Dictionary<Node, CssComputed>();
            styles[parent] = parentStyle;
            styles[childContainer] = childContainerStyle;
            foreach(var gc in childContainer.Children)
                 styles[gc] = new CssComputed { Width = 100, Height = 100 };
            
            var computer = CreateComputer(parent, styles);
            computer.Measure(parent, new SKSize(500, 500));
            computer.Arrange(parent, new SKRect(0, 0, 500, 500));
            
            var childBox = computer.GetBox(childContainer);
            
            // Parent centers childContainer. 
            // ChildContainer width 300. Parent 500. Center X = 100.
            Assert.Equal(100, childBox.ContentBox.Left);
            
            // Verify grandchild positions relative to childContainer?
            // Note: GetBox returns coordinates relative to *viewport* or *root* layout context?
            // MinimalLayoutComputer usually returns relative to parent? No, Arrange passes `finalRect` which is usually absolute in the layout context.
            // But internal recursion usually passes offset.
            // Let's check Grandchild.
            // Grandchild 0 should be at 0 relative to childContainer.
            // Grandchild 0 X in global = 100 + 0 = 100.
            
            var gc0 = computer.GetBox(childContainer.Children[0]);
            Assert.Equal(100, gc0.ContentBox.Left);
        }

        [Fact]
        public async System.Threading.Tasks.Task ColumnMinHeightDvh_AllowsFlexOneHeroToCenterContent()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    #shell { display: flex; flex-direction: column; min-height: 100dvh; }
    #hero { display: flex; flex: 1; align-items: center; }
    #card { width: 100px; height: 100px; }
    #footer { height: 40px; }
  </style>
</head>
<body>
  <div id='shell'>
    <div id='hero'><div id='card'></div></div>
    <footer id='footer'></footer>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await FenBrowser.FenEngine.Rendering.CssLoader.ComputeAsync(
                root,
                new System.Uri("https://x.test/"),
                null,
                viewportWidth: 800,
                viewportHeight: 600);

            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var hero = doc.GetElementById("hero");
            var card = doc.GetElementById("card");
            var heroBox = computer.GetBox(hero);
            var cardBox = computer.GetBox(card);

            Assert.True(heroBox.ContentBox.Height >= 550, $"Expected flex:1 hero to fill viewport remainder, got {heroBox.ContentBox.Height}.");
            Assert.InRange(cardBox.ContentBox.Top, 220f, 240f);
        }

        [Fact]
        public async System.Threading.Tasks.Task AlignItemsCenter_WithPaddedRow_DoesNotStretchAutoHeightBlockChild()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    #container { display: flex; align-items: center; width: 800px; height: 852px; padding-top: 40px; padding-bottom: 40px; box-sizing: border-box; }
    #child { width: 400px; }
    #content { height: 200px; }
  </style>
</head>
<body>
  <div id='container'><div id='child'><div id='content'></div></div></div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await FenBrowser.FenEngine.Rendering.CssLoader.ComputeAsync(
                root,
                new System.Uri("https://x.test/"),
                null,
                viewportWidth: 800,
                viewportHeight: 900);

            var computer = new LayoutEngineComputer(styles, 800, 900);
            computer.Measure(doc, new SKSize(800, 900));
            computer.Arrange(doc, new SKRect(0, 0, 800, 900));

            var child = doc.GetElementById("child");
            var childBox = computer.GetBox(child);

            Assert.InRange(childBox.ContentBox.Height, 199f, 201f);
            Assert.InRange(childBox.ContentBox.Top, 325f, 327f);
        }

        [Fact]
        public void StretchedFlexItem_ReflowsDescendantsWithForcedCrossSize()
        {
            var outer = new Element("div");
            var panel = new Element("div");
            var child = new Element("div");
            panel.AppendChild(child);
            outer.AppendChild(panel);

            var styles = CreateStyles(outer, new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "stretch",
                Width = 800,
                Height = 852
            });

            styles[panel] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "center",
                FlexGrow = 1,
                Padding = new FenBrowser.Core.Thickness(40, 0, 40, 0)
            };
            styles[child] = new CssComputed { Display = "block", Width = 400, Height = 200 };

            var computer = CreateComputer(outer, styles);
            computer.Measure(outer, new SKSize(800, 900));
            computer.Arrange(outer, new SKRect(0, 0, 800, 900));

            var panelBox = computer.GetBox(panel);
            var childBox = computer.GetBox(child);

            Assert.InRange(panelBox.MarginBox.Height, 851.5f, 852.5f);
            Assert.InRange(childBox.ContentBox.Top, 325f, 327f);
        }

        [Fact]
        public void FlexRow_AutoWidthBlockItem_DoesNotKeepInfiniteProbeWidth()
        {
            var container = new Element("div");
            var item = new Element("div");
            var headline = new Element("h2");
            headline.AppendChild(new Text("MacBook Air"));
            item.AppendChild(headline);
            container.AppendChild(item);

            var styles = CreateStyles(container, new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                Width = 1920,
                Height = 804
            });

            styles[item] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column"
            };
            styles[headline] = new CssComputed
            {
                Display = "block",
                TextAlign = SKTextAlign.Center
            };

            var computer = CreateComputer(container, styles);
            computer.Measure(container, new SKSize(1920, 804));
            computer.Arrange(container, new SKRect(0, 0, 1920, 804));

            var itemBox = computer.GetBox(item);
            var headlineBox = computer.GetBox(headline);

            Assert.True(
                itemBox.ContentBox.Width <= 1921f,
                $"Expected flex item width to remain bounded by container; got {itemBox.ContentBox.Width}");
            Assert.True(
                headlineBox.ContentBox.Left < 5000f,
                $"Expected headline geometry to remain finite; got left={headlineBox.ContentBox.Left}");
        }

        [Fact]
        public void InlineCustomElementFlexItem_IsBlockifiedAndSizesFromBlockDescendants()
        {
            var headerRow = new Element("div");
            var partial = new Element("react-partial");
            var reactRoot = new Element("div");
            var nav = new Element("nav");
            var list = new Element("ul");

            headerRow.AppendChild(partial);
            partial.AppendChild(reactRoot);
            reactRoot.AppendChild(nav);
            nav.AppendChild(list);

            var buttons = new List<Element>();
            foreach (var label in new[] { "Platform", "Solutions", "Resources", "Open Source", "Enterprise", "Pricing" })
            {
                var item = new Element("li");
                var button = new Element("button");
                button.AppendChild(new Text(label));
                item.AppendChild(button);
                list.AppendChild(item);
                buttons.Add(button);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [headerRow] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    Width = 720,
                    Height = 60
                },
                [partial] = new CssComputed { Display = "inline" },
                [reactRoot] = new CssComputed { Display = "block" },
                [nav] = new CssComputed { Display = "block" },
                [list] = new CssComputed { Display = "flex", FlexDirection = "row" }
            };

            foreach (var button in buttons)
            {
                styles[button.ParentNode] = new CssComputed { Display = "list-item" };
                styles[button] = new CssComputed
                {
                    Display = "flex",
                    Padding = new Thickness(8),
                    FontSize = 16
                };
            }

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(headerRow);

            var partialBox = FindBox(rootBox, partial);
            Assert.IsType<BlockBox>(partialBox);

            var state = new LayoutState(
                new SKSize(720, 60),
                720,
                60,
                720,
                60);

            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var listBox = FindBox(rootBox, list);
            var firstButtonBox = FindBox(rootBox, buttons[0]);
            var lastButtonBox = FindBox(rootBox, buttons[^1]);

            Assert.NotNull(listBox);
            Assert.NotNull(firstButtonBox);
            Assert.NotNull(lastButtonBox);
            Assert.True(partialBox.Geometry.ContentBox.Width > 360f, $"Expected inline custom flex item to size from nav descendants, got partial={partialBox.Geometry.ContentBox.Width}, list={listBox.Geometry.ContentBox.Width}, first={firstButtonBox.Geometry.MarginBox}, last={lastButtonBox.Geometry.MarginBox}.");
            Assert.True(listBox.Geometry.ContentBox.Width > 360f, $"Expected nav flex list to retain child button width, got {listBox.Geometry.ContentBox.Width}.");
            Assert.True(lastButtonBox.Geometry.MarginBox.Left > firstButtonBox.Geometry.MarginBox.Right, $"Expected nav buttons to advance horizontally without overlap, first={firstButtonBox.Geometry.MarginBox} last={lastButtonBox.Geometry.MarginBox}.");
        }

        [Fact]
        public void RowFlexShrink_PreservesVisibleTextDescendantWidth()
        {
            var row = new Element("ul");
            var firstItem = new Element("li");
            var secondItem = new Element("li");
            var firstButton = new Element("button");
            var secondButton = new Element("button");
            var firstText = new Text("Open Source");
            var secondText = new Text("Enterprise");

            firstButton.AppendChild(firstText);
            secondButton.AppendChild(secondText);
            firstItem.AppendChild(firstButton);
            secondItem.AppendChild(secondButton);
            row.AppendChild(firstItem);
            row.AppendChild(secondItem);

            var styles = new Dictionary<Node, CssComputed>
            {
                [row] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    Width = 120,
                    Height = 44
                },
                [firstItem] = new CssComputed { Display = "list-item" },
                [secondItem] = new CssComputed { Display = "list-item" },
                [firstButton] = new CssComputed
                {
                    Display = "flex",
                    Padding = new Thickness(8),
                    FontSize = 16
                },
                [secondButton] = new CssComputed
                {
                    Display = "flex",
                    Padding = new Thickness(8),
                    FontSize = 16
                }
            };

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(row);

            var state = new LayoutState(
                new SKSize(120, 44),
                120,
                44,
                120,
                44);

            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var firstButtonBox = FindBox(rootBox, firstButton);
            var secondButtonBox = FindBox(rootBox, secondButton);
            var firstTextBox = FindBox(rootBox, firstText);

            Assert.NotNull(firstButtonBox);
            Assert.NotNull(secondButtonBox);
            Assert.NotNull(firstTextBox);
            Assert.True(
                firstButtonBox.Geometry.MarginBox.Width >= firstTextBox.Geometry.MarginBox.Width,
                $"Expected first button to keep at least its text width, button={firstButtonBox.Geometry.MarginBox}, text={firstTextBox.Geometry.MarginBox}.");
            Assert.True(
                secondButtonBox.Geometry.MarginBox.Left > firstTextBox.Geometry.MarginBox.Right,
                $"Expected second button to advance after the first text instead of overlapping it, firstText={firstTextBox.Geometry.MarginBox}, second={secondButtonBox.Geometry.MarginBox}.");
        }

        [Fact]
        public void RowFlex_WidthAutoOverride_ClearsStalePercentWidthBeforeIntrinsicProbe()
        {
            var header = new Element("div");
            var logoShell = new Element("div");
            var mobileSpacer = new Element("div");
            var logo = new Element("a");
            var menu = new Element("div");
            var search = new Element("div");
            var signIn = new Element("a");

            logoShell.AppendChild(mobileSpacer);
            logoShell.AppendChild(logo);
            menu.AppendChild(search);
            menu.AppendChild(signIn);
            header.AppendChild(logoShell);
            header.AppendChild(menu);

            var logoShellStyle = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "space-between",
                WidthPercent = 100
            };
            logoShellStyle.Map["width"] = "auto";

            var styles = new Dictionary<Node, CssComputed>
            {
                [header] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    AlignItems = "center",
                    Width = 600,
                    Height = 60
                },
                [logoShell] = logoShellStyle,
                [mobileSpacer] = new CssComputed { Display = "block", FlexGrow = 1 },
                [logo] = new CssComputed { Display = "inline-block", Width = 40, Height = 32 },
                [menu] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    FlexGrow = 1,
                    Height = 60
                },
                [search] = new CssComputed { Display = "block", Width = 160, Height = 32 },
                [signIn] = new CssComputed { Display = "inline-block", Width = 56, Height = 32 }
            };

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(header);

            var state = new LayoutState(
                new SKSize(600, 60),
                600,
                60,
                600,
                60);

            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var logoShellBox = FindBox(rootBox, logoShell);
            var logoBox = FindBox(rootBox, logo);
            var menuBox = FindBox(rootBox, menu);

            Assert.NotNull(logoShellBox);
            Assert.NotNull(logoBox);
            Assert.NotNull(menuBox);
            Assert.True(
                logoShellBox.Geometry.MarginBox.Width < 100f,
                $"Expected width:auto to clear stale width:100% projection and shrink-wrap the logo shell. logoShell={logoShellBox.Geometry.MarginBox} menu={menuBox.Geometry.MarginBox}");
            Assert.True(
                logoBox.Geometry.MarginBox.Left < 80f,
                $"Expected logo to remain near row start instead of being pushed by a full-width logo shell. logo={logoBox.Geometry.MarginBox} logoShell={logoShellBox.Geometry.MarginBox}");
            Assert.True(
                menuBox.Geometry.MarginBox.Left < 120f && menuBox.Geometry.MarginBox.Width > 480f,
                $"Expected menu to consume remaining header width after the shrink-wrapped logo shell. logoShell={logoShellBox.Geometry.MarginBox} menu={menuBox.Geometry.MarginBox}");
        }

        [Fact]
        public void FlexColumn_MaxWidthAutoInlineMargins_CentersCrossAxisContent()
        {
            var container = new Element("div");
            var item = new Element("div");
            var child = new Element("div");
            item.AppendChild(child);
            container.AppendChild(item);

            var styles = CreateStyles(container, new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                Width = 1920,
                Height = 899
            });

            styles[item] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                WidthPercent = 100,
                MaxWidth = 1280,
                MarginLeftAuto = true,
                MarginRightAuto = true,
                Height = 400
            };
            styles[item].Map["margin-inline"] = "auto";
            styles[child] = new CssComputed { Display = "block", Width = 100, Height = 100 };

            var computer = new LayoutEngineComputer(styles, 1920, 899);
            computer.Measure(container, new SKSize(1920, 899));
            computer.Arrange(container, new SKRect(0, 0, 1920, 899));

            var itemBox = computer.GetBox(item);
            var childBox = computer.GetBox(child);

            Assert.InRange(itemBox.ContentBox.Width, 1279.5f, 1280.5f);
            Assert.InRange(itemBox.ContentBox.Left, 319.5f, 320.5f);
            Assert.InRange(childBox.ContentBox.Left, 319.5f, 320.5f);
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


