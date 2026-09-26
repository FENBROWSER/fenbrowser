using System;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgNestedResourceTests
    {
        [Fact]
        public void NestedUnsupportedFeature_IsRejectedWithoutEscape()
        {
            string nested = DataSvg("<svg width='10' height='10'><foreignObject width='10' height='10'/></svg>");

            using var result = new FenSvgRenderer().Render(
                $"<svg width='10' height='10'><image href='{nested}' width='10' height='10'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.NotEmpty(result.ResourceRejectionReasonCodes);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
        }

        [Fact]
        public void RecursiveSvgImages_ObeySharedDepthBudget()
        {
            string nested = "<svg width='1' height='1'><rect width='1' height='1'/></svg>";
            for (int i = 0; i < 18; i++)
            {
                string uri = DataSvg(nested);
                nested = $"<svg width='1' height='1'><image href='{uri}' width='1' height='1'/></svg>";
            }

            using var result = new FenSvgRenderer().Render(nested,
                new SvgRenderLimits { MaxReferenceDepth = 4, MaxDecodedImageBytes = 2 * 1024 * 1024 });

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.Contains(result.Warnings, warning => warning.Contains("embedded SVG"));
        }

        [Fact]
        public void SiblingSvgImages_ShareCumulativeDecodedByteBudget()
        {
            string uri = DataSvg("<svg width='2' height='2'><rect width='2' height='2'/></svg>");
            string svg = $"<svg width='4' height='2'><image href='{uri}' width='2' height='2'/>" +
                         $"<image href='{uri}' x='2' width='2' height='2'/></svg>";

            using var result = new FenSvgRenderer().Render(svg,
                new SvgRenderLimits
                {
                    MaxDecodedImageBytes = 100,
                    MaxCumulativeResourceBytes = 100
                });

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.Contains(result.Warnings, warning => warning.Contains("cumulative byte budget"));
        }

        [Fact]
        public void InvalidUtf8NestedSvg_IsRejected()
        {
            string uri = "data:image/svg+xml;base64," + Convert.ToBase64String(new byte[] { 0xff, 0xfe, 0xfd });

            using var result = new FenSvgRenderer().Render(
                $"<svg width='2' height='2'><image href='{uri}' width='2' height='2'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.Contains(result.Warnings, warning => warning.Contains("UTF-8"));
        }

        [Fact]
        public void PercentEncodedSvgDataUri_RendersWithoutBase64()
        {
            const string uri =
                "data:image/svg+xml,%3Csvg%20width%3D%272%27%20height%3D%272%27%3E" +
                "%3Crect%20width%3D%272%27%20height%3D%272%27%20fill%3D%27red%27/%3E%3C/svg%3E";

            using var result = new FenSvgRenderer().Render(
                $"<svg width='4' height='4'><image href='{uri}' width='4' height='4'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.HadResourceRejection);
            Assert.True(result.Bitmap.GetPixel(2, 2).Red > 200);
        }

        [Fact]
        public void XmlEscapedSvgDataUri_RendersAfterAttributeEntityDecoding()
        {
            const string svg =
                "<svg width='4' height='4'><image width='4' height='4' " +
                "href=\"data:image/svg+xml,&lt;svg width='2' height='2'&gt;" +
                "&lt;rect width='2' height='2' fill='red'/&gt;&lt;/svg&gt;\"/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.HadResourceRejection);
            Assert.True(result.Bitmap.GetPixel(2, 2).Red > 200);
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

        private static string DataSvg(string svg) =>
            "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}
