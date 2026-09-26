using System;
using System.Buffers.Binary;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    [Collection(SvgRendererBackendStateCollection.Name)]
    public sealed class SvgProductionHardeningTests
    {
        [Fact]
        public void EmbeddedRaster_HeaderBudgetRejectsBeforePixelAllocation()
        {
            var encoded = BuildPngHeader(width: 50_000, height: 50_000);

            var decoded = SvgRenderEngine.TryDecodeEmbeddedBitmap(
                encoded,
                maxPixels: 16L * 1024 * 1024,
                maxDimension: 8192,
                out var bitmap,
                out var error);

            Assert.False(decoded);
            Assert.Null(bitmap);
            Assert.Equal("image decoded size exceeds raster budget; rejected", error);
        }

        [Fact]
        public void EmbeddedRaster_ValidPayloadDecodesWithinBudget()
        {
            using var source = new SKBitmap(2, 3);
            source.Erase(SKColors.CornflowerBlue);
            using var image = SKImage.FromBitmap(source);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            var decoded = SvgRenderEngine.TryDecodeEmbeddedBitmap(
                data.ToArray(),
                maxPixels: 64,
                maxDimension: 8,
                out var bitmap,
                out var error);

            Assert.True(decoded, error);
            using (bitmap)
            {
                Assert.Equal(2, bitmap.Width);
                Assert.Equal(3, bitmap.Height);
                Assert.Equal(SKColors.CornflowerBlue, bitmap.GetPixel(1, 1));
            }
        }

        [Fact]
        public void RenderResult_DetachTransfersBitmapOwnership()
        {
            using var result = new SvgRenderResult { Bitmap = new SKBitmap(1, 1) };
            using var detached = result.DetachBitmap();

            Assert.NotNull(detached);
            Assert.Null(result.Bitmap);
        }

        [Fact]
        public void RenderResult_DisposeIsIdempotent()
        {
            using var recorder = new SKPictureRecorder();
            recorder.BeginRecording(new SKRect(0, 0, 1, 1));
            var result = new SvgRenderResult
            {
                Bitmap = new SKBitmap(1, 1),
                Picture = recorder.EndRecording()
            };

            result.Dispose();
            result.Dispose();

            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
        }

        [Fact]
        public void RenderLimits_NormalizeClampsUntrustedCallerValuesToHardCaps()
        {
            var limits = SvgRenderLimits.Normalize(new SvgRenderLimits
            {
                MaxRecursionDepth = int.MaxValue,
                MaxFilterCount = int.MaxValue,
                MaxRenderTimeMs = int.MaxValue,
                MaxElementCount = int.MaxValue,
                MaxSourceChars = int.MaxValue,
                MaxRasterWidth = int.MaxValue,
                MaxRasterHeight = int.MaxValue,
                MaxRasterPixels = long.MaxValue,
                MaxDecodedImagePixels = long.MaxValue,
                MaxDecodedImageBytes = int.MaxValue,
                MaxCumulativeResourceBytes = int.MaxValue,
                MaxResourceCount = int.MaxValue,
                MaxActiveLayers = int.MaxValue,
                MaxReferenceDepth = int.MaxValue,
                AllowExternalReferences = true
            });

            Assert.Equal(512, limits.MaxRecursionDepth);
            Assert.Equal(1_000, limits.MaxFilterCount);
            Assert.Equal(30_000, limits.MaxRenderTimeMs);
            Assert.Equal(250_000, limits.MaxElementCount);
            Assert.Equal(32 * 1024 * 1024, limits.MaxSourceChars);
            Assert.Equal(32_768, limits.MaxRasterWidth);
            Assert.Equal(32_768, limits.MaxRasterHeight);
            Assert.Equal(64L * 1024 * 1024, limits.MaxRasterPixels);
            Assert.Equal(64L * 1024 * 1024, limits.MaxDecodedImagePixels);
            Assert.Equal(32 * 1024 * 1024, limits.MaxDecodedImageBytes);
            Assert.Equal(64 * 1024 * 1024, limits.MaxCumulativeResourceBytes);
            Assert.Equal(512, limits.MaxResourceCount);
            Assert.Equal(16, limits.MaxActiveLayers);
            Assert.Equal(64, limits.MaxReferenceDepth);
            Assert.True(limits.AllowExternalReferences);
        }

        [Theory]
        [InlineData("first-party", SvgRendererBackend.FirstParty)]
        [InlineData("fen", SvgRendererBackend.FirstParty)]
        [InlineData("legacy", SvgRendererBackend.FirstParty)]
        [InlineData("svg-skia", SvgRendererBackend.FirstParty)]
        public void BackendConfiguration_ParsesCrossPlatformValues(
            string value,
            SvgRendererBackend expected)
        {
            Assert.True(SvgRendererConfiguration.TryParse(value, out var parsed));
            Assert.Equal(expected, parsed);
        }

        [Theory]
        [InlineData("hybrid")]
        [InlineData("AUTO")]
        [InlineData("not-a-backend")]
        public void BackendConfiguration_RetiredOrUnrecognizedValuesFailClosedOntoFirstParty(
            string value)
        {
            Assert.False(SvgRendererConfiguration.TryParse(value, out var parsed));
            Assert.Equal(SvgRendererBackend.FirstParty, parsed);
        }

        [Fact]
        public void FirstParty_BasicTextRendersWithoutFallback()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='20'><text x='2' y='15'>Fen</text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.True(HasForeground(result.Bitmap));
        }

        [Fact]
        public void FirstParty_TextEntitiesAndAnchorRenderWithinExpectedRegion()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='80' height='30'><text x='40' y='22' text-anchor='middle' " +
                "font-size='18' fill='red'>A&amp;B</text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(HasForegroundIn(result.Bitmap, 10, 4, 70, 28));
        }

        [Fact]
        public void FirstParty_TextPercentPositionAndLastInlineDeclarationAreHonored()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='40'><text x='50%' y='75%' " +
                "style='font-size:8px;font-size:20px' text-anchor='middle'>Fen</text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(HasForegroundIn(result.Bitmap, 25, 8, 75, 36));
        }

        [Fact]
        public void FirstParty_VerticalTextFailsClosedAsUnsupported()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='80' height='30'><text x='2' y='15' writing-mode='vertical-rl'>Fen</text></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.FallbackReasonCodes);
            Assert.NotEmpty(result.Warnings);
        }

        [Theory]
        [InlineData("Καλημέρα")]
        [InlineData("مرحبا")]
        [InlineData("שלום")]
        public void FirstParty_ComplexScriptTextUsesShapedGlyphRuns(string text)
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='160' height='40'><text x='4' y='30' font-size='24'>{text}</text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(HasForeground(result.Bitmap));
        }

        [Fact]
        public void FirstParty_TspanOrderStylesAndLetterSpacingAreRendered()
        {
            const string svg =
                "<svg width='160' height='40'><text x='4' y='30' font-size='24' letter-spacing='1'>" +
                "A<tspan fill='red' dx='2'>B</tspan>C</text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(HasForeground(result.Bitmap));
            Assert.True(HasColorIn(result.Bitmap, SKColors.Red, 0, 0, 160, 40));
        }

        [Fact]
        public void FirstParty_OversizedBasicTextFailsClosedBeforeFontWork()
        {
            string content = new string('a', 4097);
            using var result = new FenSvgRenderer().Render(
                $"<svg width='80' height='30'><text x='2' y='15'>{content}</text></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("render length budget", StringComparison.Ordinal));
        }

        [Fact]
        public void SiblingShapeOpacity_ReleasesLayerBudgetAfterEachElement()
        {
            var svg = new System.Text.StringBuilder("<svg width='64' height='8'>");
            for (int i = 0; i < 16; i++)
                svg.Append($"<rect x='{i * 4}' width='4' height='8' opacity='.5'/>");
            svg.Append("</svg>");

            using var result = new FenSvgRenderer().Render(svg.ToString());

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
        }

        [Fact]
        public void FirstParty_SupportedGeometryRendersAuthoritatively()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='10' height='10'><rect width='10' height='10' fill='red'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.False(result.HadResourceRejection);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.True(SvgRenderResult.IsAdmissible(result));
        }

        [Fact]
        public void FirstParty_SecurityFailureFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<!DOCTYPE svg><svg width='10' height='10'><rect width='10' height='10'/></svg>");

            AssertFailsClosed(result);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.Contains("DOCTYPE", result.ErrorMessage, StringComparison.Ordinal);
        }

        [Fact]
        public void FirstParty_ExternalImageRejectionFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='10' height='10'><image href='https://invalid.example/image.png' " +
                "width='10' height='10'/></svg>",
                SvgRenderLimits.Strict);

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
        }

        [Fact]
        public void FirstParty_LayerBudgetExhaustionFailsClosedInsteadOfFidelityLoss()
        {
            var svg = new System.Text.StringBuilder("<svg width='20' height='20'>");
            for (int i = 0; i < 10; i++) svg.Append("<g opacity='.9'>");
            svg.Append("<rect width='20' height='20' fill='red'/>");
            for (int i = 0; i < 10; i++) svg.Append("</g>");
            svg.Append("</svg>");

            using var result = new FenSvgRenderer().Render(svg.ToString());

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.FallbackReasonCodes);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
        }

        [Fact]
        public void SharedFirstPartyRenderer_IsSafeUnderConcurrentMixedWorkload()
        {
            var renderer = SvgRendererFactory.GetRenderer(SvgRendererBackend.FirstParty);
            var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();

            System.Threading.Tasks.Parallel.For(0, 64, index =>
            {
                string svg = index % 2 == 0
                    ? "<svg width='8' height='8'><rect width='8' height='8' fill='lime'/></svg>"
                    : "<svg width='40' height='16'><text x='1' y='12'>Fen</text></svg>";
                using var result = renderer.Render(svg);
                if (!result.Success || result.Bitmap == null || result.Bitmap.Width <= 0)
                {
                    failures.Enqueue(result.ErrorMessage ?? "missing bitmap");
                }
            });

            Assert.Empty(failures);
        }

        [Theory]
        [InlineData("<rect width='10' height='10' style='mask:url(#m)'/>")]
        [InlineData("<path d='M0 0L10 10' marker-end='url(#m)'/>")]
        public void FirstParty_PartiallySupportedFeaturesFailClosedRatherThanDropSilently(string content)
        {
            using var result = new FenSvgRenderer().Render($"<svg width='10' height='10'>{content}</svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ObjectBoundingBoxClip_MapsUnitGeometryToShapeBounds()
        {
            const string svg =
                "<svg width='40' height='20'><defs>" +
                "<clipPath id='c' clipPathUnits='objectBoundingBox'>" +
                "<rect x='0' y='0' width='.5' height='1'/></clipPath></defs>" +
                "<rect x='10' y='5' width='20' height='10' fill='red' clip-path='url(#c)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(14, 10).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(26, 10).Alpha);
        }

        [Fact]
        public void ClipPath_ChildTransformIsApplied()
        {
            const string svg =
                "<svg width='30' height='15'><defs><clipPath id='c'>" +
                "<rect width='8' height='10' transform='translate(10 0)'/>" +
                "</clipPath></defs><rect width='30' height='15' fill='red' clip-path='url(#c)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(4, 5).Alpha);
            Assert.True(result.Bitmap.GetPixel(12, 5).Alpha > 0);
        }

        [Fact]
        public void EmbeddedImage_DefaultAspectRatioMeetsAndCenters()
        {
            string imageUri = MakeSolidPngDataUri(4, 2, SKColors.Red);
            string svg = $"<svg width='20' height='20'><image href='{imageUri}' width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(10, 2).Alpha);
            Assert.True(result.Bitmap.GetPixel(10, 10).Alpha > 0);
        }

        [Fact]
        public void EmbeddedImage_AspectRatioNoneStretchesToViewport()
        {
            string imageUri = MakeSolidPngDataUri(4, 2, SKColors.Red);
            string svg = $"<svg width='20' height='20'><image href='{imageUri}' width='20' height='20' preserveAspectRatio='none'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(10, 2).Alpha > 0);
            Assert.True(result.Bitmap.GetPixel(10, 18).Alpha > 0);
        }

        [Fact]
        public void EmbeddedSvgImage_RendersFirstPartyWithIsolatedNestedStyles()
        {
            string nested = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("<svg width='2' height='2'><rect width='2' height='2'/></svg>"));
            string svg =
                $"<svg width='10' height='10'><style>rect{{fill:red}}</style>" +
                $"<image href='data:image/svg+xml;base64,{nested}' width='10' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.False(result.HadResourceRejection);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.Equal(SKColors.Black, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public void EmbeddedSvgImage_FractionalScaleDoesNotExposeBackgroundAtSourceEdge()
        {
            string nested = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(
                    "<svg width='100' height='100' preserveAspectRatio='xMinYMin'>" +
                    "<rect width='100' height='100' fill='green'/></svg>"));
            string svg =
                "<svg width='100' height='100'><rect width='100' height='100' fill='red'/>" +
                "<defs><clipPath id='c'><rect width='95' height='100'/></clipPath></defs>" +
                $"<g clip-path='url(#c)'><image href='data:image/svg+xml;base64,{nested}' " +
                "width='101.2' height='100'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(0, 50));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(94, 50));
        }

        [Fact]
        public void EmbeddedRasterBudgetRejection_FailsClosed()
        {
            string payload = Convert.ToBase64String(BuildPngHeader(width: 50_000, height: 50_000));
            using var result = new FenSvgRenderer().Render(
                $"<svg width='10' height='10'><image href='data:image/png;base64,{payload}' width='10' height='10'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.False(result.RequiresFallback);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void PercentageRect_ResolvesAgainstCurrentViewport()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='20'><rect x='25%' y='25%' width='50%' height='50%' fill='red'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(5, 10).Alpha);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(20, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(35, 10).Alpha);
        }

        [Fact]
        public void PercentageRect_WithViewBoxUsesUserSpaceViewport()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='200' height='100' viewBox='0 0 100 50'>" +
                "<rect x='25%' y='20%' width='50%' height='60%' fill='lime'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(25, 50).Alpha);
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(100, 50));
            Assert.Equal(0, result.Bitmap.GetPixel(175, 50).Alpha);
        }

        [Fact]
        public void NestedGroups_PercentageRectsRemainVisible()
        {
            const string svg =
                "<svg width='200' height='200'><rect width='100%' height='100%' fill='DarkGreen'/>" +
                "<g transform='translate(20,20)'><rect width='100%' height='100%' fill='LightSeaGreen'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(10, 10).Alpha > 0);
            Assert.True(result.Bitmap.GetPixel(100, 100).Alpha > 0);
        }

        [Fact]
        public void MoveCommand_AdditionalPairsRenderAsLineSegments()
        {
            const string svg =
                "<svg width='20' height='20'><path d='M2 2 18 18' stroke='red' stroke-width='2' fill='none'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(10, 10).Alpha > 0);
        }

        [Fact]
        public void RootDisplayOpacityAndExplicitZeroDimensions_ArePreserved()
        {
            using var hidden = new FenSvgRenderer().Render(
                "<svg width='20' height='20' display='none'><rect width='20' height='20' fill='red'/></svg>");
            using var faded = new FenSvgRenderer().Render(
                "<svg width='20' height='20' opacity='.5'><rect width='20' height='20' fill='red'/></svg>");
            using var zero = new FenSvgRenderer().Render(
                "<svg width='0' height='0'><rect width='20' height='20' fill='red'/></svg>");

            Assert.True(hidden.Success, hidden.ErrorMessage);
            Assert.Equal(0, hidden.Bitmap.GetPixel(10, 10).Alpha);
            Assert.True(faded.Success, faded.ErrorMessage);
            Assert.InRange(faded.Bitmap.GetPixel(10, 10).Alpha, 100, 155);
            Assert.True(zero.Success, zero.ErrorMessage);
            Assert.True(zero.Width < 10f);
            Assert.Equal(0, zero.Bitmap.GetPixel(0, 0).Alpha);
        }

        [Fact]
        public void RootClipFilterAndMaskApplyThroughTheRootEffectPipeline()
        {
            using var clip = new FenSvgRenderer().Render(
                "<svg width='20' height='20' clip-path='url(#c)'><defs>" +
                "<clipPath id='c'><rect width='10' height='20'/></clipPath></defs>" +
                "<rect width='20' height='20' fill='red'/></svg>");
            using var filter = new FenSvgRenderer().Render(
                "<svg width='40' height='40' filter='url(#f)'><defs><filter id='f' " +
                "filterUnits='userSpaceOnUse' x='0' y='0' width='40' height='40'>" +
                "<feOffset dx='10'/></filter></defs>" +
                "<rect x='5' y='5' width='10' height='10' fill='red'/></svg>");
            using var mask = new FenSvgRenderer().Render(
                "<svg width='40' height='20' mask='url(#m)'><defs><mask id='m' " +
                "maskUnits='userSpaceOnUse' x='0' y='0' width='20' height='20'>" +
                "<rect width='20' height='20' fill='white'/></mask></defs>" +
                "<rect width='40' height='20' fill='red'/></svg>");

            Assert.True(clip.Success, clip.ErrorMessage);
            Assert.False(clip.RequiresFallback);
            Assert.True(clip.Bitmap.GetPixel(5, 10).Alpha > 0);
            Assert.Equal(0, clip.Bitmap.GetPixel(15, 10).Alpha);
            Assert.True(filter.Success, filter.ErrorMessage);
            Assert.False(filter.RequiresFallback);
            Assert.Equal(0, filter.Bitmap.GetPixel(7, 7).Alpha);
            Assert.True(filter.Bitmap.GetPixel(17, 7).Red > 200);
            Assert.True(mask.Success, mask.ErrorMessage);
            Assert.False(mask.RequiresFallback);
            Assert.True(mask.Bitmap.GetPixel(10, 10).Alpha > 0);
            Assert.Equal(0, mask.Bitmap.GetPixel(30, 10).Alpha);
        }

        [Fact]
        public void RootOpacityAndBlendUseBoundedEffectHandling()
        {
            using var budgeted = new FenSvgRenderer().Render(
                "<svg width='20' height='20' opacity='.5' filter='url(#f)'><defs>" +
                "<filter id='f'><feOffset dx='1'/></filter></defs>" +
                "<rect width='20' height='20' fill='red'/></svg>",
                new SvgRenderLimits { MaxActiveLayers = 1 });
            using var invalidBlend = new FenSvgRenderer().Render(
                "<svg width='20' height='20' style='mix-blend-mode:plus-lighter'>" +
                "<rect width='20' height='20' fill='red'/></svg>");

            AssertFailsClosed(budgeted);
            Assert.True(budgeted.RequiresFallback);
            Assert.Contains(budgeted.Warnings, warning => warning.Contains("layer budget", StringComparison.Ordinal));
            AssertFailsClosed(invalidBlend);
            Assert.True(invalidBlend.RequiresFallback);
        }

        [Fact]
        public void ZeroDimensionRootSkipsAuthorEffectsWithoutChangingSize()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='0' height='0' opacity='.5' clip-path='url(#c)' " +
                "filter='url(#missing)' mask='url(#missing)' style='mix-blend-mode:multiply'>" +
                "<rect width='20' height='20' fill='red'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(result.Width < 10f);
            Assert.Equal(0, result.Bitmap.GetPixel(0, 0).Alpha);
        }

        [Fact]
        public void NestedSvgClipUsesViewBoxTransformOnce()
        {
            const string svg =
                "<svg width='40' height='20'><defs><clipPath id='c'>" +
                "<rect width='6' height='20'/></clipPath></defs>" +
                "<svg x='5' width='20' height='20' viewBox='0 0 10 10' " +
                "preserveAspectRatio='none' transform='translate(3 0)' clip-path='url(#c)'>" +
                "<rect width='10' height='10' fill='red'/></svg></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(0, result.Bitmap.GetPixel(7, 10).Alpha);
            Assert.True(result.Bitmap.GetPixel(8, 10).Alpha > 0);
            Assert.True(result.Bitmap.GetPixel(19, 10).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(20, 10).Alpha);
        }

        [Fact]
        public void NestedSvgUsesXyTranslation()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='20'><svg x='10' y='5' width='10' height='10'>" +
                "<rect width='10' height='10' fill='red'/></svg></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(15, 10).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(5, 5).Alpha);
        }

        [Fact]
        public void SwitchAppliesContainerStyleClipOpacityAndTransform()
        {
            const string svg =
                "<svg width='30' height='20'><defs><clipPath id='c'>" +
                "<rect width='10' height='10'/></clipPath></defs>" +
                "<switch transform='translate(5 5)' opacity='.5' fill='red' clip-path='url(#c)'>" +
                "<rect width='20' height='20'/></switch></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.InRange(result.Bitmap.GetPixel(7, 7).Alpha, 100, 155);
            Assert.Equal(0, result.Bitmap.GetPixel(17, 17).Alpha);
        }

        [Fact]
        public void RequiredFeaturesSelectSupportedConditionalBranches()
        {
            const string svg =
                "<svg width='20' height='20'><switch>" +
                "<rect width='20' height='20' fill='red' requiredFeatures='invalid'/>" +
                "<rect width='20' height='20' fill='green' " +
                "requiredFeatures='http://www.w3.org/TR/SVG11/feature#ConditionalProcessing'/>" +
                "</switch></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void InvalidCssWinnersFallBackToPresentationOrInheritedPaint()
        {
            const string svg =
                "<svg width='20' height='20'><g fill='blue'>" +
                "<rect width='10' height='20' fill='red' style='fill:not-a-color'/>" +
                "<rect x='10' width='10' height='20' style='fill:not-a-color'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 10));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(15, 10));
        }

        [Fact]
        public void TinyViewBoxAndOverflowingTransformRemainFinite()
        {
            using var tiny = new FenSvgRenderer().Render(
                "<svg width='20' height='20' viewBox='0 0 1e-30 1'>" +
                "<rect width='1' height='1' fill='red'/></svg>");
            using var overflow = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><rect width='4' height='4' fill='red' " +
                "transform='scale(1e308)'/></svg>");

            AssertFailsClosed(tiny);
            Assert.True(tiny.RequiresFallback);
            Assert.NotEmpty(tiny.FallbackReasonCodes);
            AssertFailsClosed(overflow);
            Assert.True(overflow.RequiresFallback);
        }

        [Fact]
        public void UseCssCoordinatesAndImageDimensionsAreValidated()
        {
            string image = MakeSolidPngDataUri(2, 2, SKColors.Red);
            const string useSvg =
                "<svg width='30' height='20'><defs><rect id='r' width='4' height='4' fill='red'/></defs>" +
                "<use href='#r' style='x:10px;y:5px'/></svg>";
            string imageSvg =
                $"<svg width='20' height='20'><image href='{image}' " +
                "style='x:2px;y:3px;width:6px;height:4px'/></svg>";
            string invalidImageSvg =
                $"<svg width='20' height='20'><image href='{image}' width='-1' height='4'/></svg>";

            using var useResult = new FenSvgRenderer().Render(useSvg);
            using var imageResult = new FenSvgRenderer().Render(imageSvg);
            using var invalidResult = new FenSvgRenderer().Render(invalidImageSvg);

            Assert.True(useResult.Success, useResult.ErrorMessage);
            Assert.True(useResult.Bitmap.GetPixel(12, 7).Alpha > 0);
            Assert.True(imageResult.Success, imageResult.ErrorMessage);
            Assert.True(imageResult.Bitmap.GetPixel(5, 5).Alpha > 0);
            Assert.True(invalidResult.Success, invalidResult.ErrorMessage);
            Assert.Equal(0, invalidResult.Bitmap.GetPixel(0, 0).Alpha);
        }

        [Fact]
        public void ClipPathInheritsClipRuleFromItsDefinition()
        {
            const string svg =
                "<svg width='20' height='20'><defs><clipPath id='c' clip-rule='evenodd'>" +
                "<path d='M0 0H20V20H0Z'/><path d='M5 5H15V15H5Z'/>" +
                "</clipPath></defs><rect width='20' height='20' fill='red' " +
                "clip-path='url(#c)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(2, 2).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void NamespacePrefixedRootWithScriptLikeElementFailsClosedAsDynamicContent()
        {
            const string svg =
                "<svg:svg xmlns:svg='http://www.w3.org/2000/svg' width='20' height='20'>" +
                "<svg:defs><svg:clipPath id='base'><svg:rect width='10' height='10'/>" +
                "</svg:clipPath><svg:clipPath id='derived' href='#base'/></svg:defs>" +
                "<SCRIPT>ignored</SCRIPT><svg:rect width='20' height='20' fill='red' " +
                "clip-path='url(#derived)'/></svg:svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
        }

        [Fact]
        public void NamespacePrefixedRootAndClipHrefRenderWithoutScript()
        {
            const string svg =
                "<svg:svg xmlns:svg='http://www.w3.org/2000/svg' width='20' height='20'>" +
                "<svg:defs><svg:clipPath id='base'><svg:rect width='10' height='10'/>" +
                "</svg:clipPath><svg:clipPath id='derived' href='#base'/></svg:defs>" +
                "<svg:rect width='20' height='20' fill='red' clip-path='url(#derived)'/></svg:svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(result.Bitmap.GetPixel(5, 5).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(15, 15).Alpha);
        }

        [Fact]
        public void FirstParty_OversizedRootNameFatalDiagnosticStaysBounded()
        {
            string rootName = new string('z', 200_000);

            using var result = new FenSvgRenderer().Render($"<{rootName}/>");

            Assert.False(result.Success);
            Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
            Assert.InRange(result.ErrorMessage.Length, 1, FenSvgRenderer.MaxResultDiagnosticChars);
            Assert.DoesNotContain(rootName, result.ErrorMessage, StringComparison.Ordinal);
        }

        [Fact]
        public void FirstParty_OversizedDuplicateAttributeWarningStaysBounded()
        {
            string attributeName = new string('q', 2_000);
            string svg =
                $"<svg width='4' height='4' {attributeName}='1' {attributeName}='2'>" +
                "<rect width='4' height='4'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.InRange(result.Warnings.Count, 1, FenSvgRenderer.MaxResultDiagnosticEntries);
            Assert.Contains(result.Warnings, warning =>
                warning.StartsWith("duplicate attribute '", StringComparison.Ordinal));
            Assert.All(result.Warnings, warning =>
                Assert.InRange(warning.Length, 1, SvgDiagnosticText.MaxIdentifierChars + 64));
            Assert.DoesNotContain(attributeName, string.Join("|", result.Warnings), StringComparison.Ordinal);
        }

        [Fact]
        public void FirstParty_OversizedDuplicateIdWarningAndReasonCodesStayBounded()
        {
            string id = new string('i', 400);
            string svg =
                $"<svg width='4' height='4'><rect id='{id}' width='2' height='2'/>" +
                $"<rect id='{id}' width='2' height='2'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains(result.Warnings, warning =>
                warning.StartsWith("duplicate id '", StringComparison.Ordinal));
            Assert.All(result.Warnings, warning =>
                Assert.InRange(warning.Length, 1, SvgDiagnosticText.MaxIdentifierChars + 64));
            Assert.All(result.FallbackReasonCodes, code =>
                Assert.InRange(code.Length, 1, FenSvgRenderer.MaxResultDiagnosticChars));
            Assert.All(result.ResourceRejectionReasonCodes, code =>
                Assert.InRange(code.Length, 1, FenSvgRenderer.MaxResultDiagnosticChars));
        }

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        private static string MakeSolidPngDataUri(int width, int height, SKColor color)
        {
            using var bitmap = new SKBitmap(width, height);
            bitmap.Erase(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }

        private static bool HasForeground(SKBitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0) return true;
            return false;
        }

        private static bool HasForegroundIn(SKBitmap bitmap, int left, int top, int right, int bottom)
        {
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0) return true;
            return false;
        }

        private static bool HasColorIn(SKBitmap bitmap, SKColor expected, int left, int top, int right, int bottom)
        {
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var actual = bitmap.GetPixel(x, y);
                if (actual.Alpha > 0 &&
                    Math.Abs(actual.Red - expected.Red) <= 8 &&
                    Math.Abs(actual.Green - expected.Green) <= 8 &&
                    Math.Abs(actual.Blue - expected.Blue) <= 8)
                {
                    return true;
                }
            }
            return false;
        }

        private static byte[] BuildPngHeader(int width, int height)
        {
            // Start with a valid PNG so the codec accepts the container, then
            // change only IHDR dimensions and its CRC. Pixel allocation must be
            // rejected from this metadata before the intentionally mismatched
            // compressed payload is ever decoded.
            using var bitmap = new SKBitmap(1, 1);
            bitmap.Erase(SKColors.Transparent);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var png = data.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), height);
            var crc = Crc32(png.AsSpan(12, 17));
            BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29, 4), crc);
            return png;
        }

        private static uint Crc32(ReadOnlySpan<byte> bytes)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = unchecked((uint)-(int)(crc & 1));
                    crc = (crc >> 1) ^ (0xEDB88320u & mask);
                }
            }
            return ~crc;
        }
    }
}
