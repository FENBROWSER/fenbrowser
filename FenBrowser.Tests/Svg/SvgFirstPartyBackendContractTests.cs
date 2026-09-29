using System;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// First-party-only backend contract. SVG rendering has exactly one backend,
    /// so the suite pins the process-wide default, the deprecated legacy
    /// environment aliases, the resource admission policy, and the fail-closed
    /// result shape instead of comparing against a second renderer.
    /// <para>
    /// <c>TryParse</c> and <c>DescribeValue</c> record into the process-wide
    /// <see cref="SvgRendererConfiguration"/> last-parse reason code and
    /// <c>ProcessWideBackendSelection_RejectsUndefinedValues</c> assigns the
    /// process-wide selection, so this class runs inside
    /// <see cref="SvgRendererBackendStateCollection"/> and never reads a backend
    /// state another test left behind.
    /// </para>
    /// </summary>
    [Collection(SvgRendererBackendStateCollection.Name)]
    public class SvgFirstPartyBackendContractTests
    {
        private readonly FenSvgRenderer _renderer = new FenSvgRenderer();

        [Fact]
        public void DefaultLimits_AllowNormalComplexInlineArtworkBudget()
        {
            var limits = SvgRenderLimits.Default;

            Assert.Equal(250, limits.MaxRenderTimeMs);
            Assert.Equal(32, limits.MaxRecursionDepth);
            Assert.Equal(16, limits.MaxFilterCount);
            Assert.False(limits.AllowExternalReferences);
        }

        [Fact]
        public void ViewBoxOnlyIconRendersVisiblePixelsOnColdUse()
        {
            var svg = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" width=""32"" height=""32"">
    <path d=""M10.226 17.284c-2.965-.36-5.054-2.493-5.054-5.256 0-1.123.404-2.336 1.078-3.144C3 4 7 1 12 1s9 3 9 9-4 11-9 12c1-2 1-3-1.774-4.716Z""/>
</svg>";

            using var result = _renderer.Render(svg);

            AssertAdmissible(result);
            Assert.True(BitmapHasVisiblePixels(result.Bitmap));
        }

        [Fact]
        public void DefaultBackend_IsFirstParty()
        {
            Assert.True(SvgRendererConfiguration.TryParse(null, out var defaultBackend));
            Assert.Equal(SvgRendererBackend.FirstParty, defaultBackend);
            Assert.IsType<FenSvgRenderer>(SvgRendererFactory.GetRenderer(defaultBackend));
            Assert.IsType<FenSvgRenderer>(SvgRendererFactory.GetConfiguredRenderer());
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("not-a-backend", false)]
        [InlineData("hybrid", false)]
        [InlineData("auto", false)]
        public void AbsentBlankOrUnrecognizedValue_ResolvesToFirstParty(string? value, bool recognized)
        {
            Assert.Equal(recognized, SvgRendererConfiguration.TryParse(value, out var backend));
            Assert.Equal(SvgRendererBackend.FirstParty, backend);
            Assert.IsType<FenSvgRenderer>(SvgRendererFactory.GetRenderer(backend));
        }

        [Fact]
        public void UndefinedBackendValue_IsNeverAdmissible()
        {
            Assert.All(
                new[] { 1, 7, 42, -1, int.MaxValue },
                raw =>
                {
                    Assert.False(SvgRendererBackendPolicy.IsAdmissible((SvgRendererBackend)raw));
                    Assert.Equal(
                        SvgRendererBackend.FirstParty,
                        SvgRendererBackendPolicy.Normalize((SvgRendererBackend)raw));
                });

            using var result = new SvgRenderResult
            {
                Success = true,
                Backend = (SvgRendererBackend)7,
                Bitmap = new SKBitmap(2, 2)
            };

            Assert.False(SvgRenderResult.IsAdmissible(result));
            AssertBoundedRejection(result);
        }

        [Fact]
        public void UndefinedBackendValue_IsRejectedBeforeAnyOtherSignal()
        {
            using var failed = new SvgRenderResult
            {
                Success = false,
                ErrorMessage = "SVG render failed",
                Backend = (SvgRendererBackend)7
            };
            using var fallback = new SvgRenderResult
            {
                Success = true,
                RequiresFallback = true,
                FallbackReasonCodes = new[] { "unsupported-feature" },
                Backend = (SvgRendererBackend)7
            };

            Assert.False(SvgRenderResult.IsAdmissible(failed));
            AssertBoundedRejection(failed);
            Assert.False(SvgRenderResult.IsAdmissible(fallback));
            AssertBoundedRejection(fallback);
        }

        [Fact]
        public void Backend_RejectsUndefinedValuesWithBoundedReason()
        {
            using var result = new SvgRenderResult
            {
                Success = true,
                Backend = (SvgRendererBackend)7,
                Bitmap = new SKBitmap(2, 2)
            };

            string reason = SvgRenderResult.DescribeRejection(result);

            AssertBoundedRejection(result);
            Assert.Contains("first-party", reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unsupported", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Backend_CannotBeAssignedByCallersOutsideTheAdapter()
        {
            var setter = typeof(SvgRenderResult)
                .GetProperty(nameof(SvgRenderResult.Backend))!
                .SetMethod!;

            Assert.False(setter.IsPublic);
        }

        [Fact]
        public void ProcessWideBackendSelection_RejectsUndefinedValues()
        {
            var original = SvgRendererConfiguration.Backend;
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => SvgRendererConfiguration.Backend = (SvgRendererBackend)7);
                Assert.Equal(SvgRendererBackend.FirstParty, SvgRendererConfiguration.Backend);
            }
            finally
            {
                SvgRendererConfiguration.Backend = original;
            }
        }

        [Fact]
        public void Factory_RejectsUndefinedBackendValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SvgRendererFactory.GetRenderer((SvgRendererBackend)7));
        }

        [Theory]
        [InlineData(null, true, null)]
        [InlineData("", true, null)]
        [InlineData("first-party", true, null)]
        [InlineData("fen", true, null)]
        [InlineData("legacy", true, SvgRendererConfiguration.DeprecatedAliasReasonCode)]
        [InlineData("svg-skia", true, SvgRendererConfiguration.DeprecatedAliasReasonCode)]
        [InlineData("hybrid", false, SvgRendererConfiguration.UnrecognizedValueReasonCode)]
        [InlineData("auto", false, SvgRendererConfiguration.UnrecognizedValueReasonCode)]
        [InlineData("not-a-backend", false, SvgRendererConfiguration.UnrecognizedValueReasonCode)]
        public void ParseDiagnostics_SeparateDeprecatedAliasesFromUnusableValues(
            string? value,
            bool recognized,
            string? expectedReasonCode)
        {
            Assert.Equal(
                recognized,
                SvgRendererConfiguration.TryParse(value, out var backend, out var reasonCode));
            Assert.Equal(SvgRendererBackend.FirstParty, backend);
            Assert.Equal(expectedReasonCode, reasonCode);
        }

        [Theory]
        [InlineData("legacy", true)]
        [InlineData("svg-skia", true)]
        [InlineData("hybrid", false)]
        [InlineData("auto", false)]
        [InlineData("not-a-backend", false)]
        public void ConfigurationDiagnostics_StayBoundedAndSourceFree(string value, bool recognized)
        {
            string description = SvgRendererConfiguration.DescribeValue(value);

            Assert.Equal(recognized, SvgRendererConfiguration.TryParse(value, out _));
            Assert.Contains("FirstParty", description, StringComparison.Ordinal);
            Assert.DoesNotContain(value, description, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(
                description.Length,
                1,
                SvgRendererConfiguration.MaxConfigurationDiagnosticChars);
        }

        [Fact]
        public void ConfigurationDiagnostics_CollapseUnknownReasonCodes()
        {
            string hostile = new string('c', 4096);

            string description = SvgRendererConfiguration.DescribeSelection(hostile);

            Assert.Contains(
                SvgRendererConfiguration.UnknownDiagnosticReasonCode,
                description,
                StringComparison.Ordinal);
            Assert.DoesNotContain("cccc", description, StringComparison.Ordinal);
            Assert.InRange(
                description.Length,
                1,
                SvgRendererConfiguration.MaxConfigurationDiagnosticChars);
            Assert.Equal(
                "FirstParty",
                SvgRendererConfiguration.DescribeSelection(null));
        }

        [Theory]
        [InlineData("first-party")]
        [InlineData("fen")]
        [InlineData("FIRST-PARTY")]
        [InlineData("legacy")]
        [InlineData("svg-skia")]
        [InlineData("LEGACY")]
        [InlineData("  fen  ")]
        public void FirstPartyAndDeprecatedLegacyAliases_ResolveToFirstParty(string value)
        {
            Assert.True(SvgRendererConfiguration.TryParse(value, out var backend));
            Assert.Equal(SvgRendererBackend.FirstParty, backend);
            Assert.IsType<FenSvgRenderer>(SvgRendererFactory.GetRenderer(backend));
        }

        [Fact]
        public void FirstPartyRenderer_RejectsExternalResourceWithoutAmbientIo()
        {
            var limits = SvgRenderLimits.Default;
            limits.AllowExternalReferences = true;
            using var result = _renderer.Render(new SvgRenderRequest(
                "<svg width='10' height='10'><image href='https://example.invalid/image.png'/></svg>",
                limits));

            AssertFailClosedWithResourceRejection(result);
            Assert.Contains("resource-context-missing", result.ResourceRejectionReasonCodes);
        }

        [Theory]
        [InlineData(@"<svg width='10' height='10'><rect style='fill: u\72l(https://example.invalid/a.png)' width='10' height='10'/></svg>")]
        [InlineData(@"<svg width='10' height='10'><rect style='fill: url/**/(https://example.invalid/a.png)' width='10' height='10'/></svg>")]
        [InlineData(@"<svg width='10' height='10'><style>rect{fill:u\72l(https://example.invalid/a.png)}</style><rect width='10' height='10'/></svg>")]
        [InlineData(@"<svg width='10' height='10'><style>rect{fill:url/**/(https://example.invalid/a.png)}</style><rect width='10' height='10'/></svg>")]
        public void FirstPartyRenderer_RejectsEscapedOrCommentedCssResourceDuringPreScan(string source)
        {
            using var result = _renderer.Render(source);

            AssertFailClosedWithResourceRejection(result);
            Assert.Contains("external-resource", result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void FirstPartyRenderer_RejectsNestedSvgDataUri()
        {
            const string nested =
                "<svg width='10' height='10'><image href='https://example.invalid/a.png'/></svg>";
            string uri = "data:image/svg+xml;base64," +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(nested));

            using var result = _renderer.Render(
                $"<svg width='10' height='10'><image href='{uri}' width='10' height='10'/></svg>");

            AssertFailClosedWithResourceRejection(result);
            Assert.Contains("resource-rejected", result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void FirstPartyRenderer_RejectsSvgPayloadDisguisedAsRaster()
        {
            const string nested =
                "<svg width='10' height='10'><rect width='10' height='10'/></svg>";
            string uri = "data:image/png;base64," +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(nested));

            using var result = _renderer.Render(
                $"<svg width='10' height='10'><image href='{uri}' width='10' height='10'/></svg>");

            AssertFailClosedWithResourceRejection(result);
            Assert.Contains("resource-rejected", result.ResourceRejectionReasonCodes);
            Assert.DoesNotContain("invalid-resource", result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void FirstPartyRenderer_ReferenceDepthBudget_FailsClosed()
        {
            // The budget is not a warning: a browser expands the whole chain, so a
            // frame missing the instances past the budget is a frame a browser does
            // not produce, and success would certify it.
            string source = BuildAcyclicReferenceChain(2048);
            var limits = new SvgRenderLimits
            {
                MaxReferenceDepth = 64,
                MaxRenderTimeMs = 30000
            };

            using var result = _renderer.Render(source, limits);

            AssertFailClosedWithFallback(result);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("use reference depth budget exceeded",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains("admission-budget", result.FallbackReasonCodes);
        }

        [Fact]
        public void FirstPartyRenderer_UseChainWithinReferenceBudget_EmitsNoBudgetWarning()
        {
            string source = BuildAcyclicReferenceChain(8);
            var limits = new SvgRenderLimits
            {
                MaxReferenceDepth = 64,
                MaxRenderTimeMs = 30000
            };

            using var result = _renderer.Render(source, limits);

            AssertAdmissible(result);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void FirstPartyRenderer_ReferenceCycle_FailsClosed()
        {
            // A cycle is refused by the browser as well, so painting the rest of the
            // frame and reporting it as settled would certify a frame with a hole
            // where the cyclic instance belongs.
            string source = BuildCyclicReferenceChain(64);
            var limits = new SvgRenderLimits
            {
                MaxReferenceDepth = 64,
                MaxRenderTimeMs = 30000
            };

            using var result = _renderer.Render(source, limits);

            AssertFailClosedWithFallback(result);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("use reference cycle", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("reference-resolution", result.FallbackReasonCodes);
            Assert.All(result.Warnings,
                warning => Assert.True(warning.Length <= FenSvgRenderer.MaxResultDiagnosticChars));
        }

        [Fact]
        public void FirstPartyRenderer_IgnoresNavigationHref()
        {
            using var result = _renderer.Render(
                "<svg width='10' height='10'><a href='https://example.invalid/page'>" +
                "<rect width='10' height='10' fill='red'/></a></svg>");

            AssertAdmissible(result);
            Assert.Empty(result.ResourceRejectionReasonCodes);
            Assert.True(BitmapHasVisiblePixels(result.Bitmap));
        }

        [Fact]
        public void FirstPartyRenderer_RejectsDoctype()
        {
            using var result = _renderer.Render("<!DOCTYPE svg><svg width='10' height='10'/>");

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Contains("DOCTYPE", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("<svg width='10' height='10'><rect width='5'")]
        [InlineData("<svg width='10' height='10'><rect width='5' height='5' fill='red'><unclosed></rect></svg>")]
        public void FirstPartyParser_RecoversFromUnclosedMarkupAndRenders(string source)
        {
            using var result = _renderer.Render(source);

            AssertAdmissible(result);
        }

        [Fact]
        public void FirstPartyParser_ReportsDuplicateAttributeAsABoundedWarning()
        {
            using var result = _renderer.Render(
                "<svg width='10' height='10'><rect fill='red' fill='blue'/></svg>");

            AssertAdmissible(result);
            Assert.Contains(result.Warnings, warning =>
                warning.StartsWith("duplicate attribute '", StringComparison.Ordinal));
        }

        [Fact]
        public void FirstPartyRenderer_RejectsDeclaredRasterLimit()
        {
            using var result = _renderer.Render(
                "<svg width='100' height='100'><rect width='100' height='100'/></svg>",
                new SvgRenderLimits
                {
                    MaxRasterWidth = 10,
                    MaxRasterHeight = 10,
                    MaxRasterPixels = 100
                });

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Contains("exceed browser limits", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FirstPartyRenderer_PreservesValidInlineCss()
        {
            const string source =
                "<svg width='10' height='10'><path style='fill: red' d='M0 0h10v10H0z'/></svg>";

            using var result = _renderer.Render(source);

            AssertAdmissible(result);
            Assert.True(result.Bitmap.GetPixel(5, 5).Red > 200);
        }

        [Fact]
        public void NullRequest_FailsClosed()
        {
            using var result = _renderer.Render((SvgRenderRequest)null!);

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void NoBackendOtherThanFirstPartyIsDeclared()
        {
            Assert.Equal(new[] { "FirstParty" }, Enum.GetNames<SvgRendererBackend>());
        }

        [Fact]
        public void DeprecatedLegacyEnvironmentAliases_ArePublishedForDeployments()
        {
            Assert.Equal("legacy", SvgRendererConfiguration.DeprecatedLegacyValue);
            Assert.Equal("svg-skia", SvgRendererConfiguration.DeprecatedLegacyAliasValue);
            Assert.Equal("first-party", SvgRendererConfiguration.FirstPartyValue);
            Assert.Equal("fen", SvgRendererConfiguration.FirstPartyAliasValue);
            Assert.Equal("FEN_SVG_RENDERER", SvgRendererConfiguration.EnvironmentVariable);
        }

        private static void AssertAdmissible(SvgRenderResult result)
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(result.HadResourceRejection);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Empty(result.ResourceRejectionReasonCodes);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.Null(SvgRenderResult.DescribeRejection(result));
        }

        private static void AssertBoundedRejection(SvgRenderResult result)
        {
            string reason = SvgRenderResult.DescribeRejection(result);

            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.True(
                reason.Length <=
                    SvgRenderResult.MaxRejectionDiagnosticChars + 3,
                $"rejection reason was {reason.Length} chars");
        }

        private static void AssertFailClosedWithFallback(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.FallbackReasonCodes);
            Assert.False(result.HadResourceRejection);
            Assert.Empty(result.ResourceRejectionReasonCodes);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        private static void AssertFailClosedWithResourceRejection(SvgRenderResult result)
        {
            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.True(result.HadResourceRejection);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.Contains("resource", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildAcyclicReferenceChain(int length)
        {
            var source = new StringBuilder("<svg width='1' height='1'>");
            for (int i = 0; i < length; i++)
            {
                source.Append("<use id='n").Append(i)
                    .Append("' href='#n").Append(i + 1).Append("'/>");
            }
            source.Append("<rect id='n").Append(length)
                .Append("' width='1' height='1'/></svg>");
            return source.ToString();
        }

        private static string BuildCyclicReferenceChain(int length)
        {
            var source = new StringBuilder("<svg width='1' height='1'>");
            for (int i = 0; i < length; i++)
            {
                int next = (i + 1) % length;
                source.Append("<use id='n").Append(i)
                    .Append("' href='#n").Append(next).Append("'/>");
            }
            return source.Append("</svg>").ToString();
        }

        private static bool BitmapHasVisiblePixels(SKBitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
