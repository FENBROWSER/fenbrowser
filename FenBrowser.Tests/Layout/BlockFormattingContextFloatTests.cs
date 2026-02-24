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
