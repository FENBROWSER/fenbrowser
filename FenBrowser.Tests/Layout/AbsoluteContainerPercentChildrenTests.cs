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
    /// CSS 2.2 §10.3.7 / §10.6.4 / §10.5: an absolutely positioned box's contents are laid
    /// out in the size solved for it against its containing block, so a percentage child of
    /// a `calc(100% - 250px)` box takes half of the solved width, not of that calc applied a
    /// second time, and `height:100%` resolves against a height given by top and bottom.
    /// w3schools' tryit editor is exactly this shape.
    /// </summary>
    public sealed class AbsoluteContainerPercentChildrenTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  html { box-sizing: border-box; } *, *:before, *:after { box-sizing: inherit; }
  body { margin: 0; }
  #c { position: absolute; top: 44px; bottom: 0; width: calc(100% - 250px); overflow: auto; }
  .half { float: left; width: 50%; height: 100%; }
  #pane { width: 100%; height: 100%; padding: 1px 10px 10px 5px; }
  #fill { width: 100%; height: 100%; }
</style></head>
<body><div id='c'><div class='half' id='a'></div><div class='half' id='b'><div id='pane'><div id='fill'></div></div></div></div></body></html>";

        [Fact]
        public async Task PercentChildren_ResolveAgainstTheSolvedBox()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null, viewportWidth: 1280, viewportHeight: 800);
            var computer = new LayoutEngineComputer(styles, 1280, 800);
            computer.Measure(doc, new SKSize(1280, 800));
            computer.Arrange(doc, new SKRect(0, 0, 1280, 800));
            var boxes = computer.GetAllBoxes().ToDictionary(kv => kv.Key, kv => kv.Value);

            SKRect Box(string id) => boxes[doc.Descendants().OfType<Element>().First(e => e.Id == id)].BorderBox;

            var container = Box("c");
            Assert.Equal(1030f, container.Width, 1);
            Assert.Equal(756f, container.Height, 1);

            var a = Box("a");
            var b = Box("b");
            Assert.Equal(515f, a.Width, 1);
            Assert.Equal(756f, a.Height, 1);
            Assert.Equal(515f, b.Width, 1);
            Assert.Equal(515f, b.Left, 1);
            Assert.Equal(44f, b.Top, 1);

            var fill = Box("fill");
            Assert.Equal(500f, fill.Width, 1);
            Assert.Equal(745f, fill.Height, 1);
        }
    }
}
