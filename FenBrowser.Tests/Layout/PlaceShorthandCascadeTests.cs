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
    /// CSS Cascade 4 §6 / CSS Box Alignment 3 §6.3: a shorthand declared by the winning rule
    /// sets its longhands, so `place-items:start` overrides `align-items:center` from a less
    /// specific rule. bing.com relies on this to top-align its search form's grid items.
    /// </summary>
    public sealed class PlaceShorthandCascadeTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  body { margin: 0; }
  .form { align-items: center; justify-items: end; }
  .form.grid { display: grid; grid-template-columns: 100px; grid-template-rows: 200px; place-items: start; }
  #item { width: 20px; height: 20px; }
</style></head>
<body><div class='form grid' id='g'><div id='item'></div></div></body></html>";

        [Fact]
        public async Task PlaceItemsShorthand_OverridesLonghandsFromLessSpecificRule()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));
            var boxes = computer.GetAllBoxes().ToDictionary(kv => kv.Key, kv => kv.Value);

            SKRect Box(string id) => boxes[doc.Descendants().OfType<Element>().First(e => e.Id == id)].BorderBox;

            var grid = Box("g");
            var item = Box("item");
            Assert.Equal(grid.Top, item.Top, 1);
            Assert.Equal(grid.Left, item.Left, 1);
        }
    }
}
