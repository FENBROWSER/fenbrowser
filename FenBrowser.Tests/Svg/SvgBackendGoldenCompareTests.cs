using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// Migration gate: FenSvgRenderer must produce visually equivalent output
    /// to the legacy SvgSkiaRenderer on a legacy-safe geometric corpus before
    /// the default backend flips. Similarity = 1 - meanAbsDiff/255 over RGB.
    /// </summary>
    public class SvgBackendGoldenCompareTests
    {
        public static IEnumerable<object[]> Corpus()
        {
            yield return new object[]
            {
                "red rect",
                "<svg xmlns='http://www.w3.org/2000/svg' width='48' height='32'><rect width='48' height='32' fill='red'/></svg>"
            };
            yield return new object[]
            {
                "circle stroke",
                "<svg width='40' height='40'><circle cx='20' cy='20' r='14' fill='blue' stroke='black' stroke-width='2'/></svg>"
            };
            yield return new object[]
            {
                "linear gradient",
                "<svg width='40' height='20'><defs><linearGradient id='g'><stop offset='0' stop-color='white'/>" +
                "<stop offset='1' stop-color='black'/></linearGradient></defs>" +
                "<rect width='40' height='20' fill='url(#g)'/></svg>"
            };
            yield return new object[]
            {
                "transform rotate",
                "<svg width='40' height='40'><rect x='10' y='10' width='20' height='20' fill='purple' transform='rotate(30 20 20)'/></svg>"
            };
            yield return new object[]
            {
                "group opacity",
                "<svg width='30' height='30'><g opacity='0.6'><rect width='30' height='15' fill='red'/>" +
                "<rect y='15' width='30' height='15' fill='lime'/></g></svg>"
            };
            yield return new object[]
            {
                "path arcs",
                "<svg width='40' height='40'><path d='M20 4 A16 16 0 1 0 36 20 L20 20 Z' fill='teal'/></svg>"
            };
        }

        [Theory]
        [MemberData(nameof(Corpus))]
        public void Backends_ProduceSimilarPixels(string caseName, string svg)
        {
            var fen = new FenSvgRenderer().Render(svg);
            var legacy = new SvgSkiaRenderer().Render(svg);

            Assert.True(fen.Success, $"fen failed [{caseName}]: {fen.ErrorMessage}");
            Assert.True(legacy.Success, $"legacy failed [{caseName}]: {legacy.ErrorMessage}");

            using var fenBmp = fen.Bitmap;
            using var legBmp = legacy.Bitmap;

            // Normalize comparison surface to legacy natural size (contain-fit
            // would blur; direct sample with independent scales instead).
            double similarity = Similarity(fenBmp, legBmp);
            Assert.True(similarity >= 0.85,
                $"[{caseName}] similarity {similarity:F3} below gate");
        }

        internal static double Similarity(SKBitmap a, SKBitmap b)
        {
            int w = System.Math.Min(a.Width, b.Width);
            int h = System.Math.Min(a.Height, b.Height);
            if (w == 0 || h == 0) return 0;

            long diffSum = 0;
            long samples = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var pa = a.GetPixel(x, y);
                    var pb = b.GetPixel(x, y);
                    diffSum += System.Math.Abs(pa.Red - pb.Red)
                             + System.Math.Abs(pa.Green - pb.Green)
                             + System.Math.Abs(pa.Blue - pb.Blue);
                    samples += 3;
                }
            }
            return samples == 0 ? 0 : 1.0 - (double)diffSum / (samples * 255.0);
        }
    }
}
