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
    /// CSS 2.2 §10.5 / CSS Grid 1 §11.1: a grid container whose `height:100%` refers to an
    /// auto-height containing block has an indefinite height, so its tracks are sized by
    /// content and start at the container's top. bing.com's search form is such a grid
    /// inside a fixed wrapper that only sets `top`.
    /// </summary>
    public sealed class GridPercentHeightIndefiniteParentTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  body { margin: 0; }
  #wrap { position: fixed; top: 160px; left: 0; right: 0; margin: 0 auto; max-width: 600px; }
  #grid { display: grid; height: 100%; min-height: 52px; position: relative;
          grid-template-areas: 'sbox text mic' 'foot foot foot';
          grid-template-columns: 48px auto 32px; grid-template-rows: auto; place-items: start; }
  #icon { grid-area: sbox; width: 24px; height: 24px; margin: 14px 0 0 16px; }
  #field { width: 100%; height: 22px; }
  #mic { grid-area: mic; width: 24px; height: 24px; margin-top: 14px; }
</style></head>
<body>
  <div id='wrap'><form id='grid'><div id='icon'></div><div id='field'></div><div id='mic'></div></form></div>
</body></html>";

        [Fact]
        public async Task PercentHeightGrid_UnderAutoHeightFixedParent_PlacesTracksAtItsTop()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 1280, 800);
            computer.Measure(doc, new SKSize(1280, 800));
            computer.Arrange(doc, new SKRect(0, 0, 1280, 800));
            var boxes = computer.GetAllBoxes().ToDictionary(kv => kv.Key, kv => kv.Value);

            SKRect Box(string id) => boxes[doc.Descendants().OfType<Element>().First(e => e.Id == id)].BorderBox;

            var grid = Box("grid");
            Assert.Equal(52f, grid.Height, 1);
            Assert.Equal(grid.Top + 14f, Box("icon").Top, 1);
            Assert.Equal(grid.Top, Box("field").Top, 1);
            Assert.Equal(grid.Top + 14f, Box("mic").Top, 1);
        }
    }
}
