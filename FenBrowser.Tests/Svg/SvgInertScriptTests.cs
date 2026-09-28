using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// SVG used as an image runs with scripting disabled (SVG Integration, secure
    /// animated mode): script never executes and the document still paints. The
    /// default renderer contract keeps refusing documents that declare script, so a
    /// conformance harness never certifies a pre-script frame.
    /// </summary>
    [Collection(SvgRendererBackendStateCollection.Name)]
    public sealed class SvgInertScriptTests
    {
        private const string Scripted =
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>" +
            "<script>document.documentElement.remove()</script>" +
            "<rect width='10' height='10' fill='lime'/></svg>";

        [Fact]
        public void DefaultLimits_StillRefuseDeclaredScript()
        {
            using var result = new FenSvgRenderer().Render(Scripted);

            Assert.False(result.Success);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData(Scripted)]
        [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>" +
                    "<rect width='10' height='10' fill='lime'>" +
                    "<animate attributeName='width' from='10' to='10' dur='1s' onbegin='alert(1)'/></rect></svg>")]
        [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>" +
                    "<style>:lang(en) { fill: lime }</style><script>0</script>" +
                    "<text x='0' y='8' lang='en' font-size='8'>A</text>" +
                    "<rect width='10' height='10' fill='lime'/></svg>")]
        public void InertScript_PaintsTheDocumentAsAuthored(string svg)
        {
            var limits = SvgRenderLimits.Default;
            limits.TreatScriptsAsInert = true;

            using var result = new FenSvgRenderer().Render(svg, limits);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public void InertScript_IsReportedAsIgnored()
        {
            var limits = SvgRenderLimits.Default;
            limits.TreatScriptsAsInert = true;

            using var result = new FenSvgRenderer().Render(Scripted, limits);

            Assert.Contains(result.Warnings, warning => warning.Contains("inert", StringComparison.Ordinal));
        }
    }
}
