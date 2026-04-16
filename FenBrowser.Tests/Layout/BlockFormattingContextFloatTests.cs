using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class BlockFormattingContextFloatTests
    {
        [Fact]
        public void MultipleFloats_LeftAndRight_ShareSameRowWithCorrectOffsets()
        {
            var root = new Element("div");
            var leftA = new Element("div");
            var leftB = new Element("div");
            var rightA = new Element("div");

            root.AppendChild(leftA);
            root.AppendChild(leftB);
            root.AppendChild(rightA);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 300, Height = 120 },
                [leftA] = new CssComputed { Display = "block", Float = "left", Width = 90, Height = 20 },
                [leftB] = new CssComputed { Display = "block", Float = "left", Width = 90, Height = 20 },
                [rightA] = new CssComputed { Display = "block", Float = "right", Width = 90, Height = 20 }
            };

            var rootBox = LayoutRoot(root, styles, 300, 120);
            var leftABox = FindBox(rootBox, leftA);
            var leftBBox = FindBox(rootBox, leftB);
            var rightABox = FindBox(rootBox, rightA);

            Assert.NotNull(leftABox);
            Assert.NotNull(leftBBox);
            Assert.NotNull(rightABox);

            Assert.Equal(0f, leftABox.Geometry.MarginBox.Left, 1);
            Assert.Equal(90f, leftBBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(210f, rightABox.Geometry.MarginBox.Left, 1);

            Assert.Equal(0f, leftABox.Geometry.MarginBox.Top, 1);
            Assert.Equal(0f, leftBBox.Geometry.MarginBox.Top, 1);
            Assert.Equal(0f, rightABox.Geometry.MarginBox.Top, 1);
        }

        [Fact]
        public void FloatAutoWidth_UsesShrinkToFitContent()
        {
            var root = new Element("div");
            var floated = new Element("div");
            floated.AppendChild(new Text("Menu"));
            root.AppendChild(floated);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 300, Height = 120 },
                [floated] = new CssComputed { Display = "block", Float = "left" }
            };

            var rootBox = LayoutRoot(root, styles, 300, 120);
            var floatedBox = FindBox(rootBox, floated);

            Assert.NotNull(floatedBox);
            Assert.True(floatedBox.Geometry.MarginBox.Width > 0f);
            Assert.True(floatedBox.Geometry.MarginBox.Width < 300f);
        }

        [Fact]
        public void BlockWithExplicitWidth_MovesBelowFloatWhenBandTooNarrow()
        {
            var root = new Element("div");
            var floated = new Element("div");
            var block = new Element("div");

            root.AppendChild(floated);
            root.AppendChild(block);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 200, Height = 180 },
                [floated] = new CssComputed { Display = "block", Float = "left", Width = 120, Height = 40 },
                [block] = new CssComputed { Display = "block", Width = 150, Height = 20 }
            };

            var rootBox = LayoutRoot(root, styles, 200, 180);
            var floatBox = FindBox(rootBox, floated);
            var blockBox = FindBox(rootBox, block);

            Assert.NotNull(floatBox);
            Assert.NotNull(blockBox);

            Assert.True(blockBox.Geometry.MarginBox.Top >= floatBox.Geometry.MarginBox.Bottom - 0.5f);
            Assert.Equal(0f, blockBox.Geometry.MarginBox.Left, 1);
        }

        [Fact]
        public void AutoWidthBlock_ReflowsIntoFloatReducedBand()
        {
            var root = new Element("div");
            var floated = new Element("div");
            var paragraph = new Element("p");
            paragraph.AppendChild(new Text("This paragraph must wrap beside the float without overflowing the containing block width."));

            root.AppendChild(floated);
            root.AppendChild(paragraph);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 300, Height = 240 },
                [floated] = new CssComputed { Display = "block", Float = "left", Width = 120, Height = 80 },
                [paragraph] = new CssComputed { Display = "block" }
            };

            var rootBox = LayoutRoot(root, styles, 300, 240);
            var floatBox = FindBox(rootBox, floated);
            var paragraphBox = FindBox(rootBox, paragraph);

            Assert.NotNull(floatBox);
            Assert.NotNull(paragraphBox);

            Assert.Equal(120f, paragraphBox.Geometry.MarginBox.Left, 1);
            Assert.True(paragraphBox.Geometry.MarginBox.Right <= 300.5f);
            Assert.True(paragraphBox.Geometry.MarginBox.Width <= 180.5f);
        }

        [Fact]
        public void FloatReplacedElement_ExplicitZeroSize_DoesNotFallbackToDefaultDimensions()
        {
            var root = new Element("div");
            var iframe = new Element("iframe");
            root.AppendChild(iframe);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 300, Height = 120 },
                [iframe] = new CssComputed { Display = "inline", Float = "left", Width = 0, Height = 0 }
            };

            var rootBox = LayoutRoot(root, styles, 300, 120);
            var iframeBox = FindBox(rootBox, iframe);

            Assert.NotNull(iframeBox);
            Assert.Equal(0f, iframeBox.Geometry.MarginBox.Width, 1);
            Assert.Equal(0f, iframeBox.Geometry.MarginBox.Height, 1);
        }

        [Fact]
        public void FloatManager_ClearanceY_AllowsNegativeDeltaWhenFloatsAreAboveMarginEdge()
        {
            var floats = new FloatManager();
            floats.AddFloat(new SKRect(0, 10, 50, 30), isLeft: true);

            // If the current margin edge already sits below float bottoms,
            // clearance resolves to the float bottom (30) and can be above
            // or below the current edge depending on collapse math.
            var clearanceY = floats.GetClearanceY("both", currentY: 60);

            Assert.Equal(30f, clearanceY, 1);
            Assert.True(clearanceY < 60f, "Expected clearance Y to allow a negative delta relative to currentY.");
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
