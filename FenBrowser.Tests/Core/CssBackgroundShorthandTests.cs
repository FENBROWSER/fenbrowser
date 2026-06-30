using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class CssBackgroundShorthandTests
    {
        [Fact]
        public async Task BackgroundShorthand_PositionOnlyResetsColorToTransparent()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>.box { background: 0 0; }</style>
</head>
<body>
    <div class='box'>Probe</div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://test.local"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("transparent", style.Map["background-color"]);
            Assert.Equal("none", style.Map["background-image"]);
            Assert.Equal("0 0", style.Map["background-position"]);
            Assert.Equal("0", style.Map["background-position-x"]);
            Assert.Equal("0", style.Map["background-position-y"]);
        }
    }
}
