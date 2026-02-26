using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssBackgroundShorthandColorTests
    {
        [Fact]
        public async Task BackgroundShorthand_LastLayerOklabColor_IsExtracted()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    .card { background: linear-gradient(90deg, red, blue), oklab(0.6 0.1 0.1); }
  </style>
</head>
<body>
  <div class='card'>x</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var card = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("card"));

            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var style = computed[card];
            var expected = CssParser.ParseColor("oklab(0.6 0.1 0.1)");

            Assert.True(expected.HasValue);
            Assert.True(style.BackgroundColor.HasValue);
            Assert.Equal(expected.Value, style.BackgroundColor.Value);
        }

        [Fact]
        public async Task BackgroundShorthand_ModernRgbFunctionToken_IsExtracted()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    .card { background: url('https://example.com/a.png') center / cover no-repeat rgb(10 20 30 / 50%); }
  </style>
</head>
<body>
  <div class='card'>x</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var card = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("card"));

            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var style = computed[card];
            var expected = CssParser.ParseColor("rgb(10 20 30 / 50%)");

            Assert.True(expected.HasValue);
            Assert.True(style.BackgroundColor.HasValue);
            Assert.Equal(expected.Value, style.BackgroundColor.Value);
        }
    }
}
