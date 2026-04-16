using FenBrowser.FenEngine.Rendering;
using FenBrowser.Core.Dom.V2;
using System.Linq;
using Xunit;

namespace FenBrowser.Tests.Rendering.Css
{
    public class HslColorParsingTests
    {
        [Fact]
        public void ParseColor_HslaWithoutPercentSaturationAndLightness_IsRejected()
        {
            var color = CssParser.ParseColor("hsla(0, 0, 0, 1)");

            Assert.Null(color);
        }

        [Fact]
        public void ParseColor_ValidLegacyHsla_IsAccepted()
        {
            var color = CssParser.ParseColor("hsla(0, 0%, 0%, 1)");

            Assert.NotNull(color);
            Assert.Equal(255, color.Value.Alpha);
        }

        [Fact]
        public async System.Threading.Tasks.Task Cascade_InvalidLaterColor_DoesNotOverrideEarlierValidColor()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        #target { color: transparent; color: hsla(0, 0, 0, 1); }
    </style>
</head>
<body>
    <div id='target'>FAIL</div>
</body>
</html>";

            var parser = new FenBrowser.Core.Parsing.HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var target = doc.GetElementById("target");

            var computed = await FenBrowser.FenEngine.Rendering.CssLoader.ComputeAsync(root, new System.Uri("https://test.local"), null);

            Assert.Equal("transparent", computed[target].Map["color"]);
        }
    }
}
