using System;
using System.Threading.Tasks;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    // HTML §15.3.8: the list type attributes are presentational hints for
    // list-style-type; the numbering letters are case-sensitive, the bullet names
    // are not, and an author declaration wins over the hint.
    public class ListTypeAttributeTests
    {
        [Theory]
        [InlineData("<ol type=i><li id=t></ol>", "lower-roman")]
        [InlineData("<ol type=I><li id=t></ol>", "upper-roman")]
        [InlineData("<ol type=A><li id=t></ol>", "upper-alpha")]
        [InlineData("<ul type=CIRCLE><li id=t></ul>", "circle")]
        [InlineData("<ul><li id=t type=a></ul>", "lower-alpha")]
        [InlineData("<ul><li id=t type=square></ul>", "square")]
        [InlineData("<ol type=i style='list-style-type: square'><li id=t></ol>", "square")]
        public async Task ListTypeAttributesMapToListStyleType(string body, string expected)
        {
            var uri = new Uri("https://lists.test/");
            var document = new HtmlParser("<!doctype html><html><body>" + body + "</body></html>", uri).Parse();
            var styles = await CssLoader.ComputeAsync(document.DocumentElement, uri, null);

            Assert.Equal(expected, styles[document.GetElementById("t")].ListStyleType);
        }
    }
}
