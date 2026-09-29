using System.Collections.Generic;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// The first-party adapter is the sole complete-renderer backend, so it must
    /// never hand out partial pixels. A render that the engine reports as
    /// requiring fallback, as having rejected a resource, or as having left a
    /// resource unresolved must fail closed: Success=false with no Bitmap, no
    /// Picture, and bounded diagnostics.
    /// <para>
    /// Every case renders through the first-party adapter, so the class runs
    /// inside <see cref="SvgRendererBackendStateCollection"/> to keep the
    /// resource admission and backend result shape from being observed while
    /// another test mutates process-wide SVG/ImageLoader state.
    /// </para>
    /// </summary>
    [Collection(SvgRendererBackendStateCollection.Name)]
    public class FenSvgRendererPartialResultTests
    {
        // A 5000x5000 tile whose instance edge crosses the rect: too large to rasterize.
        private const string OversizedPatternTile =
            "<svg width='100' height='100'><pattern id='p' patternUnits='userSpaceOnUse' " +
            "x='-4950' width='5000' height='5000'>" +
            "<rect width='5000' height='5000' fill='red'/></pattern>" +
            "<rect width='100' height='100' fill='url(#p)'/></svg>";

        private const string UnsupportedElement =
            "<svg width='20' height='20'><foreignObject width='20' height='20'/></svg>";

        private const string ExternalImage =
            "<svg width='20' height='20'>" +
            "<image href='https://example.test/a.png' width='20' height='20'/></svg>";

        private const string MalformedDataUri =
            "<svg width='20' height='20'>" +
            "<image href='data:image/png;base64,%%%not-base64%%%' width='20' height='20'/></svg>";

        private const string PayloadlessDataUri =
            "<svg width='20' height='20'>" +
            "<image href='data:image/png;base64' width='20' height='20'/></svg>";

        private const string CompleteRedSquare =
            "<svg width='20' height='20'><rect width='20' height='20' fill='red'/></svg>";

        private readonly FenSvgRenderer _renderer = new FenSvgRenderer();

        [Fact]
        public void RequiresFallback_FailsClosedWithNoPixels()
        {
            using var result = _renderer.Render(OversizedPatternTile);

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.RequiresFallback);
            Assert.False(result.HadResourceRejection);
            Assert.NotEmpty(result.FallbackReasonCodes);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
        }

        [Fact]
        public void UnsupportedElement_FailsClosedWithNoPixels()
        {
            using var result = _renderer.Render(UnsupportedElement);

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.FallbackReasonCodes);
        }

        [Fact]
        public void ExternalResourceRejected_FailsClosedWithNoPixels()
        {
            using var result = _renderer.Render(ExternalImage);

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.HadResourceRejection);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
        }

        [Theory]
        [InlineData(MalformedDataUri)]
        [InlineData(PayloadlessDataUri)]
        public void UnresolvedEmbeddedResource_FailsClosedWithNoPixels(string svg)
        {
            using var result = _renderer.Render(svg);

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.HadResourceRejection);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void UnresolvedResourceWithoutResolver_FailsClosedWithNoPixels()
        {
            var limits = SvgRenderLimits.Default;
            limits.AllowExternalReferences = true;

            using var result = _renderer.Render(ExternalImage, limits);

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.True(result.HadResourceRejection);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void CompleteRender_StillReturnsOwnedBitmap()
        {
            using var result = _renderer.Render(CompleteRedSquare);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(20f, result.Width);
            Assert.Equal(20f, result.Height);
            Assert.False(result.RequiresFallback);
            Assert.False(result.HadResourceRejection);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Empty(result.ResourceRejectionReasonCodes);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void CompleteRender_IsAdmissible()
        {
            using var result = _renderer.Render(CompleteRedSquare);

            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.Null(SvgRenderResult.DescribeRejection(result));
        }

        [Theory]
        [InlineData(OversizedPatternTile)]
        [InlineData(UnsupportedElement)]
        [InlineData(ExternalImage)]
        [InlineData(MalformedDataUri)]
        [InlineData(PayloadlessDataUri)]
        [InlineData(CompleteRedSquare)]
        public void NeverReturnsPartialSuccess(string svg)
        {
            using var result = _renderer.Render(svg);

            if (result.Success)
            {
                Assert.True(SvgRenderResult.IsAdmissible(result),
                    "a successful result must be admissible");
                Assert.False(result.RequiresFallback);
                Assert.False(result.HadResourceRejection);
                Assert.NotNull(result.Bitmap);
                Assert.True(result.Width > 0f, $"expected positive width, got {result.Width}");
                Assert.True(result.Height > 0f, $"expected positive height, got {result.Height}");
            }
            else
            {
                Assert.True(result.Bitmap is null, "a failed result must not carry pixels");
                Assert.True(result.Picture is null, "a failed result must not carry a picture");
                Assert.Equal(0f, result.Width);
                Assert.Equal(0f, result.Height);
                Assert.False(SvgRenderResult.IsAdmissible(result));
                Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
            }
        }

        [Theory]
        [InlineData(OversizedPatternTile)]
        [InlineData(UnsupportedElement)]
        [InlineData(ExternalImage)]
        [InlineData(MalformedDataUri)]
        [InlineData(PayloadlessDataUri)]
        public void FailClosedDiagnostics_AreBoundedAndSourceFree(string svg)
        {
            using var result = _renderer.Render(svg);

            Assert.False(result.Success);
            AssertDiagnosticsBounded(result);
            AssertSourceFree(result, svg);
        }

        [Theory]
        [InlineData(OversizedPatternTile)]
        [InlineData(ExternalImage)]
        public void FailClosedResult_ExplainsRejection(string svg)
        {
            using var result = _renderer.Render(svg);

            string explanation = SvgRenderResult.DescribeRejection(result);
            Assert.False(string.IsNullOrWhiteSpace(explanation));
            Assert.Equal(result.ErrorMessage, explanation);
            Assert.True(explanation.Length <= SvgRenderResult.MaxRejectionDiagnosticChars);
        }

        [Theory]
        [InlineData(OversizedPatternTile)]
        [InlineData(UnsupportedElement)]
        [InlineData(ExternalImage)]
        [InlineData(MalformedDataUri)]
        [InlineData(PayloadlessDataUri)]
        [InlineData(CompleteRedSquare)]
        public void BackendMetadata_IsFirstParty(string svg)
        {
            using var result = _renderer.Render(svg);

            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
        }

        private static void AssertDiagnosticsBounded(SvgRenderResult result)
        {
            Assert.True(result.ErrorMessage.Length <= FenSvgRenderer.MaxResultDiagnosticChars,
                $"error message was {result.ErrorMessage.Length} chars");
            AssertBoundedList(result.Warnings, "warnings");
            AssertBoundedList(result.FallbackReasonCodes, "fallback reason codes");
            AssertBoundedList(result.ResourceRejectionReasonCodes, "resource reason codes");
        }

        private static void AssertBoundedList(IReadOnlyList<string> values, string name)
        {
            Assert.NotNull(values);
            Assert.True(values.Count <= FenSvgRenderer.MaxResultDiagnosticEntries,
                $"{name} had {values.Count} entries");
            foreach (string value in values)
            {
                Assert.NotNull(value);
                Assert.True(value.Length <= FenSvgRenderer.MaxResultDiagnosticChars,
                    $"{name} entry was {value.Length} chars");
            }
        }

        private static void AssertSourceFree(SvgRenderResult result, string svg)
        {
            Assert.NotEqual(svg, result.ErrorMessage);
            Assert.DoesNotContain("<svg", result.ErrorMessage);
            foreach (string warning in result.Warnings)
            {
                Assert.DoesNotContain("<svg", warning);
            }
        }
    }
}
