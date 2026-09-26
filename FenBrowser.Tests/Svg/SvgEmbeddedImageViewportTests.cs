using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgEmbeddedImageViewportTests
    {
        [Fact]
        public void EmbeddedImageViewportUnits_ResolveAgainstImageViewport()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<image xlink:href=\"data:image/svg+xml,&lt;svg xmlns='http://www.w3.org/2000/svg'&gt;" +
                "&lt;rect width='50vw' height='50vh' fill='green'/&gt;&lt;/svg&gt;\" width='200' height='200'/>" +
                "</svg>";

            AssertGreenSquare(svg, 300, 150, 100);
        }

        [Fact]
        public void EmbeddedImageViewportUnitsFromInlineStyle_ResolveAgainstImageViewport()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<image xlink:href=\"data:image/svg+xml,&lt;svg xmlns='http://www.w3.org/2000/svg'&gt;" +
                "&lt;rect style='width: 50vw; height: 50vh' fill='green'/&gt;&lt;/svg&gt;\" " +
                "width='200' height='200'/></svg>";

            AssertGreenSquare(svg, 300, 150, 100);
        }

        [Fact]
        public void EmbeddedImageAutoHeight_UsesReferencedIntrinsicRatio()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 3 3' width='200' height='200'>" +
                "<image href=\"data:image/svg+xml,&lt;svg xmlns='http://www.w3.org/2000/svg' width='50' height='50'&gt;" +
                "&lt;rect width='50' height='50' fill='green'/&gt;&lt;/svg&gt;\" width='1.5'/></svg>";

            AssertGreenSquare(svg, 200, 200, 100);
        }

        [Fact]
        public void EmbeddedImageAutoWidth_UsesReferencedIntrinsicRatio()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 4 4' width='200' height='200'>" +
                "<image href=\"data:image/svg+xml,&lt;svg xmlns='http://www.w3.org/2000/svg' width='50' height='25'&gt;" +
                "&lt;rect width='50' height='25' fill='green'/&gt;&lt;/svg&gt;\" height='1'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(200, result.Bitmap.Width);
            Assert.Equal(200, result.Bitmap.Height);
            Assert.Equal(5000, Foreground(result.Bitmap));
            AssertGreen(result.Bitmap, 50, 25);
            AssertGreen(result.Bitmap, 99, 49);
            AssertTransparent(result.Bitmap, 100, 25);
            AssertTransparent(result.Bitmap, 50, 50);
        }

        [Fact]
        public void EmbeddedImageViewFragment_UsesViewViewBox()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<rect x='-355' y='-1110' width='455' height='1210' fill='red'/>" +
                "<image preserveAspectRatio='none' x='-355' y='-1110' width='455' height='1210' " +
                "xlink:href=\"data:image/svg+xml," +
                "%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 2317 2320'%3e" +
                "%3cview id='view' preserveAspectRatio='none' viewBox='0 0 455 1210'/%3e" +
                "%3crect width='455' height='1210' fill='green'/%3e" +
                "%3c/svg%3e#view\"/></svg>";

            AssertGreenSquare(svg, 300, 150, 100);
        }

        [Fact]
        public void EmbeddedImageViewFragmentWithoutViewBox_KeepsDocumentViewport()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<image xlink:href=\"data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' width='40' " +
                "height='40'%3e%3cview id='view'/%3e%3crect width='40' height='40' fill='green'/%3e%3c/svg%3e#view\" " +
                "width='40' height='40'/></svg>";

            AssertGreenSquare(svg, 300, 150, 40);
        }

        [Fact]
        public void EmbeddedImageUnknownFragment_FailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<image xlink:href=\"data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' width='40' " +
                "height='40'%3e%3crect width='40' height='40' fill='green'/%3e%3c/svg%3e#missing\" " +
                "width='40' height='40'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("reference-resolution", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("view fragment is unresolved or invalid", StringComparison.Ordinal));
        }

        [Fact]
        public void EmbeddedImageUnknownFragment_DoesNotPaintTheReferencedDocument()
        {
            AssertNoInk(
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<image xlink:href=\"data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' width='40' " +
                "height='40'%3e%3crect width='40' height='40' fill='green'/%3e%3c/svg%3e#nosuch\" " +
                "width='40' height='40'/></svg>");
        }

        [Fact]
        public void EmbeddedImageFragmentNamingANonViewElement_FailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<image xlink:href=\"data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' width='40' " +
                "height='40'%3e%3cg id='notaview'%3e%3crect width='40' height='40' fill='green'/%3e%3c/g%3e" +
                "%3c/svg%3e#notaview\" width='40' height='40'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("reference-resolution", result.FallbackReasonCodes);
        }

        [Fact]
        public void EmbeddedImageViewFragmentWithInvalidViewBox_FailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<image xlink:href=\"data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' width='40' " +
                "height='40'%3e%3cview id='view' viewBox='0 0 0 0'/%3e" +
                "%3crect width='40' height='40' fill='green'/%3e%3c/svg%3e#view\" " +
                "width='40' height='40'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("reference-resolution", result.FallbackReasonCodes);
        }

        [Fact]
        public void EmbeddedImageViewFragmentWithMalformedViewBox_FailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<image xlink:href=\"data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' width='40' " +
                "height='40'%3e%3cview id='view' viewBox='0 0 40'/%3e" +
                "%3crect width='40' height='40' fill='green'/%3e%3c/svg%3e#view\" " +
                "width='40' height='40'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("reference-resolution", result.FallbackReasonCodes);
        }

        [Fact]
        public void EmbeddedImageWithoutFragment_IgnoresDanglingViewsInTheReferencedDocument()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<image xlink:href=\"data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' width='40' " +
                "height='40'%3e%3cview id='view' viewBox='0 0 0 0'/%3e" +
                "%3crect width='40' height='40' fill='green'/%3e%3c/svg%3e\" " +
                "width='40' height='40'/></svg>";

            AssertGreenSquare(svg, 300, 150, 40);
        }

        [Fact]
        public void EmbeddedImageViewportUnitsDoNotReshapeSizedReference()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<image xlink:href=\"data:image/svg+xml,&lt;svg xmlns='http://www.w3.org/2000/svg' width='100' " +
                "height='100'&gt;&lt;rect width='50vw' height='50vh' fill='green'/&gt;&lt;/svg&gt;\" " +
                "width='200' height='200'/></svg>";

            AssertGreenSquare(svg, 300, 150, 100);
        }

        [Fact]
        public void EmbeddedImageViewportUnitsFromUnsizedReference_FailClosedOnNestedFallback()
        {
            string nested =
                "<svg xmlns='http://www.w3.org/2000/svg'>" +
                "<rect width='10vw' height='10vh' fill='green'/>" +
                "<foreignObject width='10' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40'>" +
                "<image href='" + DataUri(nested) + "' width='40' height='40'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-resource", result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void EmbeddedImageUnsizedReference_FailClosedOnNestedResourceRejection()
        {
            string nested = "<svg xmlns='http://www.w3.org/2000/svg'><rect width='10vw' height='10vh'/>" +
                           "<image href='https://example.invalid/x.png' width='4' height='4'/></svg>";

            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40'>" +
                "<image href='" + DataUri(nested) + "' width='40' height='40'/></svg>");

            AssertFailsClosed(result);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void EmbeddedImageUnsizedReference_ObeysReferenceDepthBudget()
        {
            string nested =
                "<svg xmlns='http://www.w3.org/2000/svg'><rect width='2' height='2' fill='green'/></svg>";
            for (int i = 0; i < 18; i++)
            {
                nested = "<svg xmlns='http://www.w3.org/2000/svg'>" +
                         "<image href='" + DataUri(nested) + "' width='2' height='2'/></svg>";
            }

            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8'>" +
                "<image href='" + DataUri(nested) + "' width='8' height='8'/></svg>",
                new SvgRenderLimits { MaxReferenceDepth = 4, MaxDecodedImageBytes = 2 * 1024 * 1024 });

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.Contains(result.Warnings, warning => warning.Contains("embedded SVG"));
        }

        [Fact]
        public void EmbeddedImageAutoDimensionWithUnresolvedImage_FailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40'>" +
                "<image href='https://example.invalid/x.svg' width='40'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
        }

        private static void AssertGreenSquare(string svg, int width, int height, int side)
        {
            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.False(result.HadResourceRejection);
            Assert.Equal(width, result.Bitmap.Width);
            Assert.Equal(height, result.Bitmap.Height);

            int expected = Math.Min(side, width) * Math.Min(side, height);
            Assert.Equal(expected, Foreground(result.Bitmap));
            AssertGreen(result.Bitmap, side / 2, side / 2);
            AssertGreen(result.Bitmap, side - 1, side / 2);
            AssertGreen(result.Bitmap, side / 2, side - 1);
            AssertGreen(result.Bitmap, 0, 0);
            AssertTransparent(result.Bitmap, side, side / 2);
            AssertTransparent(result.Bitmap, side / 2, side);
        }

        private static int Foreground(SKBitmap bitmap)
        {
            int count = 0;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0)
                    count++;
            return count;
        }

        private static void AssertGreen(SKBitmap bitmap, int x, int y)
        {
            SKColor pixel = bitmap.GetPixel(x, y);
            Assert.True(pixel.Alpha >= 250, $"alpha {pixel.Alpha} at {x},{y}");
            Assert.True(pixel.Green > 100, $"green {pixel.Green} at {x},{y}");
            Assert.True(pixel.Red < 64, $"red {pixel.Red} at {x},{y}");
            Assert.True(pixel.Blue < 64, $"blue {pixel.Blue} at {x},{y}");
        }

        private static void AssertTransparent(SKBitmap bitmap, int x, int y) =>
            Assert.Equal(0, bitmap.GetPixel(x, y).Alpha);

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        private static void AssertNoInk(string svg)
        {
            bool rendered = SvgRenderEngine.TryRender(
                svg, SvgRenderLimits.Default, null, null, 0,
                out var picture, out float width, out float height, out string error,
                out _, out IReadOnlyList<string> fallbackCodes, out _,
                out bool requiresFallback, out _);

            Assert.True(rendered, error);
            using (picture)
            {
                Assert.True(requiresFallback);
                Assert.Contains("reference-resolution", fallbackCodes);
                Assert.True(width > 0f && height > 0f);
                using var surface = SKSurface.Create(new SKImageInfo(300, 150));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.DrawPicture(picture);
                using var raster = surface.Snapshot();
                using var bitmap = SKBitmap.FromImage(raster);
                Assert.Equal(0, Foreground(bitmap));
            }
        }

        private static string DataUri(string svg) =>
            "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}
