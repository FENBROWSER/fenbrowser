using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS Flexbox §9.4 step 11 / §9.7: a stretched or flexed item gets a definite size,
    /// and its padding and border sit outside that content size exactly once, whatever its
    /// box-sizing (CSS Box Sizing 3 §3). x.com's login column is a padded border-box flex
    /// item stretched to the viewport; its centred form came out 40px too high.
    /// </summary>
    public sealed class FlexForcedSizeBoxSizingTests
    {
        [Theory]
        [InlineData("border-box")]
        [InlineData("content-box")]
        public async Task StretchedPaddedItem_CentresItsContentInsideThePadding(string boxSizing)
        {
            string html = @"
<!doctype html>
<html><head><style>
  * { box-sizing: " + boxSizing + @"; }
  body { margin: 0; }
  #row { display: flex; height: 752px; }
  #col { display: flex; flex: 1; align-items: center; padding: 40px 16px 40px 36px; }
  #hero { flex: 1; }
  #form { width: 400px; height: 600px; }
</style></head>
<body><div id='row'><div id='col'><div id='form'></div></div><div id='hero'></div></div></body></html>";

            var doc = new HtmlParser(html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 1280, 800);
            computer.Measure(doc, new SKSize(1280, 800));
            computer.Arrange(doc, new SKRect(0, 0, 1280, 800));

            var col = computer.GetBox(doc.GetElementById("col"));
            var form = computer.GetBox(doc.GetElementById("form"));

            Assert.Equal(752f, col.BorderBox.Height, 1);
            Assert.Equal(672f, col.ContentBox.Height, 1);
            // (672 - 600) / 2 below the 40px top padding.
            Assert.Equal(40f + 36f, form.BorderBox.Top, 1);
        }
    }
}
