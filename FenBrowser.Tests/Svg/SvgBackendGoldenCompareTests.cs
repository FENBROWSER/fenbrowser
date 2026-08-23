using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// PHASE 2 migration gate. Whole-image RGB similarity hides foreground
    /// errors behind transparent background, so verification is foreground-
    /// focused:
    ///   - identical canvas dimensions
    ///   - non-transparent pixel-count ratio
    ///   - alpha-mask Intersection-over-Union
    ///   - foreground bounding-box agreement (<= 2px per edge)
    ///   - RGB mean-abs-diff over the union of foreground masks (<= 16/255)
    ///   - explicit semantic probes where expected output is known
    /// Svg.Skia is treated as REFERENCE only on its spec-conformant subset;
    /// known spec violations are pinned by explicit expectations instead.
    /// </summary>
    public class SvgBackendGoldenCompareTests
    {
        

        public static IEnumerable<object[]> Corpus()
        {
            yield return new object[] { "rect-solid",
                "<svg xmlns='http://www.w3.org/2000/svg' width='48' height='32'><rect width='48' height='32' fill='red'/></svg>", true };
            yield return new object[] { "circle-stroke",
                "<svg width='40' height='40'><circle cx='20' cy='20' r='14' fill='blue' stroke='black' stroke-width='2'/></svg>", true };
            yield return new object[] { "linear-gradient",
                "<svg width='40' height='20'><defs><linearGradient id='g'><stop offset='0' stop-color='white'/>" +
                "<stop offset='1' stop-color='black'/></linearGradient></defs><rect width='40' height='20' fill='url(#g)'/></svg>", true };
            yield return new object[] { "radial-gradient",
                "<svg width='40' height='40'><defs><radialGradient id='r'><stop offset='0' stop-color='yellow'/>" +
                "<stop offset='1' stop-color='navy'/></radialGradient></defs><rect width='40' height='40' fill='url(#r)'/></svg>", true };
            yield return new object[] { "path-arcs",
                "<svg width='40' height='40'><path d='M20 4 A16 16 0 1 0 36 20 L20 20 Z' fill='teal'/></svg>", true };
            yield return new object[] { "curves-qcubic",
                "<svg width='60' height='30'><path d='M4 25 Q 20 -10 35 25 T 58 18' fill='none' stroke='darkgreen' stroke-width='3'/></svg>", true };
            yield return new object[] { "transform-translate-shape",
                "<svg width='40' height='40'><rect x='2' y='2' width='6' height='6' fill='red' transform='translate(12 8)'/></svg>", true };
            yield return new object[] { "transform-rotate-center",
                "<svg width='50' height='50'><rect x='20' y='10' width='10' height='6' fill='red' transform='rotate(180 30 18)'/></svg>", true };
            yield return new object[] { "nested-transforms",
                "<svg width='40' height='40'><g transform='translate(10 10)'>" +
                "<rect width='5' height='5' fill='red' transform='translate(5 5)'/></g></svg>", true };
            yield return new object[] { "group-opacity-isolated",
                "<svg width='30' height='30'><g opacity='0.6'><rect width='30' height='15' fill='red'/>" +
                "<rect y='15' width='30' height='15' fill='lime'/></g></svg>", true };
            yield return new object[] { "fill-stroke-opacity",
                "<svg width='30' height='30'><rect x='4' y='4' width='22' height='22' fill='#0000ff' fill-opacity='0.5' " +
                "stroke='#ff0000' stroke-opacity='0.75' stroke-width='3'/></svg>", true };
            yield return new object[] { "currentcolor-inherited",
                "<svg width='24' height='24' color='crimson'><rect x='2' y='2' width='20' height='20' fill='currentColor'/></svg>", true };
            yield return new object[] { "inline-style-overrides",
                "<svg width='20' height='20'><rect width='20' height='20' fill='red' style='fill:gold'/></svg>", true };
            yield return new object[] { "use-symbol",
                "<svg width='40' height='40'><symbol id='s'><rect width='10' height='10' fill='purple'/></symbol>" +
                "<use href='#s' x='6' y='8'/><use href='#s' x='22' y='20'/></svg>", true };
            yield return new object[] { "clip-path",
                "<svg width='30' height='20'><clipPath id='c'><rect width='15' height='20'/></clipPath>" +
                "<rect width='30' height='20' fill='red' clip-path='url(#c)'/></svg>", true };
            yield return new object[] { "viewbox-par-xMinYMin",
                "<svg width='40' height='40' viewBox='0 0 80 20' preserveAspectRatio='xMinYMin'>" +
                "<rect width='80' height='20' fill='red'/></svg>", true };
            yield return new object[] { "viewbox-par-slice",
                "<svg width='20' height='20' viewBox='0 0 40 20' preserveAspectRatio='xMidYMid slice'>" +
                "<rect width='40' height='20' fill='dodgerblue'/></svg>", true };
            yield return new object[] { "image-data-uri",
                ImageCase(), true };
            yield return new object[] { "malformed-recoverable",
                "<svg width='20' height='20'><rect width='14' height='20' fill='red'><unclosed></rect>" +
                "</weird><circle cx='17' cy='10' r='3' fill='blue'/></svg>", false };
            yield return new object[] { "basic-text",
                // Font rasterization is platform-dependent, so this carries a
                // semantic foreground expectation rather than pixel comparison.
                "<svg width='40' height='20'><text x='2' y='14' font-size='12'>Hi</text></svg>", false };
        }

        private static string ImageCase()
        {
            using var bmp = new SKBitmap(4, 4);
            bmp.Erase(SKColors.Red);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            var uri = "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
            return $"<svg width='20' height='20'><image href='{uri}' x='3' y='4' width='12' height='9'/></svg>";
        }

        [Theory]
        [MemberData(nameof(Corpus))]
        public void Differential_Gate(string caseName, string svg, bool differential)
        {
            var fen = new FenSvgRenderer().Render(svg);
            Assert.True(fen.Success, $"fen failed [{caseName}]: {fen.ErrorMessage}");

            if (!differential)
            {
                // Non-differential cases have spec-derived expectations instead
                // of trusting Svg.Skia (which fails or diverges on them).
                if (caseName == "malformed-recoverable")
                {
                    Assert.Equal((byte)255, fen.Bitmap.GetPixel(5, 5).Red);
                    Assert.Equal((byte)255, fen.Bitmap.GetPixel(17, 10).Blue);
                }
                else if (caseName == "basic-text")
                {
                    Assert.True(HasForeground(fen.Bitmap), "basic text must render visible glyphs");
                }
                return;
            }

            var legacy = new SvgSkiaRenderer().Render(svg);
            Assert.True(legacy.Success, $"legacy failed [{caseName}]: {legacy.ErrorMessage}");
            using var _f = fen.Bitmap;
            using var _l = legacy.Bitmap;

            Assert.Equal(legacy.Bitmap.Width, fen.Bitmap.Width);
            Assert.Equal(legacy.Bitmap.Height, fen.Bitmap.Height);

            var m = Metrics.Compute(fen.Bitmap, legacy.Bitmap);

            // Foreground presence must be comparable (not mostly-empty vs full).
            double countRatio = m.FenPixels == 0 && m.LegacyPixels == 0
                ? 1.0
                : (double)System.Math.Min(m.FenPixels, m.LegacyPixels) /
                  System.Math.Max(m.FenPixels, m.LegacyPixels);
            Assert.True(countRatio >= 0.80,
                $"[{caseName}] foreground pixel count ratio {countRatio:F2} " +
                $"(fen={m.FenPixels}, legacy={m.LegacyPixels})");

            // 0.80 still fails any structural error (ignored transform/clip
            // drops IoU far below) while tolerating edge AA on small rasters.
            Assert.True(m.AlphaIou >= 0.80,
                $"[{caseName}] alpha IoU {m.AlphaIou:F3} (a rotation/translation/" +
                $"clip omission shows up here)");

            Assert.True(m.BboxDeltaX <= 2 && m.BboxDeltaY <= 2,
                $"[{caseName}] foreground bbox moved: fen={FormatRect(m.FenBox)} " +
                $"legacy={FormatRect(m.LegacyBox)}");

            if (m.UnionPixels > 0)
            {
                Assert.True(m.UnionRgbDiff <= 16.0,
                    $"[{caseName}] union RGB mean diff {m.UnionRgbDiff:F1}/255");
            }
        }

        [Fact]
        public void BasicText_RendersVisiblePixels_SpecDerived()
        {
            const string svg = "<svg width='40' height='20'><text x='2' y='14' font-size='12'>Hi</text></svg>";
            var fen = new FenSvgRenderer().Render(svg);
            Assert.True(fen.Success, fen.ErrorMessage ?? "");
            Assert.True(HasForeground(fen.Bitmap), "basic text must render visible glyphs");
        }

        private static bool HasForeground(SKBitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0) return true;
            return false;
        }

        private static string FormatRect(SKRectI r) => $"({r.Left},{r.Top},{r.Right},{r.Bottom})";

        internal sealed class DiffMetrics
        {
            public int FenPixels;
            public int LegacyPixels;
            public double AlphaIou;
            public int BboxDeltaX;
            public int BboxDeltaY;
            public double UnionRgbDiff;
            public int UnionPixels;
            public SKRectI FenBox;
            public SKRectI LegacyBox;
        }

        internal static class Metrics
        {
            public static DiffMetrics Compute(SKBitmap fen, SKBitmap legacy)
            {
                var m = new DiffMetrics();
                int w = System.Math.Min(fen.Width, legacy.Width);
                int h = System.Math.Min(fen.Height, legacy.Height);

                int inter = 0, union = 0;
                long rgbSum = 0;
                m.FenBox = SKRectI.Empty;
                m.LegacyBox = SKRectI.Empty;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        bool fa = fen.GetPixel(x, y).Alpha > 0;
                        bool la = legacy.GetPixel(x, y).Alpha > 0;
                        if (fa)
                        {
                            m.FenPixels++;
                            Expand(ref m.FenBox, x, y);
                        }
                        if (la)
                        {
                            m.LegacyPixels++;
                            Expand(ref m.LegacyBox, x, y);
                        }
                        if (fa || la) union++;
                        if (fa && la) inter++;

                        if (fa || la)
                        {
                            var pa = fen.GetPixel(x, y);
                            var pb = legacy.GetPixel(x, y);
                            rgbSum += System.Math.Abs(pa.Red - pb.Red)
                                    + System.Math.Abs(pa.Green - pb.Green)
                                    + System.Math.Abs(pa.Blue - pb.Blue);
                        }
                    }
                }

                m.AlphaIou = union == 0 ? 1.0 : (double)inter / union;
                m.UnionPixels = union;
                m.UnionRgbDiff = union == 0 ? 0 : rgbSum / (union * 3.0);

                if (m.FenPixels > 0 && m.LegacyPixels > 0)
                {
                    m.BboxDeltaX = System.Math.Max(
                        System.Math.Abs(m.FenBox.Left - m.LegacyBox.Left),
                        System.Math.Abs(m.FenBox.Right - m.LegacyBox.Right));
                    m.BboxDeltaY = System.Math.Max(
                        System.Math.Abs(m.FenBox.Top - m.LegacyBox.Top),
                        System.Math.Abs(m.FenBox.Bottom - m.LegacyBox.Bottom));
                }
                return m;
            }

            private static void Expand(ref SKRectI r, int x, int y)
            {
                if (r.IsEmpty)
                {
                    r = new SKRectI(x, y, x, y);
                    return;
                }
                if (x < r.Left) r.Left = x;
                if (y < r.Top) r.Top = y;
                if (x > r.Right) r.Right = x;
                if (y > r.Bottom) r.Bottom = y;
            }
        }
    }
}




