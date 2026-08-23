using System;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// PHASE 4: backend selection must honor the configuration flag.
    /// PHASE 5: adversarial cases added in the hardening pass.
    /// </summary>
    public class SvgPhase45Tests
    {
        // ------------------------------------------------------------ PHASE 4

        [Fact]
        public void BackendSelection_HonorsConfigurationFlag()
        {
            var original = SvgRendererConfiguration.Backend;
            try
            {
                SvgRendererConfiguration.Backend = SvgRendererBackend.FirstParty;
                Assert.IsType<FenSvgRenderer>(ImageLoader.CreateSvgRenderer());

                SvgRendererConfiguration.Backend = SvgRendererBackend.LegacySvgSkia;
                Assert.IsType<SvgSkiaRenderer>(ImageLoader.CreateSvgRenderer());
            }
            finally
            {
                SvgRendererConfiguration.Backend = original;
            }
        }

        [Fact]
        public void DefaultBackend_IsLegacy_OptInOnly()
        {
            var original = SvgRendererConfiguration.Backend;
            try
            {
                SvgRendererConfiguration.Backend = SvgRendererBackend.LegacySvgSkia;
                Assert.IsType<SvgSkiaRenderer>(ImageLoader.CreateSvgRenderer());
            }
            finally
            {
                SvgRendererConfiguration.Backend = original;
            }
        }

        // ------------------------------------------------------------ PHASE 5

        private readonly FenSvgRenderer _r = new();

        [Fact]
        public void ExtremeTransform_Scale1e9_ClampsAndSucceeds()
        {
            var result = _r.Render(
                "<svg width='20' height='20'><rect width='4' height='4' fill='red' transform='scale(1e9)'/></svg>");
            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public void GradientTwoNodeCycle_Terminates()
        {
            var svg = "<svg width='10' height='10'>" +
                      "<linearGradient id='g1' href='#g2'/><linearGradient id='g2' href='#g1'/>" +
                      "<rect width='10' height='10' fill='url(#g1)'/></svg>";
            var result = _r.Render(svg, new SvgRenderLimits { MaxRenderTimeMs = 1500 });
            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public void UppercaseScriptTag_IsIgnoredSafely()
        {
            var result = _renderer().Render(
                "<svg width='12' height='12'><SCRIPT>alert(1)</SCRIPT><rect width='12' height='12' fill='red'/></svg>");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(6, 6).Red);
        }

        private FenSvgRenderer _renderer() => new();

        [Fact]
        public void OversizedDataUriPayload_RejectedByAdmission()
        {
            // 13 MB of base64 characters exceeds the 12 MB admission budget
            // before any decode allocation happens.
            string payload = new string('A', 13 * 1024 * 1024);
            var result = _r.Render(
                $"<svg width='10' height='10'><image href='data:image/png;base64,{payload}'/></svg>",
                new SvgRenderLimits
                {
                    MaxRenderTimeMs = 4000,
                    MaxSourceChars = 20 * 1024 * 1024 // let the SOURCE pass so the
                                                      // image-level budget is exercised
                });
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(5, 5).Alpha);
        }
    }
}
