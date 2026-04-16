using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering.Css
{
    public class BorderAndPaddingSemanticsTests
    {
        [Fact]
        public async Task BorderShorthand_WithoutStyle_DoesNotProduceEffectiveBorder()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        #target { border: 1px blue; }
    </style>
</head>
<body>
    <div id='target'>x</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var target = doc.GetElementById("target");

            var computed = await CssLoader.ComputeAsync(root, new System.Uri("https://test.local"), null);
            var style = computed[target];

            Assert.Equal(0d, style.BorderThickness.Top);
            Assert.Equal(0d, style.BorderThickness.Right);
            Assert.Equal(0d, style.BorderThickness.Bottom);
            Assert.Equal(0d, style.BorderThickness.Left);
        }

        [Fact]
        public async Task NegativePadding_IsClampedOutOfComputedGeometry()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        #target { padding-left: 8px; padding-right: -10px; }
    </style>
</head>
<body>
    <div id='target'>x</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var target = doc.GetElementById("target");

            var computed = await CssLoader.ComputeAsync(root, new System.Uri("https://test.local"), null);
            var style = computed[target];

            Assert.Equal(8d, style.Padding.Left);
            Assert.Equal(0d, style.Padding.Right);
        }

        [Fact]
        public async Task BackgroundShorthand_WithUrlAndTrailingColor_ResolvesBackgroundColor()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        #target { background: url(data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==) no-repeat 99% 1px white; }
    </style>
</head>
<body>
    <div id='target'>x</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var target = doc.GetElementById("target");

            var computed = await CssLoader.ComputeAsync(root, new System.Uri("https://test.local"), null);
            var style = computed[target];

            Assert.True(style.BackgroundColor.HasValue);
            Assert.Equal(255, style.BackgroundColor.Value.Red);
            Assert.Equal(255, style.BackgroundColor.Value.Green);
            Assert.Equal(255, style.BackgroundColor.Value.Blue);
            Assert.Equal(255, style.BackgroundColor.Value.Alpha);
        }

        [Fact]
        public async Task BackgroundShorthand_TrailingColor_SurvivesEarlierTransparentUniversalRule()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        * { background: transparent; }
        body { background: url(data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==) no-repeat 99% 1px white; }
    </style>
</head>
<body>
    <div id='target'>x</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var body = doc.Children
                .OfType<Element>()
                .First(e => e.TagName == "HTML")
                .Children
                .OfType<Element>()
                .First(e => e.TagName == "BODY");

            var computed = await CssLoader.ComputeAsync(root, new System.Uri("https://test.local"), null);
            var style = computed[body];
            Assert.True(style.BackgroundColor.HasValue);
            Assert.Equal(255, style.BackgroundColor.Value.Red);
            Assert.Equal(255, style.BackgroundColor.Value.Green);
            Assert.Equal(255, style.BackgroundColor.Value.Blue);
            Assert.Equal(255, style.BackgroundColor.Value.Alpha);
        }
    }
}
