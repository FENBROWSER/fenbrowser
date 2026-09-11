using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// google.com's logo cell: a grid with a definite height and a single
    /// `minmax(0,1fr)` row, whose item uses `margin-top:auto` + `max-height` to sit at the
    /// bottom of the row. CSS Grid §11.7 sizes fr rows against the definite container
    /// height; §10.2 lets auto margins absorb the free space in the grid area.
    /// </summary>
    public sealed class GridDefiniteHeightAlignmentTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  body { margin: 0; }
  .g { display: grid; justify-items: center; align-items: center; grid-template-rows: minmax(0,1fr); height: 290px; }
  .logo { height: 100%; max-height: 92px; margin-top: auto; grid-area: 1/1; width: 272px; }
  #bottom { margin-top: auto; height: 50px; width: 100px; }
  #center { height: 50px; width: 100px; }
  #both { margin-top: auto; margin-bottom: auto; height: 50px; width: 100px; }
  #corner { margin-left: auto; margin-top: auto; height: 50px; width: 100px; }
</style></head>
<body>
  <div class='g' id='g1'><div class='logo' id='logo'></div></div>
  <div class='g'><div id='bottom'></div></div>
  <div class='g'><div id='center'></div></div>
  <div class='g'><div id='both'></div></div>
  <div class='g' id='g5'><div id='corner'></div></div>
</body></html>";

        [Fact]
        public async System.Threading.Tasks.Task FrRow_ResolvesAgainstDefiniteContainerHeight_AndAutoMarginsAlign()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));
            var boxes = computer.GetAllBoxes().ToDictionary(kv => kv.Key, kv => kv.Value);

            SKRect Box(string id) => boxes[doc.Descendants().OfType<Element>().First(e => e.Id == id)].BorderBox;

            var g1 = Box("g1");
            Assert.Equal(290f, g1.Height, 1);

            // margin-top:auto pushes the 92px-capped item to the bottom of the 290px row.
            var logo = Box("logo");
            Assert.Equal(92f, logo.Height, 1);
            Assert.Equal(g1.Bottom, logo.Bottom, 1);

            var bottom = Box("bottom");
            Assert.Equal(290f + 290f, bottom.Bottom, 1);

            var center = Box("center");
            Assert.Equal(580f + 120f, center.Top, 1);

            var both = Box("both");
            Assert.Equal(870f + 120f, both.Top, 1);

            var g5 = Box("g5");
            var corner = Box("corner");
            Assert.Equal(g5.Right, corner.Right, 1);
            Assert.Equal(g5.Bottom, corner.Bottom, 1);
        }
    }
}
