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
    /// CSS 2.2 §10.4: when the width solved from the absolute-position constraint equation
    /// breaks max-width/min-width, the equation is solved again with the limit as 'width'.
    /// bing.com centers its fixed search box with `left:0; right:0; margin:0 auto; max-width`.
    /// </summary>
    public sealed class AbsoluteMaxWidthCenteringTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  body { margin: 0; }
  #fixed { position: fixed; top: 160px; left: 0; right: 0; margin: 0 auto; max-width: 975px; height: 52px; }
  #abs { position: absolute; top: 300px; left: 0; right: 0; margin: 0 auto; max-width: 400px; height: 10px;
         padding: 0 20px; box-sizing: border-box; }
  #min { position: absolute; top: 400px; left: 0; width: 50px; min-width: 120px; height: 10px; }
</style></head>
<body><div id='fixed'></div><div id='abs'></div><div id='min'></div></body></html>";

        [Fact]
        public async Task AutoMargins_CenterTheBoxAfterMaxWidthClamps()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 1280, 800);
            computer.Measure(doc, new SKSize(1280, 800));
            computer.Arrange(doc, new SKRect(0, 0, 1280, 800));
            var boxes = computer.GetAllBoxes().ToDictionary(kv => kv.Key, kv => kv.Value);

            SKRect Box(string id) => boxes[doc.Descendants().OfType<Element>().First(e => e.Id == id)].BorderBox;

            var fixedBox = Box("fixed");
            Assert.Equal(975f, fixedBox.Width, 1);
            Assert.Equal((1280f - 975f) / 2f, fixedBox.Left, 1);

            var abs = Box("abs");
            Assert.Equal(400f, abs.Width, 1);
            Assert.Equal((1280f - 400f) / 2f, abs.Left, 1);

            var min = Box("min");
            Assert.Equal(120f, min.Width, 1);
            Assert.Equal(0f, min.Left, 1);
        }
    }
}
