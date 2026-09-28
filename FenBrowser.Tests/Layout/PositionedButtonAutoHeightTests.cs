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
    /// CSS 2.2 §10.6.4 / CSS Flexbox §9.2: an out-of-flow box with auto height takes the height
    /// of its laid-out content. x.com's "Scan to get the app" card is a fixed flex-column
    /// &lt;button&gt; holding a caption and a 112px QR image; it collapsed to one caption line
    /// and took a per-character label width.
    /// </summary>
    public sealed class PositionedButtonAutoHeightTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  body { margin: 0; }
  .card { position: fixed; bottom: 20px; display: flex; flex-direction: column; align-items: center;
          gap: 8px; padding: 16px; border: 1px solid; font: 13px/16px sans-serif; }
  #btn { right: 20px; }
  #div { right: 400px; }
  img { width: 112px; height: 112px; }
</style></head>
<body>
<button class='card' id='btn'><span>Scan to get the app</span><img id='btnimg' alt='' width='160' height='160'></button>
<div class='card' id='div'><span>Scan to get the app</span><img id='divimg' alt='' width='160' height='160'></div>
</body></html>";

        [Fact]
        public async Task FixedFlexColumnButton_TakesItsContentSize()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 1280, 800);
            computer.Measure(doc, new SKSize(1280, 800));
            computer.Arrange(doc, new SKRect(0, 0, 1280, 800));
            var boxes = computer.GetAllBoxes().ToDictionary(kv => kv.Key, kv => kv.Value);

            BoxModel Box(string id) => boxes[doc.Descendants().OfType<Element>().First(e => e.Id == id)];

            // 16px caption + 8px gap + 112px image.
            const float contentHeight = 16f + 8f + 112f;
            Assert.Equal(contentHeight, Box("div").ContentBox.Height, 1);
            Assert.Equal(contentHeight, Box("btn").ContentBox.Height, 1);

            // Shrink-to-fit width is the widest item, same as the div - not a per-character
            // label estimate.
            Assert.Equal(Box("div").ContentBox.Width, Box("btn").ContentBox.Width, 1);

            // Bottom-anchored: the card ends 20px above the viewport bottom and holds its image.
            Assert.Equal(780f, Box("btn").BorderBox.Bottom, 1);
            Assert.True(Box("btnimg").BorderBox.Bottom <= Box("btn").ContentBox.Bottom + 0.5f);
        }
    }
}
