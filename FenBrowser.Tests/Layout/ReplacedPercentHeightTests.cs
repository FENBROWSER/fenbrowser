using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// CSS 2.2 §10.5: a percentage height on a replaced element resolves against the
    /// containing block's height, and computes to 'auto' when that height is not definite.
    /// bing.com's search icons are `svg { height: 100% }` inside auto-height labels.
    /// </summary>
    public sealed class ReplacedPercentHeightTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  body { margin: 0; }
  #wrap { width: 200px; }
  .icon { position: relative; min-width: 28px; margin: auto 4px; width: 5%; max-height: 23px; height: 100%; }
  #wrap svg { height: 100%; width: 100%; }
  #definite { height: 50px; }
  #definite svg { height: 100%; }
</style></head>
<body>
  <div id='wrap'><label class='icon'><svg id='auto' width='25' height='24' viewBox='0 0 25 24'></svg></label></div>
  <div id='definite'><svg id='fixed' width='25' height='24' viewBox='0 0 25 24'></svg></div>
</body></html>";

        [Fact]
        public async Task PercentHeight_IsAutoUnderIndefiniteHeight_AndResolvesUnderDefiniteHeight()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 1280, 800);
            computer.Measure(doc, new SKSize(1280, 800));
            computer.Arrange(doc, new SKRect(0, 0, 1280, 800));
            var boxes = computer.GetAllBoxes().ToDictionary(kv => kv.Key, kv => kv.Value);

            SKRect Box(string id) => boxes[doc.Descendants().OfType<Element>().First(e => e.Id == id)].ContentBox;

            // Inline-level: width:100% of the 200px block, height auto from the 25:24 ratio.
            var auto = Box("auto");
            Assert.Equal(200f, auto.Width, 1);
            Assert.Equal(192f, auto.Height, 1);

            var fixedSvg = Box("fixed");
            Assert.Equal(50f, fixedSvg.Height, 1);
        }
    }
}
