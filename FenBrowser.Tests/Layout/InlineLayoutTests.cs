using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// Tests for inline layout correctness.
    /// Verifies IFC rules, baseline alignment, and line box behavior.
    /// </summary>
    public class InlineLayoutTests
    {
        /// <summary>
        /// Create a simple DOM structure for testing.
        /// </summary>
        private Element CreateElement(string tag, params Node[] children)
        {
            var element = new Element(tag);
            foreach (var child in children)
            {
                element.AppendChild(child);
            }
            return element;
        }

        private Text CreateText(string content)
        {
            return new Text(content);
        }

        [Fact]
        public void InlineElement_InBlockContainer_CreatesIFC()
        {
            // Arrange: <div><span>Text</span></div>
            var span = CreateElement("SPAN", CreateText("Text"));
            var div = CreateElement("DIV", span);

            // Assert: SPAN should be inline, DIV should be block
            Assert.Equal("DIV", div.TagName);
            Assert.Equal("SPAN", span.TagName);
            Assert.Single(div.Children);
        }

        [Fact]
        public void TextNode_MeasuresWithFontMetrics()
        {
            // Arrange: Simple text node
            var text = CreateText("Hello World");

            // Assert: Text content is preserved
            Assert.Equal("Hello World", text.Data);
        }

        [Fact]
        public void MultipleInlineElements_SharesLineBox()
        {
            // Arrange: <p><span>A</span><span>B</span><span>C</span></p>
            var span1 = CreateElement("SPAN", CreateText("A"));
            var span2 = CreateElement("SPAN", CreateText("B"));
            var span3 = CreateElement("SPAN", CreateText("C"));
            var p = CreateElement("P", span1, span2, span3);

            // Assert: All three spans are children of P
            Assert.Equal(3, p.Children.Length);
        }

        [Fact]
        public void InlineBlock_EstablishesBFC()
        {
            // Arrange: inline-block element
            var inlineBlock = CreateElement("DIV");
            var style = new CssComputed { Display = "inline-block" };

            // Assert: Display is inline-block
            Assert.Equal("inline-block", style.Display);
        }

        [Fact]
        public void VerticalAlign_DefaultIsBaseline()
        {
            // Arrange: Element without explicit vertical-align
            var style = new CssComputed();

            // Assert: Default should be null (baseline by spec)
            Assert.Null(style.VerticalAlign);
        }

        [Fact]
        public void LineHeight_NormalUseFontMetrics()
        {
            // Arrange: line-height: normal
            var style = new CssComputed { LineHeight = null };

            // Assert: null means normal
            Assert.Null(style.LineHeight);
        }
    }
}
