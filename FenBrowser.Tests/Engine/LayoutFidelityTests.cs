using System.Collections.Generic;
using Xunit;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using FenBrowser.Core;

namespace FenBrowser.Tests.Engine
{
    public class LayoutFidelityTests
    {
        [Fact]
        public void BoxTreeBuilder_MixedBlockAndInline_WrapsInlineInAnonymousBlock()
        {
            // <div>
            //   Text
            //   <div>Inner</div>
            // </div>
            
            var root = new Element("div");
            var text = new Text("Text");
            var inner = new Element("div");
            root.AppendChild(text);
            root.AppendChild(inner);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block" },
                [inner] = new CssComputed { Display = "block" }
            };

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);

            Assert.IsType<BlockBox>(rootBox);
            Assert.Equal(2, rootBox.Children.Count);
            Assert.IsType<AnonymousBlockBox>(rootBox.Children[0]);
            Assert.IsType<BlockBox>(rootBox.Children[1]);
            Assert.Same(inner, rootBox.Children[1].SourceNode);
            Assert.IsType<TextLayoutBox>(rootBox.Children[0].Children[0]);
        }

        [Fact]
        public void BoxTreeBuilder_BlockInInline_SplitsInline()
        {
            // <div>
            //   <span>
            //     Start
            //     <div>Middle</div>
            //     End
            //   </span>
            // </div>

            var div = new Element("div");
            var span = new Element("span");
            var start = new Text("Start");
            var middle = new Element("div");
            var end = new Text("End");

            div.AppendChild(span);
            span.AppendChild(start);
            span.AppendChild(middle);
            span.AppendChild(end);

            var styles = new Dictionary<Node, CssComputed>
            {
                [div] = new CssComputed { Display = "block" },
                [span] = new CssComputed { Display = "inline" },
                [middle] = new CssComputed { Display = "block" }
            };

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(div);

            // div is block, it contains only 1 child span which is inline.
            // span splits into [Inline, Block, Inline]
            // div then sees [Inline, Block, Inline] as children.
            // div fixup wraps inlines: [AnonBlock(Inline), Block, AnonBlock(Inline)]

            Assert.IsType<BlockBox>(rootBox);
            Assert.Equal(3, rootBox.Children.Count);
            
            Assert.IsType<AnonymousBlockBox>(rootBox.Children[0]);
            Assert.IsType<InlineBox>(rootBox.Children[0].Children[0]);
            Assert.Same(span, rootBox.Children[0].Children[0].SourceNode);

            Assert.IsType<BlockBox>(rootBox.Children[1]);
            Assert.Same(middle, rootBox.Children[1].SourceNode);

            Assert.IsType<AnonymousBlockBox>(rootBox.Children[2]);
            Assert.IsType<InlineBox>(rootBox.Children[2].Children[0]);
            Assert.Same(span, rootBox.Children[2].Children[0].SourceNode);
        }

        [Fact]
        public void BoxTreeBuilder_DisplayContents_FlattensChildren()
        {
            // <div style="display: block">
            //   <div style="display: contents">
            //     <span>Child</span>
            //   </div>
            // </div>

            var root = new Element("div");
            var contents = new Element("div");
            var span = new Element("span");
            root.AppendChild(contents);
            contents.AppendChild(span);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block" },
                [contents] = new CssComputed { Display = "contents" },
                [span] = new CssComputed { Display = "inline" }
            };

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);

            Assert.IsType<BlockBox>(rootBox);
            Assert.Single(rootBox.Children);
            Assert.IsType<InlineBox>(rootBox.Children[0]);
            Assert.Same(span, rootBox.Children[0].SourceNode);
        }

        [Fact]
        public void RelativePositionedFlexItem_ShiftsSubtreeWithoutChangingSiblingFlow()
        {
            var root = new Element("div");
            var nudged = new Element("div");
            var sibling = new Element("div");
            var text = new Text("AI");

            nudged.AppendChild(text);
            root.AppendChild(nudged);
            root.AppendChild(sibling);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "flex", Width = 160, Height = 40 },
                [nudged] = new CssComputed { Display = "block", Width = 36, Height = 20, Position = "relative", Left = 6, Top = 4 },
                [text] = new CssComputed { Display = "inline" },
                [sibling] = new CssComputed { Display = "block", Width = 36, Height = 20 }
            };

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);

            var state = new LayoutState(
                new SKSize(160, 40),
                160,
                40,
                160,
                40);

            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var nudgedBox = FindBox(rootBox, nudged);
            var siblingBox = FindBox(rootBox, sibling);
            var textBox = FindBox(rootBox, text);

            Assert.NotNull(nudgedBox);
            Assert.NotNull(siblingBox);
            Assert.NotNull(textBox);

            Assert.Equal(6f, nudgedBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(4f, nudgedBox.Geometry.MarginBox.Top, 1);
            Assert.Equal(36f, siblingBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(0f, siblingBox.Geometry.MarginBox.Top, 1);
            Assert.True(textBox.Geometry.MarginBox.Left >= nudgedBox.Geometry.MarginBox.Left);
            Assert.True(textBox.Geometry.MarginBox.Top >= nudgedBox.Geometry.MarginBox.Top);
        }
        [Fact]
        public void InlineLayout_EmptyLine_RespectsStrut()
        {
            // <div style="font-size: 20px; line-height: 30px">
            //   <span> </span>
            // </div>
            
            var div = new Element("div");
            var span = new Element("span");
            span.AppendChild(new Text(" "));
            div.AppendChild(span);

            var styles = new Dictionary<Node, CssComputed>
            {
                [div] = new CssComputed { FontSize = 20, LineHeight = 30 },
                [span] = new CssComputed { FontSize = 10, Display = "inline" }
            };

            // We test the InlineLayoutComputer directly
            var result = FenBrowser.FenEngine.Layout.InlineLayoutComputer.Compute(
                div,
                new SkiaSharp.SKSize(800, 600),
                n => styles.ContainsKey(n) ? styles[n] : new CssComputed(),
                (e, s, d) => new FenBrowser.FenEngine.Layout.LayoutMetrics(),
                0
            );

            // With the space, a line box is created. 
            // It should have height 30 (container's line-height) even though span's fontSize is 10.
            Assert.True(result.Metrics.ContentHeight >= 30, $"Height was {result.Metrics.ContentHeight}, expected >= 30");
        }
        [Fact]
        public void InlineLayout_MixedFonts_AlignsBaselinesToStrut()
        {
            // <div style="font-size: 20px; line-height: 40px"> <!-- Strut Lh=40, Ascent~32 -->
            //   <span style="font-size: 10px; line-height: 12px">Small</span> <!-- Ascent~10 -->
            // </div>
            
            var div = new Element("div");
            var span = new Element("span");
            span.AppendChild(new Text("Small"));
            div.AppendChild(span);

            var styles = new Dictionary<Node, CssComputed>
            {
                [div] = new CssComputed { FontSize = 20, LineHeight = 40 },
                [span] = new CssComputed { FontSize = 10, LineHeight = 12, Display = "inline" }
            };

            var result = FenBrowser.FenEngine.Layout.InlineLayoutComputer.Compute(
                div,
                new SkiaSharp.SKSize(800, 600),
                n => styles.ContainsKey(n) ? styles[n] : new CssComputed(),
                (e, s, d) => new FenBrowser.FenEngine.Layout.LayoutMetrics(),
                0
            );

            // Container line-height is 40. The small text should be vertically centered 
            // relative to the 40px line, BUT its baseline must match the strut's baseline.
            
            // The text line's Origin.Y should be at (currentY + maxAscent - item.Ascent)
            // If currentY=0, maxAscent should be the strut's ascent (~32 if lh=40 and centered)
            // If item.Ascent is ~10, the origin.Y should be ~22.
            
            Assert.True(result.Metrics.ContentHeight >= 40);
            
            var firstTextNode = span.ChildNodes[0];
            var lines = result.TextLines[firstTextNode];
            var line = lines[0];
            
            // Origin.Y should be positive and pushed down by the strut's baseline
            Assert.True(line.Origin.Y > 8, $"Baseline Y was {line.Origin.Y}, expected it to be pushed down by the strut (> 8).");
        }
        [Fact]
        public void BlockLayout_MarginCollapsing_Siblings()
        {
            // <div>
            //   <div style="margin-bottom: 20px">A</div>
            //   <div style="margin-top: 10px">B</div>
            // </div>
            // Gap should be 20px, total height = A.height + 20 + B.height
            
            var root = new Element("div");
            var a = new Element("div");
            var b = new Element("div");
            root.AppendChild(a);
            root.AppendChild(b);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 100 },
                [a] = new CssComputed { Display = "block", Height = 50, Margin = new Thickness(0, 0, 0, 20) },
                [b] = new CssComputed { Display = "block", Height = 50, Margin = new Thickness(0, 10, 0, 0) }
            };

            var computer = new FenBrowser.FenEngine.Layout.MinimalLayoutComputer(styles, 800, 600);
            var result = computer.Measure(root, new SKSize(800, 600));

            // Height = 50 (a) + 20 (collapsed margin) + 50 (b) = 120
            // Note: root might collapse with 'a' and 'b' if no padding/border.
            // In our case, root has auto height, so it will collapse with child margins.
            // bubbled MT = 0. bubbled MB = 0.
            Assert.Equal(120, result.ContentHeight);
        }

        [Fact]
        public void BlockLayout_FloatClearance()
        {
            var document = new Document();
            var html = document.CreateElement("html");
            var body = document.CreateElement("body");
            document.AppendChild(html);
            html.AppendChild(body);

            // Container
            var container = document.CreateElement("div");
            body.AppendChild(container);

            // Float
            var floatElem = document.CreateElement("div");
            container.AppendChild(floatElem);

            // Clearing Element
            var clearElem = document.CreateElement("div");
            container.AppendChild(clearElem);

            var styles = new Dictionary<Node, CssComputed>
            {
                [html] = new CssComputed { Display = "block" },
                [body] = new CssComputed { Display = "block" },
                [container] = new CssComputed { Display = "block", Width = 500 },
                [floatElem] = new CssComputed { Display = "block", Float = "left", Width = 50, Height = 50 },
                [clearElem] = new CssComputed { Display = "block", Clear = "both", Height = 50 }
            };

            var computer = new FenBrowser.FenEngine.Layout.MinimalLayoutComputer(styles, 1920, 1080);
            computer.Measure(html, new SKSize(1920, 1080));
            computer.Arrange(html, new SKRect(0, 0, 1920, 1080));

            var floatBox = computer.GetBox(floatElem);
            var clearBox = computer.GetBox(clearElem);

            // Verify
            Assert.NotNull(floatBox);
            Assert.NotNull(clearBox);
            
            // Float should be at top
            Assert.Equal(0, floatBox.BorderBox.Top);
            
            // Clear element should be below the float (Top >= 50)
            Assert.True(clearBox.BorderBox.Top >= 50, $"Clear element Top ({clearBox.BorderBox.Top}) should be >= Float Bottom (50)");
        }
        [Fact]
        public void BlockLayout_MarginCollapsing_PositiveNegative()
        {
            // <div>
            //   <div style="margin-bottom: 20px">A</div>
            //   <div style="margin-top: -10px">B</div>
            // </div>
            // Gap should be 20 + (-10) = 10px.
            
            var root = new Element("div");
            var a = new Element("div");
            var b = new Element("div");
            root.AppendChild(a);
            root.AppendChild(b);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 100, BorderThickness = new Thickness(1) }, // prevent root collapse
                [a] = new CssComputed { Display = "block", Height = 50, Margin = new Thickness(0, 0, 0, 20) },
                [b] = new CssComputed { Display = "block", Height = 50, Margin = new Thickness(0, -10, 0, 0) }
            };

            var computer = new FenBrowser.FenEngine.Layout.MinimalLayoutComputer(styles, 800, 600);
            var result = computer.Measure(root, new SKSize(800, 600));

            // Height = 1 (border) + 50 (a) + 10 (collapsed 20 - 10) + 50 (b) + 1 (border) = 112
            Assert.Equal(112, result.ContentHeight);
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

