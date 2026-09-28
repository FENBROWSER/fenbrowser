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
    /// CSS Values 4 §10: a calc() width is resolved against the containing block like a
    /// percentage, whatever the box's display. x.com's Google sign-in face is a flex box
    /// with `width: calc(100% / var(--jf-scale))` under `transform: scale(1.15)`; a flex
    /// container ignored the expression, stayed 100% wide and scaled past its clip.
    /// </summary>
    public sealed class FlexContainerWidthExpressionTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  * { box-sizing: border-box; }
  body { margin: 0; }
  #row { width: 384px; display: flex; overflow: clip; }
  #shell { position: relative; width: 100%; }
  .face { --jf-scale: 1.15; height: 40px; width: calc(100% / var(--jf-scale, 1)) !important; border: 1px solid; }
  #flex { display: flex; align-items: center; justify-content: center; overflow: hidden; }
</style></head>
<body>
<div id='row'><div id='shell'><div id='flex' class='face'><span>Continue with Google</span></div></div></div>
<div style='width:384px'><div id='block' class='face'><span>Continue with Google</span></div></div>
</body></html>";

        [Fact]
        public async Task FlexContainer_ResolvesCalcWidthLikeABlock()
        {
            var doc = new HtmlParser(Html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 1280, 800);
            computer.Measure(doc, new SKSize(1280, 800));
            computer.Arrange(doc, new SKRect(0, 0, 1280, 800));

            float Width(string id) => computer.GetBox(doc.GetElementById(id)).BorderBox.Width;

            Assert.Equal(384f / 1.15f, Width("block"), 1);
            Assert.Equal(384f / 1.15f, Width("flex"), 1);
        }
    }
}
