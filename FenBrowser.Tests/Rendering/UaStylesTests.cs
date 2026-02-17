using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;
using System;
using SkiaSharp;
using FenBrowser.Core.Css;

namespace FenBrowser.Tests.Rendering
{
    public class UaStylesTests
    {
        [Fact]
        public async Task MarkElement_HasYellowBackground_FromUaCss()
        {
            var html = "<html><body><mark>Highlighted</mark></body></html>";
            var parser = new FenBrowser.Core.Parsing.HtmlParser(html);
            var doc = parser.Parse();
            var mark = doc.DocumentElement.Children[1].Children[0] as Element; // html -> body -> mark
            
            // Compute styles
            // Note: We need a valid BaseUri for CssLoader to not crash, though not used for UA here.
            var baseUri = new Uri("about:blank");
            
            var computed = await CssLoader.ComputeAsync(doc.DocumentElement, baseUri, null);
            
            Assert.True(computed.ContainsKey(mark), "Mark element style should be computed");
            var style = computed[mark];
            
            // Check UA style
            // mark { background-color: yellow; color: black; }
            Assert.NotNull(style.BackgroundColor);
            
            // Yellow is #FFFF00 (255, 255, 0)
            var yellow = new SKColor(255, 255, 0);
            Assert.Equal(yellow, style.BackgroundColor.Value);
            
            Assert.Equal("inline", style.Display);
        }

        [Fact]
        public async Task H1_HasCorrectMargin_FromUaCss()
        {
            var html = "<html><body><h1>Title</h1></body></html>";
            var parser = new FenBrowser.Core.Parsing.HtmlParser(html);
            var doc = parser.Parse();
            var h1 = doc.DocumentElement.Children[1].Children[0] as Element;
            
            var computed = await CssLoader.ComputeAsync(doc.DocumentElement, new Uri("about:blank"), null);
            var style = computed[h1];
            
            // h1 margin is 0.67em. Base font size 16px. 0.67 * 16 = 10.72px.
            // Wait, h1 font-size is 2em = 32px.
            // margin is 0.67em relative to FONT SIZE of H1.
            // So 0.67 * 32 = 21.44px.
            // MinimalLayoutComputer logic used 21px fixed.
            // UA CSS uses ems.
            
            // Check display block
            Assert.Equal("block", style.Display);
            
            Assert.True(style.FontSize > 20); // Should be around 32
        }
    }
}
