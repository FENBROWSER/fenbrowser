using System;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgUseDataUrlParseAdmissionTests
    {
        private const string WptDataUrlUse =
            "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPgogIDxyZWN0" +
            "IGlkPSJyZWQtcmVjdCIgd2lkdGg9IjEwMCIgaGVpZ2h0PSIxMDAiIGZpbGw9InJlZCIvPgo8L3N2Zz4=" +
            "#red-rect";

        private const string WptDataUrlSet =
            "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPgogIDxyZWN0" +
            "IGlkPSJyZWQtcmVjdCIgd2lkdGg9IjEwMCIgaGVpZ2h0PSIxMDAiIGZpbGw9InJlZCIvPgo8L3N2Zz4=" +
            "#red-rect";

        [Fact]
        public void WptUseDataUrl_IsAdmissibleAndPaintsOnlyTheGreenRect()
        {
            string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'>" +
                "<rect width='100' height='100' fill='green'/>" +
                "<use href='" + WptDataUrlUse + "'/></svg>";

            using var result = Render(svg, 0d);

            AssertAdmissible(result);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(50, 50));
            Assert.Single(result.Warnings);
            Assert.Contains("data: URL", result.Warnings[0]);
        }

        [Fact]
        public void WptUseDataUrlSetAttributeName_IsAdmissibleAndPaintsOnlyTheGreenRect()
        {
            string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'>" +
                "<rect width='100' height='100' fill='green'/>" +
                "<use><set attributeName='href' to='" + WptDataUrlSet + "'/></use></svg>";

            using var result = Render(svg, 0d);

            AssertAdmissible(result);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(50, 50));
            Assert.Single(result.Warnings);
            Assert.Contains("data: URL", result.Warnings[0]);
        }

        [Theory]
        [InlineData("href")]
        [InlineData("xlink:href")]
        public void DataUrlUseHref_IsNotAParseStageResourceRejection(string attribute)
        {
            string svg =
                "<svg width='20' height='20' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<rect width='20' height='20' fill='green'/>" +
                "<use " + attribute + "='" + DataUri(RedRectPayload) + "#red-rect'/></svg>";

            using var result = Render(svg, 0d);

            AssertAdmissible(result);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void DataUrlUseHref_WithLeadingWhitespace_IsStillAReferenceToNothing()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/>" +
                "<use href='   data:image/svg+xml;base64,PHN2Zy8+#red-rect'/></svg>";

            using var result = Render(svg, 0d);

            AssertAdmissible(result);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void UppercaseDataUrlScheme_IsStillAReferenceToNothing()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/>" +
                "<use href='DATA:image/svg+xml;base64,PHN2Zy8+#red-rect'/></svg>";

            using var result = Render(svg, 0d);

            AssertAdmissible(result);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void PercentEncodedDataUrlUseHref_NeverReachesADecode()
        {
            const string href =
                "data:image/svg+xml,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%3E" +
                "%3Crect%20id%3D%27red-rect%27%20width%3D%27100%27%20height%3D%27100%27%20fill%3D%27red%27%2F%3E" +
                "%3C%2Fsvg%3E#red-rect";

            string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/>" +
                "<use href='" + href + "'/></svg>";

            using var result = Render(svg, 0d);

            AssertAdmissible(result);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
            AssertNoDecodeDiagnostic(result);
        }

        [Fact]
        public void OverCapDataUrlUseHref_IsNeverDecodedAndStillAdmitsTheDocument()
        {
            string href = "data:image/svg+xml;base64," + new string('a', 4096) + "#red-rect";
            var limits = SvgRenderLimits.Default;
            limits.MaxDecodedImageBytes = 64;
            limits.MaxCumulativeResourceBytes = 64;
            limits.MaxResourceCount = 1;

            string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/>" +
                "<use href='" + href + "'/></svg>";

            using var result = new FenSvgRenderer().Render(
                new SvgRenderRequest(svg, limits) { DocumentTimeSeconds = 0d });

            AssertAdmissible(result);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
            AssertNoDecodeDiagnostic(result);
        }

        [Theory]
        [InlineData("other.svg#target")]
        [InlineData("/support/sprites.svg#target")]
        [InlineData("https://other.example/x.svg#target")]
        [InlineData("http://other.example/x.svg#target")]
        [InlineData("dat:other.svg#target")]
        [InlineData("not-data:other.svg#target")]
        public void RelativeOrCrossOriginUseHref_IsStillAParseStageResourceRejection(string href)
        {
            string svg = "<svg width='20' height='20'><use href='" + href + "'/></svg>";

            using var result = Render(svg, 0d);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.True(result.HadResourceRejection);
            Assert.Contains("external-resource", result.ResourceRejectionReasonCodes);
            Assert.False(SvgRenderResult.IsAdmissible(result));
        }

        [Fact]
        public void RelativeOrCrossOriginUseHref_IsStillARejectedResourceThroughXlink()
        {
            const string svg =
                "<svg width='20' height='20' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<use xlink:href='other.svg#target'/></svg>";

            using var result = Render(svg, 0d);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.True(result.HadResourceRejection);
            Assert.Contains("external-resource", result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void DataUrlUseHref_SetOnTheSmilGateIsRecordedRatherThanCondemned()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/>" +
                "<use><set attributeName='href' to='data:image/svg+xml;base64,PHN2Zy8+#r'/></use></svg>";

            using var result = Render(svg, 0d);

            AssertAdmissible(result);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
            Assert.DoesNotContain(result.Warnings,
                warning => warning.Contains("animated reference resolution"));
        }

        [Fact]
        public void DataUrlUseHref_AnimatedOnTheSmilGateIsRecordedRatherThanCondemned()
        {
            const string svg =
                "<svg width='20' height='20'><defs>" +
                "<rect id='source' width='20' height='20' fill='blue'/></defs>" +
                "<use href='#source'>" +
                "<animate attributeName='href' from='#source' " +
                "to='data:image/svg+xml;base64,PHN2Zy8+#r' dur='1s'/></use></svg>";

            using var result = Render(svg, .5d);

            AssertAdmissible(result);
            Assert.DoesNotContain(result.Warnings,
                warning => warning.Contains("animated reference resolution"));
            AssertNoRedPixel(result);
        }

        [Fact]
        public void DataUrlUseHref_AnimatedOverALiveFragmentStillReachesTheNoOpBranch()
        {
            const string svg =
                "<svg width='20' height='20'><defs>" +
                "<rect id='source' width='20' height='20' fill='blue'/></defs>" +
                "<rect width='20' height='20' fill='green'/>" +
                "<use href='#source'>" +
                "<set attributeName='href' to='data:image/svg+xml;base64,PHN2Zy8+#r' dur='1s'/>" +
                "</use></svg>";

            using var during = Render(svg, .5d);
            using var after = Render(svg, 1.5d);

            AssertAdmissible(during);
            AssertAdmissible(after);
            Assert.Equal(SKColors.Green, during.Bitmap.GetPixel(10, 10));
            Assert.Equal(SKColors.Blue, after.Bitmap.GetPixel(10, 10));
        }

        [Theory]
        [InlineData("other.svg#r")]
        [InlineData("https://other.example/x.svg#r")]
        [InlineData("dat:other.svg#r")]
        public void RelativeOrCrossOriginSetValue_IsStillRejectedOnTheSmilGate(string value)
        {
            const string greenRect = "<rect width='20' height='20' fill='green'/>";
            string svg =
                "<svg width='20' height='20'>" + greenRect +
                "<use><set attributeName='href' to='" + value + "'/></use></svg>";

            using var result = Render(svg, 0d);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.True(result.RequiresFallback);
            Assert.Contains("smil-animation", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("animated reference resolution"));
        }

        [Fact]
        public void DataUrlSetValue_OnANonUseTarget_IsStillAFailedClosedReference()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'>" +
                "<set attributeName='href' to='data:image/svg+xml;base64,PHN2Zy8+#r'/>" +
                "</rect></svg>";

            using var result = Render(svg, 0d);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("animated reference target"));
        }

        [Fact]
        public void DataUrlSetValue_OnAnAnchorIsStillAFailedClosedReference()
        {
            const string svg =
                "<svg width='20' height='20'><a href='#x'>" +
                "<rect width='20' height='20' fill='green'/>" +
                "<set attributeName='href' to='data:image/svg+xml;base64,PHN2Zy8+#r'/>" +
                "</a></svg>";

            using var result = Render(svg, 0d);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Contains(result.Warnings,
                warning => warning.Contains("animated reference target"));
        }

        [Fact]
        public void DataUrlSetValue_NeverOverridesAStaticHrefOfADifferentName()
        {
            const string svg =
                "<svg width='20' height='20' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<rect width='20' height='20' fill='green'/>" +
                "<use href='#a' xlink:href='#b'>" +
                "<set attributeName='xlink:href' to='data:image/svg+xml;base64,PHN2Zy8+#r' dur='1s'/>" +
                "</use></svg>";

            using var result = Render(svg, .5d);

            Assert.False(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("animated reference precedence"));
        }

        [Fact]
        public void DataUrlUseHref_NeverPaintsAPixelFromThePayload()
        {
            const string payload =
                "<svg xmlns='http://www.w3.org/2000/svg'>" +
                "<rect id='red-rect' width='100' height='100' fill='red'/></svg>";

            string svg = "<svg width='20' height='20'><use href='" +
                         DataUri(payload) + "#red-rect'/></svg>";

            using var result = Render(svg, 0d);

            AssertAdmissible(result);
            AssertNoRedPixel(result);
        }

        [Fact]
        public void AnAdmittedDataUrlUseFrameIsTheSameFrameAsTheDocumentWithoutIt()
        {
            const string withoutUse =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/></svg>";
            string withUse =
                "<svg width='20' height='20'><rect width='20' height='20' fill='green'/>" +
                "<use href='" + DataUri(RedRectPayload) + "#red-rect'/></svg>";

            using var plain = Render(withoutUse, 0d);
            using var withReference = Render(withUse, 0d);

            AssertAdmissible(plain);
            AssertAdmissible(withReference);
            Assert.Equal(plain.Bitmap.GetPixel(10, 10), withReference.Bitmap.GetPixel(10, 10));
        }

        private const string RedRectPayload =
            "<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<rect id='red-rect' width='100' height='100' fill='red'/></svg>";

        private static SvgRenderResult Render(string svg, double seconds) =>
            new FenSvgRenderer().Render(new SvgRenderRequest(svg, SvgRenderLimits.Default)
            {
                DocumentTimeSeconds = seconds
            });

        private static string DataUri(string svg) =>
            "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

        private static void AssertAdmissible(SvgRenderResult result)
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result), string.Join("; ", result.Warnings));
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(result.HadResourceRejection, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Empty(result.ResourceRejectionReasonCodes);
        }

        private static void AssertNoRedPixel(SvgRenderResult result)
        {
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
                Assert.DoesNotContain("decoded", warning, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
