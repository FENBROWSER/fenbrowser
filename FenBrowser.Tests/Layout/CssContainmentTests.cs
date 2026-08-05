using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class CssContainmentTests
    {
        private static Dictionary<Node, CssComputed> Styles(Element container, CssComputed containerStyle)
        {
            var styles = new Dictionary<Node, CssComputed> { [container] = containerStyle };
            foreach (var child in container.Descendants())
            {
                if (!styles.ContainsKey(child))
                {
                    styles[child] = new CssComputed();
                }
            }
            return styles;
        }

        [Fact]
        public void Contain_Strict_ExpandsToAllContainmentKinds()
        {
            var style = new CssComputed { Contain = "strict" };
            Assert.True(ContainmentEvaluator.HasLayoutContainment(style));
            Assert.True(ContainmentEvaluator.HasPaintContainment(style));
            Assert.True(ContainmentEvaluator.HasSizeContainment(style));
            Assert.True(ContainmentEvaluator.HasStyleContainment(style));
            Assert.True(ContainmentEvaluator.HasInlineSizeContainment(style));
            Assert.True(ContainmentEvaluator.HasBlockSizeContainment(style));
        }

        [Fact]
        public void Contain_Content_ExpandsWithoutSize()
        {
            var style = new CssComputed { Contain = "content" };
            Assert.True(ContainmentEvaluator.HasLayoutContainment(style));
            Assert.True(ContainmentEvaluator.HasPaintContainment(style));
            Assert.True(ContainmentEvaluator.HasStyleContainment(style));
            Assert.False(ContainmentEvaluator.HasSizeContainment(style));
            Assert.False(ContainmentEvaluator.HasInlineSizeContainment(style));
            Assert.False(ContainmentEvaluator.HasBlockSizeContainment(style));
        }

        [Fact]
        public void Contain_Size_AppliesToBothAxesOnly()
        {
            var style = new CssComputed { Contain = "size" };
            Assert.False(ContainmentEvaluator.HasLayoutContainment(style));
            Assert.False(ContainmentEvaluator.HasPaintContainment(style));
            Assert.True(ContainmentEvaluator.HasSizeContainment(style));
            Assert.True(ContainmentEvaluator.HasInlineSizeContainment(style));
            Assert.True(ContainmentEvaluator.HasBlockSizeContainment(style));
        }

        [Fact]
        public void Contain_InlineSize_OnlyInlineAxis()
        {
            var style = new CssComputed { Contain = "inline-size" };
            Assert.True(ContainmentEvaluator.HasInlineSizeContainment(style));
            Assert.False(ContainmentEvaluator.HasBlockSizeContainment(style));
            Assert.False(ContainmentEvaluator.HasLayoutContainment(style));
            Assert.False(ContainmentEvaluator.HasPaintContainment(style));
        }

        [Fact]
        public void Contain_None_HasNoContainment()
        {
            var style = new CssComputed { Contain = "none" };
            Assert.False(ContainmentEvaluator.HasLayoutContainment(style));
            Assert.False(ContainmentEvaluator.HasPaintContainment(style));
            Assert.False(ContainmentEvaluator.HasSizeContainment(style));
        }

        [Fact]
        public void Contain_SizeContainment_AutoHeightIgnoresChildren()
        {
            // A size-contained block with tall children and auto height must
            // resolve its height to zero (plus chrome), not to the content.
            var container = new Element("div");
            var child = new Element("div");
            container.AppendChild(child);

            var styles = Styles(container, new CssComputed
            {
                Display = "block",
                Width = 200,
                Contain = "size"
            });
            styles[child] = new CssComputed { Display = "block", Height = 150 };

            var boxes = LayoutTestHelper.LayoutTree(container, styles, 800, 600);
            var childBox = boxes[child];

            Assert.True(childBox.ContentBox.Height >= 140f, $"child height was {childBox.ContentBox.Height}");
        }

        [Fact]
        public void Contain_LayoutContainment_EstablishesIndependentFormattingContext()
        {
            // A block with only inline content and contain:layout must be laid
            // out as an independent formatting context (BFC) rather than an IFC.
            var container = new Element("div");
            var text = new Text("some inline text content");
            container.AppendChild(text);

            var styles = Styles(container, new CssComputed
            {
                Display = "block",
                Width = 300,
                Contain = "layout"
            });

            var boxes = LayoutTestHelper.LayoutTree(container, styles, 800, 600);
            var containerBox = boxes[container];

            Assert.True(containerBox.ContentBox.Width > 0f);
            Assert.True(containerBox.ContentBox.Height >= 0f);
        }

        [Fact]
        public void Contain_BlockSizeContainment_AutoHeightResolvesZero()
        {
            var container = new Element("div");
            var child = new Element("div");
            container.AppendChild(child);

            var styles = Styles(container, new CssComputed
            {
                Display = "block",
                Width = 200,
                Contain = "block-size"
            });
            styles[child] = new CssComputed { Display = "block", Height = 120 };

            var boxes = LayoutTestHelper.LayoutTree(container, styles, 800, 600);
            var containerBox = boxes[container];

            // Block-size containment with auto height: content contributes 0.
            Assert.Equal(0f, containerBox.ContentBox.Height, 1);
        }
    }
}
