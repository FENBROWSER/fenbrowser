using System;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgUseDataUrlReferenceTests
    {
        private const string RedRectPayload =
            "<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<rect id='red-rect' width='100' height='100' fill='red'/></svg>";

        [Fact]
        public void DataUrlUseReference_NeverYieldsAnInstance()
        {
            string href = DataUri(RedRectPayload) + "#red-rect";

            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100'><use href='" + href + "'/></svg>");

            AssertNoReferenceInstance(result);
            Assert.Contains(result.Warnings, warning => warning.Contains("data: URL"));
            Assert.DoesNotContain(
                result.Warnings, warning => warning.Contains("by SVG resource policy"));
        }

        [Fact]
        public void DataUrlUseReference_PercentEncodedPayloadIsAlsoNeverAnInstance()
        {
            string href =
                "data:image/svg+xml,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%3E" +
                "%3Crect%20id%3D%27red-rect%27%20width%3D%27100%27%20height%3D%27100%27%20fill%3D%27red%27%2F%3E" +
                "%3C%2Fsvg%3E#red-rect";

            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100'><use href='" + href + "'/></svg>");

            AssertNoReferenceInstance(result);
        }

        [Fact]
        public void MalformedDataUrlUseReference_NeverReachesADecode()
        {
            string href = "data:image/svg+xml;base64,%%%not-base64-not-percent%%%#red-rect";

            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100'><use href='" + href + "'/></svg>");

            AssertNoReferenceInstance(result);
            AssertNoDecodeDiagnostic(result);
        }

        [Fact]
        public void OverCapDataUrlUseReference_IsNeverDecodedOrAdmitted()
        {
            string href = "data:image/svg+xml;base64," + new string('a', 4096) + "#red-rect";
            var limits = SvgRenderLimits.Default;
            limits.MaxDecodedImageBytes = 64;
            limits.MaxCumulativeResourceBytes = 64;
            limits.MaxResourceCount = 1;

            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100'><use href='" + href + "'/></svg>", limits);

            AssertNoReferenceInstance(result);
            AssertNoDecodeDiagnostic(result);
        }

        [Fact]
        public void UnresolvedFragmentOnDataUrlUseReference_IsTheSameOutcome()
        {
            string href = DataUri(RedRectPayload) + "#not-a-target";

            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100'><use href='" + href + "'/></svg>");

            AssertNoReferenceInstance(result);
            AssertNoDecodeDiagnostic(result);
        }

        [Fact]
        public void DataUrlUseReference_AnyAdmittedFrameIsTheOneWithoutTheInstance()
        {
            string href = DataUri(RedRectPayload) + "#red-rect";

            using var result = new FenSvgRenderer().Render(
                "<svg width='100' height='100'><rect width='100' height='100' fill='green'/>" +
                "<use href='" + href + "'/></svg>");

            AssertNoReferenceInstance(result);
            if (result.Bitmap == null) return;
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(50, 50));
        }

        [Fact]
        public void DataUrlUseReference_InsideAMarkerStaysAdmissible()
        {
            string href = DataUri(RedRectPayload) + "#red-rect";
            string svg =
                "<svg width='20' height='20'>" +
                "<defs><marker id='m' markerWidth='4' markerHeight='4'>" +
                "<use href='" + href + "'/></marker></defs>" +
                "<path d='M0 10 L20 10' stroke='black' marker-end='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertNoReferenceInstance(result);
            AssertNoDecodeDiagnostic(result);
        }

        [Fact]
        public void UseWithoutAReference_IsNotARejectedResource()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><use/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.HadResourceRejection);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.ResourceRejectionReasonCodes);
            Assert.Empty(result.FallbackReasonCodes);
        }

        [Fact]
        public void UseWithoutAReference_StillRendersTheRestOfTheDocument()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/><use/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.HadResourceRejection);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void RelativeExternalUseReference_StaysAResourceRejection()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><use href='other.svg#target'/></svg>");

            Assert.False(result.Success);
            Assert.True(result.HadResourceRejection);
            Assert.Contains("external-resource", result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void CrossOriginUseReference_StaysAResourceRejection()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><use href='https://other.example/x.svg#target'/></svg>");

            Assert.False(result.Success);
            Assert.True(result.HadResourceRejection);
            Assert.Contains("external-resource", result.ResourceRejectionReasonCodes);
        }

        private static string DataUri(string svg) =>
            "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

        private static void AssertNoReferenceInstance(SvgRenderResult result)
        {
            if (result.Bitmap == null) return;
            for (int y = 0; y < result.Bitmap.Height; y++)
            {
                for (int x = 0; x < result.Bitmap.Width; x++)
                {
                    SKColor pixel = result.Bitmap.GetPixel(x, y);
                    Assert.False(
                        pixel.Alpha > 0 && pixel.Red > 200 && pixel.Green < 60 && pixel.Blue < 60,
                        $"a use instance painted {x},{y} from a reference that resolves to no element");
                }
            }
        }

        private static void AssertNoDecodeDiagnostic(SvgRenderResult result)
        {
            foreach (string warning in result.Warnings)
            {
                Assert.DoesNotContain("base64", warning, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("percent", warning, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("payload", warning, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("budget", warning, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("UTF-8", warning, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
