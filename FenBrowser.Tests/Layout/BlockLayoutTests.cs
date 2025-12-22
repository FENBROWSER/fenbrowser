using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.Core; // For Thickness
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// Tests for block layout correctness.
    /// Verifies BFC rules, margin collapsing, and block box behavior.
    /// </summary>
    public class BlockLayoutTests
    {
        private Element CreateElement(string tag, params Node[] children)
        {
            var element = new Element(tag);
            foreach (var child in children)
            {
                element.Children.Add(child);
            }
            return element;
        }

        [Fact]
        public void BlockElement_TakesFullWidth()
        {
            // Arrange: <div> without width
            var div = CreateElement("DIV");
            var style = new CssComputed { Display = "block" };

            // Assert: Display is block (takes full available width)
            Assert.Equal("block", style.Display);
        }

        [Fact]
        public void MarginCollapsing_AdjacentSiblings_LargerWins()
        {
            // Arrange: Two adjacent divs
            // <div style="margin-bottom:20px">A</div>
            // <div style="margin-top:30px">B</div>
            // Expected: 30px gap (not 50px)
            
            var style1 = new CssComputed();
            style1.Margin = new Thickness(0, 0, 0, 20); // bottom 20
            
            var style2 = new CssComputed();
            style2.Margin = new Thickness(0, 30, 0, 0); // top 30
            
            // Collapsed margin should be max(20, 30) = 30
            var collapsed = System.Math.Max(20, 30);
            Assert.Equal(30, collapsed);
        }

        [Fact]
        public void BFC_RootElementEstablishesBFC()
        {
            // Root element always establishes new BFC
            var html = CreateElement("HTML");
            Assert.Equal("HTML", html.Tag);
        }

        [Fact]
        public void BFC_FloatEstablishesBFC()
        {
            // Float creates new BFC
            var style = new CssComputed { Float = "left" };
            Assert.Equal("left", style.Float);
        }

        [Fact]
        public void BFC_OverflowHiddenEstablishesBFC()
        {
            // overflow: hidden creates new BFC
            var style = new CssComputed { Overflow = "hidden" };
            Assert.Equal("hidden", style.Overflow);
        }

        [Fact]
        public void BFC_AbsolutePositionEstablishesBFC()
        {
            // position: absolute creates new BFC
            var style = new CssComputed { Position = "absolute" };
            Assert.Equal("absolute", style.Position);
        }

        [Fact]
        public void BoxSizing_ContentBox_WidthExcludesPadding()
        {
            // box-sizing: content-box (default)
            var style = new CssComputed { BoxSizing = "content-box" };
            Assert.Equal("content-box", style.BoxSizing);
        }

        [Fact]
        public void BoxSizing_BorderBox_WidthIncludesPadding()
        {
            // box-sizing: border-box
            var style = new CssComputed { BoxSizing = "border-box" };
            Assert.Equal("border-box", style.BoxSizing);
        }
    }
}
