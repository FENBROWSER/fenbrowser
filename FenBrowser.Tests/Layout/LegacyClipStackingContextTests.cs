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
    /// CSS 2.1 11.1.2: `clip: rect(...)` clips an absolutely positioned box, its background and
    /// border included, whether or not it creates a stacking context. bing.com hides its skip
    /// links with `position:absolute; z-index:100; clip:rect(1px,1px,1px,1px)` until they are
    /// focused; the z-index made them stacking contexts, which never read clip, so both links
    /// painted over the results header.
    /// </summary>
    public sealed class LegacyClipStackingContextTests
    {
        private const string Html = @"<!doctype html><html><head><style>
body { margin: 0; background: #fff; }
.link { position: absolute; top: 20px; width: 120px; height: 32px; background: rgb(255, 0, 0); }
#clippedStacking { left: 0; z-index: 100; clip: rect(1px, 1px, 1px, 1px); }
#clippedAuto { left: 200px; clip: rect(1px, 1px, 1px, 1px); }
#visible { left: 400px; z-index: 100; }
</style></head><body>
<a class='link' id='clippedStacking' href='#'></a>
<a class='link' id='clippedAuto' href='#'></a>
<a class='link' id='visible' href='#'></a>
</body></html>";

        [Fact]
        public async Task ZeroAreaClip_HidesAPositionedBox_WithOrWithoutAStackingContext()
        {
            using var bitmap = await RenderAsync();

            Assert.False(IsRed(bitmap.GetPixel(60, 36)), "z-index:100 box with clip:rect(1px,1px,1px,1px) still painted");
            Assert.False(IsRed(bitmap.GetPixel(260, 36)), "z-index:auto box with clip:rect(1px,1px,1px,1px) still painted");
            Assert.True(IsRed(bitmap.GetPixel(460, 36)), "the unclipped control box did not paint");
        }

        private static bool IsRed(SKColor color) => color.Red > 200 && color.Green < 60 && color.Blue < 60;

        private static async Task<SKBitmap> RenderAsync()
        {
            const int width = 800;
            const int height = 200;
            var baseUri = new Uri("https://test.local/");
            var doc = new HtmlParser(Html, baseUri).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: width, viewportHeight: height);

            var renderer = new SkiaDomRenderer();
            var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            renderer.Render(root, canvas, styles, new SKRect(0, 0, width, height), baseUri.AbsoluteUri, (_, _) => { });
            return bitmap;
        }
    }
}
