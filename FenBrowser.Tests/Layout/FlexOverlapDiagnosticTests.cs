using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// Diagnostic tests for element overlap issues using the ACTIVE layout pipeline
    /// (BoxTreeBuilder + FormattingContext.Resolve + Layout), not the legacy MinimalLayoutComputer.
    /// Simulates Google's top nav bar pattern.
    /// </summary>
    public class FlexOverlapDiagnosticTests
    {
        private readonly ITestOutputHelper _output;

        public FlexOverlapDiagnosticTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private LayoutBox LayoutRoot(Element root, Dictionary<Node, CssComputed> styles, float width = 1200, float height = 800)
        {
            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            Assert.NotNull(rootBox);

            var state = new LayoutState(
                new SKSize(width, height),
                width, height,
                width, height);

            var context = FormattingContext.Resolve(rootBox);
            context.Layout(rootBox, state);
            return rootBox;
        }

        private LayoutBox FindBox(LayoutBox box, Node target)
        {
            if (box == null) return null;
            if (ReferenceEquals(box.SourceNode, target)) return box;
            foreach (var child in box.Children)
            {
                var found = FindBox(child, target);
                if (found != null) return found;
            }
            return null;
        }

        private void DumpBoxTree(LayoutBox box, int indent = 0)
        {
            if (box == null) return;
            var prefix = new string(' ', indent * 2);
            var el = box.SourceNode as Element;
            var label = el?.GetAttribute("data-label") ?? el?.TagName ?? box.SourceNode?.NodeName ?? "?";
            var g = box.Geometry;
            if (g != null)
            {
                _output.WriteLine($"{prefix}{label}: Content={RectStr(g.ContentBox)} Border={RectStr(g.BorderBox)} Margin={RectStr(g.MarginBox)} Pad={g.Padding} Mar={g.Margin}");
            }
            else
            {
                _output.WriteLine($"{prefix}{label}: NO GEOMETRY");
            }
            foreach (var child in box.Children)
                DumpBoxTree(child, indent + 1);
        }

        private string RectStr(SKRect r) => $"[L={r.Left:F1} R={r.Right:F1} W={r.Width:F1}]";

        private void AssertNoHorizontalOverlap(LayoutBox containerBox, string context = "")
        {
            var children = containerBox.Children;
            for (int i = 0; i < children.Count; i++)
            {
                var gi = children[i].Geometry;
                if (gi == null) continue;

                for (int j = i + 1; j < children.Count; j++)
                {
                    var gj = children[j].Geometry;
                    if (gj == null) continue;

                    bool overlaps = gi.MarginBox.Left < gj.MarginBox.Right &&
                                    gj.MarginBox.Left < gi.MarginBox.Right;

                    if (overlaps)
                    {
                        _output.WriteLine($"OVERLAP {context}: Child[{i}] Margin={RectStr(gi.MarginBox)} vs Child[{j}] Margin={RectStr(gj.MarginBox)}");
                    }

                    Assert.False(overlaps,
                        $"{context} Child[{i}] (R={gi.MarginBox.Right:F1}) overlaps Child[{j}] (L={gj.MarginBox.Left:F1})");
                }
            }
        }

        // ======================== BASIC FLEX ROW TESTS ========================

        [Fact]
        public void FlexRow_FixedWidthChildren_NoOverlap()
        {
            var container = new Element("div");
            for (int i = 0; i < 4; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"item{i}");
                container.AppendChild(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed { Display = "flex", FlexDirection = "row", Width = 1200, Height = 50 }
            };
            foreach (var child in container.Children)
                styles[child] = new CssComputed { Display = "block", Width = 100, Height = 40 };

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== FlexRow_FixedWidthChildren ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            Assert.NotNull(containerBox);
            AssertNoHorizontalOverlap(containerBox, "FixedWidth");
        }

        [Fact]
        public void FlexRow_ChildrenWithMargins_NoOverlap()
        {
            var container = new Element("div");
            for (int i = 0; i < 4; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"item{i}");
                container.AppendChild(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed { Display = "flex", FlexDirection = "row", Width = 1200, Height = 50 }
            };
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed
                {
                    Display = "block",
                    Width = 80,
                    Height = 40,
                    Margin = new Thickness(10, 0, 10, 0)
                };
            }

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== FlexRow_ChildrenWithMargins ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "WithMargins");

            // Verify margins are respected: each item = 80+10+10 = 100px total
            var firstChild = containerBox.Children[0];
            Assert.True(firstChild.Geometry.MarginBox.Width >= 99,
                $"Item with 80px + 10px margins should be ~100px, got {firstChild.Geometry.MarginBox.Width}");
        }

        [Fact]
        public void FlexRow_ChildrenWithPadding_NoOverlap()
        {
            var container = new Element("div");
            for (int i = 0; i < 4; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"item{i}");
                container.AppendChild(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed { Display = "flex", FlexDirection = "row", Width = 1200, Height = 50 }
            };
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed
                {
                    Display = "block",
                    Width = 80,
                    Height = 40,
                    Padding = new Thickness(10, 5, 10, 5)
                };
            }

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== FlexRow_ChildrenWithPadding ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "WithPadding");
        }

        [Fact]
        public void FlexRow_ChildrenWithPaddingAndMargins_NoOverlap()
        {
            var container = new Element("div");
            for (int i = 0; i < 4; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"item{i}");
                container.AppendChild(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed { Display = "flex", FlexDirection = "row", Width = 1200, Height = 50 }
            };
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed
                {
                    Display = "block",
                    Width = 60,
                    Height = 30,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(6, 0, 6, 0)
                };
            }

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== FlexRow_PaddingAndMargins ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "PaddingAndMargins");
        }

        // ======================== JUSTIFY-CONTENT TESTS ========================

        [Theory]
        [InlineData("flex-start")]
        [InlineData("flex-end")]
        [InlineData("center")]
        [InlineData("space-between")]
        [InlineData("space-around")]
        [InlineData("space-evenly")]
        public void FlexRow_JustifyContent_NoOverlap(string justify)
        {
            var container = new Element("div");
            for (int i = 0; i < 5; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"item{i}");
                container.AppendChild(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    JustifyContent = justify,
                    Width = 1200,
                    Height = 50
                }
            };
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed
                {
                    Display = "block",
                    Width = 80,
                    Height = 30,
                    Margin = new Thickness(8, 0, 8, 0),
                    Padding = new Thickness(4, 2, 4, 2)
                };
            }

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine($"=== JustifyContent={justify} ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, $"justify={justify}");
        }

        // ======================== GOOGLE NAV BAR SIMULATION ========================

        [Fact]
        public void GoogleNavBar_FlexEnd_ItemsWithMarginsAndPadding_NoOverlap()
        {
            // Simulates Google's top-right nav structure
            var container = new Element("div");

            var gmail = new Element("a"); gmail.SetAttribute("data-label", "Gmail");
            var images = new Element("a"); images.SetAttribute("data-label", "Images");
            var apps = new Element("div"); apps.SetAttribute("data-label", "AppsIcon");
            var profile = new Element("div"); profile.SetAttribute("data-label", "Profile");
            var signin = new Element("a"); signin.SetAttribute("data-label", "SignIn");

            container.AppendChild(gmail);
            container.AppendChild(images);
            container.AppendChild(apps);
            container.AppendChild(profile);
            container.AppendChild(signin);

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    JustifyContent = "flex-end",
                    AlignItems = "center",
                    Width = 1200,
                    Height = 48
                },
                [gmail] = new CssComputed
                {
                    Display = "block",
                    Width = 40,
                    Height = 20,
                    Padding = new Thickness(8, 10, 8, 10),
                    Margin = new Thickness(0, 0, 4, 0)
                },
                [images] = new CssComputed
                {
                    Display = "block",
                    Width = 50,
                    Height = 20,
                    Padding = new Thickness(8, 10, 8, 10),
                    Margin = new Thickness(0, 0, 4, 0)
                },
                [apps] = new CssComputed
                {
                    Display = "block",
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(8, 8, 8, 8),
                    Margin = new Thickness(4, 0, 4, 0)
                },
                [profile] = new CssComputed
                {
                    Display = "block",
                    Width = 32,
                    Height = 32,
                    Padding = new Thickness(4, 4, 4, 4),
                    Margin = new Thickness(4, 0, 8, 0)
                },
                [signin] = new CssComputed
                {
                    Display = "block",
                    Width = 60,
                    Height = 28,
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(8, 0, 8, 0),
                    BorderThickness = new Thickness(1, 1, 1, 1)
                }
            };

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== GoogleNavBar ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "GoogleNavBar");

            // Verify items are pushed to the right (flex-end)
            var gmailBox = FindBox(rootBox, gmail);
            Assert.NotNull(gmailBox?.Geometry);
            Assert.True(gmailBox.Geometry.MarginBox.Left > 500,
                $"With flex-end in 1200px container, items should be on right. Gmail at {gmailBox.Geometry.MarginBox.Left}");
        }

        [Fact]
        public void GoogleNavBar_SpaceBetween_NoOverlap()
        {
            var container = new Element("div");
            var items = new List<Element>();
            for (int i = 0; i < 5; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"NavItem{i}");
                container.AppendChild(child);
                items.Add(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    JustifyContent = "space-between",
                    AlignItems = "center",
                    Width = 1200,
                    Height = 48
                }
            };
            foreach (var child in items)
            {
                styles[child] = new CssComputed
                {
                    Display = "block",
                    Width = 80,
                    Height = 30,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(4, 0, 4, 0)
                };
            }

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== NavBar_SpaceBetween ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "SpaceBetween");

            // First item near left, last near right
            var first = FindBox(rootBox, items[0]);
            var last = FindBox(rootBox, items[4]);
            Assert.True(first.Geometry.MarginBox.Left < 50,
                $"First item should be near left, got L={first.Geometry.MarginBox.Left}");
            Assert.True(last.Geometry.MarginBox.Right > 1100,
                $"Last item should be near right, got R={last.Geometry.MarginBox.Right}");
        }

        // ======================== BORDER-BOX SIZING TESTS ========================

        [Fact]
        public void FlexRow_BorderBox_WidthIncludesPaddingAndBorder_NoOverlap()
        {
            var container = new Element("div");
            for (int i = 0; i < 4; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"bbox{i}");
                container.AppendChild(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed { Display = "flex", FlexDirection = "row", Width = 1200, Height = 50 }
            };
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed
                {
                    Display = "block",
                    Width = 100,
                    Height = 40,
                    BoxSizing = "border-box",
                    Padding = new Thickness(10, 5, 10, 5),
                    BorderThickness = new Thickness(1, 1, 1, 1)
                };
            }

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== FlexRow_BorderBox ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "BorderBox");

            // With border-box, BorderBox.Width should be ~100
            var firstChild = containerBox.Children[0];
            Assert.InRange(firstChild.Geometry.BorderBox.Width, 98, 102);
        }

        [Fact]
        public void FlexRow_ContentBox_WidthExcludesPaddingAndBorder_NoOverlap()
        {
            var container = new Element("div");
            for (int i = 0; i < 4; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"cbox{i}");
                container.AppendChild(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed { Display = "flex", FlexDirection = "row", Width = 1200, Height = 50 }
            };
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed
                {
                    Display = "block",
                    Width = 100,
                    Height = 40,
                    Padding = new Thickness(10, 5, 10, 5),
                    BorderThickness = new Thickness(1, 1, 1, 1)
                };
            }

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== FlexRow_ContentBox ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "ContentBox");

            // With content-box: Content=100, Border=100+20+2=122
            var firstChild = containerBox.Children[0];
            _output.WriteLine($"ContentBox child[0]: ContentBox.W={firstChild.Geometry.ContentBox.Width} BorderBox.W={firstChild.Geometry.BorderBox.Width}");
            Assert.InRange(firstChild.Geometry.ContentBox.Width, 98, 102);
            Assert.InRange(firstChild.Geometry.BorderBox.Width, 120, 124);
        }

        // ======================== INLINE-FLEX TESTS ========================

        [Fact]
        public void InlineFlex_ShrinkToFit_DoesNotConsumeFullWidth()
        {
            var outer = new Element("div");
            var inlineFlex = new Element("div");
            inlineFlex.SetAttribute("data-label", "inline-flex-container");

            var child1 = new Element("span");
            child1.SetAttribute("data-label", "c1");
            var child2 = new Element("span");
            child2.SetAttribute("data-label", "c2");
            inlineFlex.AppendChild(child1);
            inlineFlex.AppendChild(child2);
            outer.AppendChild(inlineFlex);

            var styles = new Dictionary<Node, CssComputed>
            {
                [outer] = new CssComputed { Display = "block", Width = 1200, Height = 50 },
                [inlineFlex] = new CssComputed { Display = "inline-flex", FlexDirection = "row", Height = 30 },
                [child1] = new CssComputed { Display = "block", Width = 50, Height = 20 },
                [child2] = new CssComputed { Display = "block", Width = 60, Height = 20 }
            };

            var rootBox = LayoutRoot(outer, styles);
            _output.WriteLine("=== InlineFlex_ShrinkToFit ===");
            DumpBoxTree(rootBox);

            var ifBox = FindBox(rootBox, inlineFlex);
            Assert.NotNull(ifBox?.Geometry);
            _output.WriteLine($"inline-flex ContentBox.Width = {ifBox.Geometry.ContentBox.Width}");

            // inline-flex should be ~110px (50+60), NOT 1200px
            Assert.True(ifBox.Geometry.ContentBox.Width < 300,
                $"inline-flex should shrink to content (~110px), got {ifBox.Geometry.ContentBox.Width}");
        }

        // ======================== GAP TESTS ========================

        [Fact]
        public void FlexRow_WithGap_ItemsSpacedCorrectly()
        {
            var container = new Element("div");
            for (int i = 0; i < 4; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"gap{i}");
                container.AppendChild(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    Width = 1200,
                    Height = 50,
                    Gap = 16
                }
            };
            foreach (var child in container.Children)
                styles[child] = new CssComputed { Display = "block", Width = 100, Height = 40 };

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== FlexRow_WithGap ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "WithGap");

            // Verify gap applied
            var c0 = containerBox.Children[0];
            var c1 = containerBox.Children[1];
            float actualGap = c1.Geometry.MarginBox.Left - c0.Geometry.MarginBox.Right;
            _output.WriteLine($"Gap: {actualGap}px (expected 16)");
            Assert.InRange(actualGap, 14, 18);
        }

        // ======================== OVERFLOW / SHRINK TESTS ========================

        [Fact]
        public void FlexRow_ItemsOverflow_FlexShrink_NoOverlap()
        {
            // 5 items of 300px in 1200px = needs shrink
            var container = new Element("div");
            for (int i = 0; i < 5; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"shrink{i}");
                container.AppendChild(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed { Display = "flex", FlexDirection = "row", Width = 1200, Height = 50 }
            };
            foreach (var child in container.Children)
            {
                styles[child] = new CssComputed
                {
                    Display = "block",
                    Width = 300,
                    Height = 40,
                    Margin = new Thickness(5, 0, 5, 0)
                };
            }

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== FlexRow_Shrink ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "FlexShrink");
        }

        // ======================== TEXT CONTENT TESTS ========================

        [Fact]
        public void FlexRow_TextChildren_NoOverlap()
        {
            var container = new Element("div");

            var link1 = new Element("a");
            link1.AppendChild(new Text("Gmail"));
            link1.SetAttribute("data-label", "Gmail");

            var link2 = new Element("a");
            link2.AppendChild(new Text("Images"));
            link2.SetAttribute("data-label", "Images");

            var link3 = new Element("a");
            link3.AppendChild(new Text("Sign in"));
            link3.SetAttribute("data-label", "SignIn");

            container.AppendChild(link1);
            container.AppendChild(link2);
            container.AppendChild(link3);

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    JustifyContent = "flex-end",
                    AlignItems = "center",
                    Width = 1200,
                    Height = 48
                },
                [link1] = new CssComputed
                {
                    Display = "block",
                    Padding = new Thickness(8, 10, 8, 10),
                    Margin = new Thickness(0, 0, 4, 0),
                    FontSize = 14
                },
                [link2] = new CssComputed
                {
                    Display = "block",
                    Padding = new Thickness(8, 10, 8, 10),
                    Margin = new Thickness(0, 0, 4, 0),
                    FontSize = 14
                },
                [link3] = new CssComputed
                {
                    Display = "block",
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(8, 0, 8, 0),
                    FontSize = 14,
                    BorderThickness = new Thickness(1, 1, 1, 1)
                }
            };

            // Add text node styles
            foreach (var link in new[] { link1, link2, link3 })
            {
                foreach (var child in link.ChildNodes)
                {
                    if (child is Text)
                        styles[child] = new CssComputed { FontSize = 14 };
                }
            }

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== FlexRow_TextChildren ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "TextChildren");
        }

        [Fact]
        public void FlexRow_GoogleTopRightCluster_NoOverlapBetweenAppsAndSignIn()
        {
            var container = new Element("div");

            var gmail = new Element("a");
            gmail.SetAttribute("data-label", "Gmail");
            gmail.AppendChild(new Text("Gmail"));

            var images = new Element("a");
            images.SetAttribute("data-label", "Images");
            images.AppendChild(new Text("Images"));

            var apps = new Element("a");
            apps.SetAttribute("data-label", "Apps");

            var signIn = new Element("a");
            signIn.SetAttribute("data-label", "SignIn");
            signIn.AppendChild(new Text("Sign in"));

            container.AppendChild(gmail);
            container.AppendChild(images);
            container.AppendChild(apps);
            container.AppendChild(signIn);

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed
                {
                    Display = "flex",
                    FlexDirection = "row",
                    JustifyContent = "flex-end",
                    AlignItems = "center",
                    Width = 1200,
                    Height = 48
                },
                [gmail] = new CssComputed
                {
                    Display = "block",
                    Padding = new Thickness(8, 10, 8, 10),
                    Margin = new Thickness(0, 0, 8, 0),
                    FontSize = 14
                },
                [images] = new CssComputed
                {
                    Display = "block",
                    Padding = new Thickness(8, 10, 8, 10),
                    Margin = new Thickness(0, 0, 8, 0),
                    FontSize = 14
                },
                [apps] = new CssComputed
                {
                    Display = "block",
                    Width = 40,
                    Height = 40,
                    Margin = new Thickness(0, 0, 8, 0)
                },
                [signIn] = new CssComputed
                {
                    Display = "inline-block",
                    BoxSizing = "border-box",
                    MinWidth = 85,
                    MinHeight = 40,
                    Padding = new Thickness(10, 12, 10, 12),
                    Margin = new Thickness(0, 0, 8, 0),
                    FontSize = 14
                }
            };

            foreach (var link in new[] { gmail, images, signIn })
            {
                foreach (var child in link.ChildNodes)
                {
                    if (child is Text)
                    {
                        styles[child] = new CssComputed { FontSize = 14 };
                    }
                }
            }

            var rootBox = LayoutRoot(container, styles);
            var containerBox = FindBox(rootBox, container);

            AssertNoHorizontalOverlap(containerBox, "GoogleTopRightCluster");

            var appsBox = FindBox(containerBox, apps);
            var signInBox = FindBox(containerBox, signIn);
            Assert.True(
                appsBox.Geometry.MarginBox.Right <= signInBox.Geometry.MarginBox.Left + 0.5f,
                $"Expected Apps and Sign in not to overlap. apps={appsBox.Geometry.MarginBox} signIn={signInBox.Geometry.MarginBox}");
        }

        // ======================== EDGE CASES ========================

        [Fact]
        public void FlexRow_ZeroGap_NoMargins_ItemsTouchButDontOverlap()
        {
            var container = new Element("div");
            for (int i = 0; i < 6; i++)
            {
                var child = new Element("div");
                child.SetAttribute("data-label", $"flush{i}");
                container.AppendChild(child);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed { Display = "flex", FlexDirection = "row", Width = 600, Height = 50 }
            };
            foreach (var child in container.Children)
                styles[child] = new CssComputed { Display = "block", Width = 100, Height = 40 };

            var rootBox = LayoutRoot(container, styles, 600, 800);
            _output.WriteLine("=== ZeroGap_NoMargins ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "ZeroGap");

            // Items should be exactly flush
            for (int i = 0; i < 5; i++)
            {
                var boxI = containerBox.Children[i];
                var boxNext = containerBox.Children[i + 1];
                float diff = Math.Abs(boxI.Geometry.MarginBox.Right - boxNext.Geometry.MarginBox.Left);
                _output.WriteLine($"  Gap between {i} and {i+1}: {diff}");
                Assert.True(diff < 2f, $"Items {i} and {i + 1} should be flush: gap={diff}");
            }
        }

        [Fact]
        public void FlexRow_MixedExplicitAndAutoWidth_NoOverlap()
        {
            var container = new Element("div");

            var explicit1 = new Element("div"); explicit1.SetAttribute("data-label", "explicit100");
            var auto1 = new Element("div"); auto1.SetAttribute("data-label", "auto");
            var explicit2 = new Element("div"); explicit2.SetAttribute("data-label", "explicit150");

            var autoChild = new Element("span");
            autoChild.SetAttribute("data-label", "autoChild");
            auto1.AppendChild(autoChild);

            container.AppendChild(explicit1);
            container.AppendChild(auto1);
            container.AppendChild(explicit2);

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed { Display = "flex", FlexDirection = "row", Width = 1200, Height = 50 },
                [explicit1] = new CssComputed { Display = "block", Width = 100, Height = 40 },
                [auto1] = new CssComputed { Display = "block", Height = 40, Padding = new Thickness(8, 4, 8, 4) },
                [autoChild] = new CssComputed { Display = "block", Width = 60, Height = 20 },
                [explicit2] = new CssComputed { Display = "block", Width = 150, Height = 40 }
            };

            var rootBox = LayoutRoot(container, styles);
            _output.WriteLine("=== MixedWidths ===");
            DumpBoxTree(rootBox);

            var containerBox = FindBox(rootBox, container);
            AssertNoHorizontalOverlap(containerBox, "MixedWidths");
        }

        // ======================== NESTED FLEX TESTS ========================

        [Fact]
        public void NestedFlex_InnerFlexEnd_OuterFlexEnd_NoOverlap()
        {
            var outer = new Element("div");
            var leftGroup = new Element("div"); leftGroup.SetAttribute("data-label", "leftGroup");
            var rightGroup = new Element("div"); rightGroup.SetAttribute("data-label", "rightGroup");

            outer.AppendChild(leftGroup);
            outer.AppendChild(rightGroup);

            var r1 = new Element("div"); r1.SetAttribute("data-label", "r1");
            var r2 = new Element("div"); r2.SetAttribute("data-label", "r2");
            var r3 = new Element("div"); r3.SetAttribute("data-label", "r3");
            rightGroup.AppendChild(r1);
            rightGroup.AppendChild(r2);
            rightGroup.AppendChild(r3);

            var styles = new Dictionary<Node, CssComputed>
            {
                [outer] = new CssComputed { Display = "flex", FlexDirection = "row", JustifyContent = "space-between", Width = 1200, Height = 48 },
                [leftGroup] = new CssComputed { Display = "flex", FlexDirection = "row", Width = 200, Height = 40 },
                [rightGroup] = new CssComputed { Display = "flex", FlexDirection = "row", JustifyContent = "flex-end", AlignItems = "center", Width = 400, Height = 48 },
                [r1] = new CssComputed { Display = "block", Width = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4, 2, 4, 2) },
                [r2] = new CssComputed { Display = "block", Width = 40, Height = 40, Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(8, 8, 8, 8) },
                [r3] = new CssComputed { Display = "block", Width = 70, Height = 32, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) }
            };

            var rootBox = LayoutRoot(outer, styles);
            _output.WriteLine("=== NestedFlex ===");
            DumpBoxTree(rootBox);

            var rightGroupBox = FindBox(rootBox, rightGroup);
            AssertNoHorizontalOverlap(rightGroupBox, "RightGroup");

            var outerBox = FindBox(rootBox, outer);
            AssertNoHorizontalOverlap(outerBox, "OuterLevel");
        }
    }
}
