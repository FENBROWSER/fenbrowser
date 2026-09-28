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

        // CSS Lists 3 §3.5 list-style: the parts in any order, a <string> type kept as
        // written, and 'none' filling in whichever of image and type is not given.
        [Theory]
        [InlineData("list-style: \"- \" inside", "\"- \"", "none")]
        [InlineData("list-style: none", "none", "none")]
        [InlineData("list-style: none disc", "disc", "none")]
        [InlineData("list-style: square linear-gradient(red, blue)", "square", "linear-gradient(red, blue)")]
        [InlineData("list-style: none url(a.png)", "none", "url(a.png)")]
        [InlineData("list-style-type: My-Style", "My-Style", "none")]
        [InlineData("list-style-type: Lower-Roman", "lower-roman", "none")]
        public async Task ListStyleValuesKeepStringsAndCustomNames(string declaration, string type, string image)
        {
            var uri = new Uri("https://lists.test/");
            var document = new HtmlParser(
                "<!doctype html><html><body><ul><li id=t style='" + declaration.Replace("'", "&#39;") + "'></ul></body></html>",
                uri).Parse();
            var styles = await CssLoader.ComputeAsync(document.DocumentElement, uri, null);
            var style = styles[document.GetElementById("t")];

            Assert.Equal(type, style.ListStyleType);
            Assert.Equal(image, style.ListStyleImage?.Trim());
        }
    }
}
