using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    /// <summary>
    /// HTML §13.2.6.5: the tokenizer lowercases names, so SVG elements and attributes
    /// with camelCase names get their SVG spelling back in foreign content. Without it
    /// an inline gradient, clip path or filter primitive reaches the SVG renderer as
    /// an unknown lowercase element and the whole icon is refused.
    /// </summary>
    public sealed class HtmlSvgNameAdjustmentTests
    {
        [Theory]
        [InlineData("lineargradient", "linearGradient")]
        [InlineData("radialgradient", "radialGradient")]
        [InlineData("clippath", "clipPath")]
        [InlineData("textpath", "textPath")]
        [InlineData("foreignobject", "foreignObject")]
        [InlineData("fegaussianblur", "feGaussianBlur")]
        [InlineData("fedropshadow", "feDropShadow")]
        [InlineData("animatetransform", "animateTransform")]
        public void SvgElementNames_GetTheirSvgSpelling(string written, string expected)
        {
            var document = Parse($"<svg><{written}></{written}></svg>");

            Element element = Elements(document).Single(e => e.LocalName.Equals(written, System.StringComparison.OrdinalIgnoreCase));
            Assert.Equal(expected, element.LocalName);
        }

        [Theory]
        [InlineData("spreadmethod", "spreadMethod")]
        [InlineData("clippathunits", "clipPathUnits")]
        [InlineData("pathlength", "pathLength")]
        [InlineData("patterncontentunits", "patternContentUnits")]
        [InlineData("systemlanguage", "systemLanguage")]
        [InlineData("viewbox", "viewBox")]
        public void SvgAttributeNames_GetTheirSvgSpelling(string written, string expected)
        {
            var document = Parse($"<svg><g {written}='1'></g></svg>");

            Element g = Elements(document).Single(e => e.LocalName == "g");
            Assert.Equal("1", g.GetAttribute(expected));
        }

        [Fact]
        public void HtmlElementsWithTheSameNames_KeepTheirLowercaseNames()
        {
            var document = Parse("<div><lineargradient></lineargradient></div>");

            Assert.Contains(Elements(document), e => e.LocalName == "lineargradient");
        }

        [Fact]
        public void AdjustedForeignObject_IsStillAnHtmlIntegrationPoint()
        {
            var document = Parse("<svg><foreignobject><div>x</div></foreignobject></svg>");

            Element div = Elements(document).Single(e => e.LocalName == "div");
            Assert.Equal(Namespaces.Html, div.NamespaceUri);
            Assert.Equal("foreignObject", div.ParentElement!.LocalName);
        }

        [Fact]
        public void LowercaseEndTags_CloseAdjustedElements()
        {
            var document = Parse("<svg><lineargradient><stop></stop></lineargradient><rect></rect></svg>");

            Element rect = Elements(document).Single(e => e.LocalName == "rect");
            Assert.Equal("svg", rect.ParentElement!.LocalName);
        }

        private static Document Parse(string body) =>
            new HtmlParser("<!doctype html><html><body>" + body + "</body></html>").Parse();

        private static IEnumerable<Element> Elements(Node root)
        {
            var pending = new Stack<Node>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                Node node = pending.Pop();
                if (node is Element element) yield return element;
                for (Node? child = node.LastChild; child != null; child = child.PreviousSibling)
                {
                    pending.Push(child);
                }
            }
        }
    }
}
