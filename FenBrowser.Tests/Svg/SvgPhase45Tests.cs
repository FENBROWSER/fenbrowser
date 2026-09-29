using System;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// PHASE 4: backend selection must honor the configuration flag.
    /// PHASE 5: adversarial cases added in the hardening pass.
    /// <para>
    /// This class assigns the process-wide
    /// <see cref="SvgRendererConfiguration.Backend"/> and rewrites the backend
    /// environment variable, so it runs inside
    /// <see cref="SvgRendererBackendStateCollection"/> and never against another
    /// test's backend selection. The collection definition itself lives in
    /// SvgRendererBackendStateCollection.cs.
    /// </para>
    /// </summary>
    [Collection(SvgRendererBackendStateCollection.Name)]
    public class SvgPhase45Tests
    {
        // ------------------------------------------------------------ PHASE 4

        [Fact]
        public void ImageLoaderRenderer_IgnoresBackendSelectionAndResolvesFirstParty()
        {
            var original = SvgRendererConfiguration.Backend;
            try
            {
                SvgRendererConfiguration.Backend = SvgRendererBackend.FirstParty;
                Assert.IsType<FenSvgRenderer>(ImageLoader.CreateSvgRenderer());
                Assert.IsType<FenSvgRenderer>(SvgRendererFactory.GetConfiguredRenderer());
            }
            finally
            {
                SvgRendererConfiguration.Backend = original;
            }
        }

        [Fact]
        public void DefaultBackend_IsFirstParty_ThereIsNoAlternateSelection()
        {
            Assert.True(SvgRendererConfiguration.TryParse(null, out var defaultBackend));
            Assert.Equal(SvgRendererBackend.FirstParty, defaultBackend);
            Assert.IsType<FenSvgRenderer>(SvgRendererFactory.GetRenderer(defaultBackend));
            Assert.Single(Enum.GetValues<SvgRendererBackend>());

            var original = SvgRendererConfiguration.Backend;
            try
            {
                SvgRendererConfiguration.Backend = SvgRendererBackend.FirstParty;
                Assert.IsType<FenSvgRenderer>(ImageLoader.CreateSvgRenderer());
            }
            finally
            {
                SvgRendererConfiguration.Backend = original;
            }
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData("not-a-backend", SvgRendererConfiguration.UnrecognizedValueReasonCode)]
        public void AbsentBlankOrInvalidEnvironment_SelectsFirstPartyProcessWide(
            string? value,
            string? expectedReasonCode)
        {
            WithEnvironmentValue(value, () =>
            {
                Assert.Equal(SvgRendererBackend.FirstParty, SvgRendererConfiguration.Backend);
                Assert.Equal(expectedReasonCode, SvgRendererConfiguration.LastParseReasonCode);
                Assert.IsType<FenSvgRenderer>(ImageLoader.CreateSvgRenderer());
                Assert.IsType<FenSvgRenderer>(SvgRendererFactory.GetConfiguredRenderer());
            });
        }

        [Theory]
        [InlineData("first-party", null)]
        [InlineData("fen", null)]
        [InlineData("legacy", SvgRendererConfiguration.DeprecatedAliasReasonCode)]
        [InlineData("svg-skia", SvgRendererConfiguration.DeprecatedAliasReasonCode)]
        public void EnvironmentSelection_IsObservedByTheSharedFactory(
            string value,
            string? expectedReasonCode)
        {
            WithEnvironmentValue(value, () =>
            {
                Assert.True(SvgRendererConfiguration.TryParse(value, out var expected));
                Assert.Equal(SvgRendererBackend.FirstParty, expected);
                Assert.Equal(expected, SvgRendererConfiguration.Backend);
                Assert.Equal(expectedReasonCode, SvgRendererConfiguration.LastParseReasonCode);
                Assert.IsType(
                    SvgRendererFactory.GetRenderer(expected).GetType(),
                    SvgRendererFactory.GetConfiguredRenderer());
            });
        }

        [Theory]
        [InlineData("hybrid")]
        [InlineData("AUTO")]
        public void RetiredRoutingValues_AreRejectedButStillResolveToFirstParty(string value)
        {
            WithEnvironmentValue(value, () =>
            {
                Assert.False(SvgRendererConfiguration.TryParse(value, out var parsed));
                Assert.Equal(SvgRendererBackend.FirstParty, parsed);
                Assert.Equal(SvgRendererBackend.FirstParty, SvgRendererConfiguration.Backend);
                Assert.Equal(
                    SvgRendererConfiguration.UnrecognizedValueReasonCode,
                    SvgRendererConfiguration.LastParseReasonCode);
                Assert.IsType<FenSvgRenderer>(SvgRendererFactory.GetConfiguredRenderer());
                Assert.Contains(
                    SvgRendererConfiguration.UnrecognizedValueReasonCode,
                    SvgRendererConfiguration.DescribeConfiguration(),
                    StringComparison.Ordinal);
                Assert.InRange(
                    SvgRendererConfiguration.DescribeConfiguration().Length,
                    1,
                    SvgRendererConfiguration.MaxConfigurationDiagnosticChars);
            });
        }

        private static void WithEnvironmentValue(string? value, Action assertion)
        {
            string? originalValue =
                Environment.GetEnvironmentVariable(SvgRendererConfiguration.EnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    SvgRendererConfiguration.EnvironmentVariable, value);
                SvgRendererConfiguration.ReloadFromEnvironment();
                assertion();
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    SvgRendererConfiguration.EnvironmentVariable, originalValue);
                SvgRendererConfiguration.ReloadFromEnvironment();
            }
        }

        // ------------------------------------------------------------ PHASE 5

        private readonly FenSvgRenderer _r = new();

        [Fact]
        public void ExtremeTransform_Scale1e9_ClampsAndSucceeds()
        {
            using var result = _r.Render(
                "<svg width='20' height='20'><rect width='4' height='4' fill='red' transform='scale(1e9)'/></svg>");
            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public void GradientTwoNodeCycle_Terminates()
        {
            var svg = "<svg width='10' height='10'>" +
                      "<linearGradient id='g1' href='#g2'/><linearGradient id='g2' href='#g1'/>" +
                      "<rect width='10' height='10' fill='url(#g1)'/></svg>";
            using var result = _r.Render(svg, new SvgRenderLimits { MaxRenderTimeMs = 1500 });

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.True(result.RequiresFallback);
            Assert.Contains("paint-server", result.FallbackReasonCodes);
        }

        [Fact]
        public void UppercaseScriptTag_FailsClosedAsDynamicContent()
        {
            using var result = _renderer().Render(
                "<svg width='12' height='12'><SCRIPT>alert(1)</SCRIPT><rect width='12' height='12' fill='red'/></svg>");

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.RequiresFallback);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
        }

        [Fact]
        public void LowercaseScriptTag_FailsClosedAsDynamicContent()
        {
            using var result = _renderer().Render(
                "<svg width='12' height='12'><script>alert(1)</script><rect width='12' height='12' fill='red'/></svg>");

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.RequiresFallback);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
        }

        private FenSvgRenderer _renderer() => new();

        [Fact]
        public void OversizedDataUriPayload_RejectedByAdmission()
        {
            // 13 MB of base64 characters exceeds the 12 MB admission budget
            // before any decode allocation happens.
            string payload = new string('A', 13 * 1024 * 1024);
            using var result = _r.Render(
                $"<svg width='10' height='10'><image href='data:image/png;base64,{payload}'/></svg>",
                new SvgRenderLimits
                {
                    MaxRenderTimeMs = 4000,
                    MaxSourceChars = 20 * 1024 * 1024 // let the SOURCE pass so the
                                                      // image-level budget is exercised
                });

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.HadResourceRejection);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
        }
    }
}
