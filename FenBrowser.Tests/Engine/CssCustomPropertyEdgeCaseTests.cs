using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssCustomPropertyEdgeCaseTests
    {
        [Fact]
        public async Task ComputeAsync_RootCustomProperties_AreCaseSensitive()
        {
            CssLoader.ClearCaches();

            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    :root { --MyVar: rgb(1, 2, 3); --myvar: rgb(4,5,6); }
    #t { color: var(--MyVar); background-color: var(--myvar); }
  </style>
</head>
<body><div id='t'>X</div></body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.Id == "t");
            var style = computed[target];

            Assert.Equal("rgb(1, 2, 3)", style.CustomProperties["--MyVar"]);
            Assert.Equal("rgb(4,5,6)", style.CustomProperties["--myvar"]);
            Assert.Equal("rgb(1, 2, 3)", style.Map["color"]);
            Assert.Equal("rgb(4,5,6)", style.Map["background-color"]);
        }

        [Fact]
        public async Task ComputeAsync_InlineStyle_ParsesSemicolonsInsideFunctions()
        {
            CssLoader.ClearCaches();

            const string html = @"
<!doctype html>
<html>
<body>
  <div id='t' style=""background-image: url('data:image/svg+xml;utf8,<svg></svg>'); color: red;"">X</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.Id == "t");
            var style = computed[target];

            Assert.Equal("red", style.Map["color"]);
            Assert.Contains("data:image/svg+xml;utf8", style.Map["background-image"], StringComparison.Ordinal);
        }
    }
}
