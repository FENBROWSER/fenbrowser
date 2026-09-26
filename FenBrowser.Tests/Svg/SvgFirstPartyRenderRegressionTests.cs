using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// First-party pixel and semantic regression suite. There is no second
    /// backend to diff against, so every case pins an explicit first-party
    /// expectation instead: exact canvas size, foreground presence, coverage
    /// floor, and named probe colors derived from the SVG specification.
    /// <para>
    /// Determinism and fail-closed shape are asserted for the same corpus, so a
    /// regression shows up as a spec-derived mismatch rather than as drift from
    /// a third-party implementation.
    /// </para>
    /// <para>
    /// The corpus includes an embedded-image and a text case, so rendering reads
    /// the process-wide image and font fallback state. The bit-for-bit
    /// determinism cases must not run while another test mutates that state, so
    /// the class runs inside <see cref="SvgRendererBackendStateCollection"/>.
    /// </para>
    /// </summary>
    [Collection(SvgRendererBackendStateCollection.Name)]
    public class SvgFirstPartyRenderRegressionTests
    {
        public sealed class Case
        {
            public Case(
                string name,
                string svg,
                int width,
                int height,
                bool foregroundExpected = true,
                double minCoverage = 0.0)
            {
                Name = name;
                Svg = svg;
                Width = width;
                Height = height;
                ForegroundExpected = foregroundExpected;
                MinCoverage = minCoverage;
                Probes = new List<(int X, int Y, SKColor Expected, int Tolerance)>();
            }

            public string Name { get; }
            public string Svg { get; }
            public int Width { get; }
            public int Height { get; }
            public bool ForegroundExpected { get; }
            public double MinCoverage { get; }
            public List<(int X, int Y, SKColor Expected, int Tolerance)> Probes { get; }

            public Case Probe(int x, int y, SKColor expected, int tolerance = 8)
            {
                Probes.Add((x, y, expected, tolerance));
                return this;
            }
        }

        public static IEnumerable<object[]> Corpus()
        {
            foreach (Case item in BuildCorpus())
            {
                yield return new object[] { item };
            }
        }

        private static IEnumerable<Case> BuildCorpus()
        {
            yield return new Case("rect-solid",
                "<svg xmlns='http://www.w3.org/2000/svg' width='48' height='32'><rect width='48' height='32' fill='red'/></svg>",
                48, 32, minCoverage: 0.99)
                .Probe(24, 16, SKColors.Red);

            yield return new Case("circle-stroke",
                "<svg width='40' height='40'><circle cx='20' cy='20' r='14' fill='blue' stroke='black' stroke-width='2'/></svg>",
                40, 40)
                .Probe(20, 20, SKColors.Blue)
                .Probe(6, 20, SKColors.Black)
                .Probe(0, 20, SKColors.Transparent);

            yield return new Case("linear-gradient",
                "<svg width='40' height='20'><defs><linearGradient id='g'><stop offset='0' stop-color='white'/>" +
                "<stop offset='1' stop-color='black'/></linearGradient></defs><rect width='40' height='20' fill='url(#g)'/></svg>",
                40, 20, minCoverage: 0.99)
                .Probe(20, 10, new SKColor(0x80, 0x80, 0x80));

            yield return new Case("radial-gradient",
                "<svg width='40' height='40'><defs><radialGradient id='r'><stop offset='0' stop-color='yellow'/>" +
                "<stop offset='1' stop-color='navy'/></radialGradient></defs><rect width='40' height='40' fill='url(#r)'/></svg>",
                40, 40, minCoverage: 0.99)
                .Probe(20, 20, SKColors.Yellow, tolerance: 16);

            yield return new Case("path-arcs",
                "<svg width='40' height='40'><path d='M20 4 A16 16 0 1 0 36 20 L20 20 Z' fill='teal'/></svg>",
                40, 40, minCoverage: 0.2);

            yield return new Case("curves-qcubic",
                "<svg width='60' height='30'><path d='M4 25 Q 20 -10 35 25 T 58 18' fill='none' stroke='darkgreen' stroke-width='3'/></svg>",
                60, 30);

            yield return new Case("transform-translate-shape",
                "<svg width='40' height='40'><rect x='2' y='2' width='6' height='6' fill='red' transform='translate(12 8)'/></svg>",
                40, 40)
                .Probe(15, 11, SKColors.Red)
                .Probe(3, 3, SKColors.Transparent);

            yield return new Case("transform-rotate-center",
                "<svg width='50' height='50'><rect x='20' y='10' width='10' height='6' fill='red' transform='rotate(180 30 18)'/></svg>",
                50, 50)
                .Probe(35, 23, SKColors.Red)
                .Probe(25, 13, SKColors.Transparent);

            yield return new Case("nested-transforms",
                "<svg width='40' height='40'><g transform='translate(10 10)'>" +
                "<rect width='5' height='5' fill='red' transform='translate(5 5)'/></g></svg>",
                40, 40)
                .Probe(16, 16, SKColors.Red)
                .Probe(2, 2, SKColors.Transparent);

            yield return new Case("group-opacity-isolated",
                "<svg width='30' height='30'><g opacity='0.6'><rect width='30' height='15' fill='red'/>" +
                "<rect y='15' width='30' height='15' fill='lime'/></g></svg>",
                30, 30, minCoverage: 0.99);

            yield return new Case("fill-stroke-opacity",
                "<svg width='30' height='30'><rect x='4' y='4' width='22' height='22' fill='#0000ff' fill-opacity='0.5' " +
                "stroke='#ff0000' stroke-opacity='0.75' stroke-width='3'/></svg>",
                30, 30)
                .Probe(15, 15, new SKColor(0x00, 0x00, 0xff, 127), tolerance: 16)
                .Probe(4, 15, new SKColor(0xda, 0x00, 0x24, 223), tolerance: 16)
                .Probe(0, 0, SKColors.Transparent);

            yield return new Case("currentcolor-inherited",
                "<svg width='24' height='24' color='crimson'><rect x='2' y='2' width='20' height='20' fill='currentColor'/></svg>",
                24, 24)
                .Probe(12, 12, SKColors.Crimson);

            yield return new Case("inline-style-overrides",
                "<svg width='20' height='20'><rect width='20' height='20' fill='red' style='fill:gold'/></svg>",
                20, 20, minCoverage: 0.99)
                .Probe(10, 10, SKColors.Gold);

            yield return new Case("use-symbol",
                "<svg width='40' height='40'><symbol id='s'><rect width='10' height='10' fill='purple'/></symbol>" +
                "<use href='#s' x='6' y='8'/><use href='#s' x='22' y='20'/></svg>",
                40, 40)
                .Probe(11, 13, SKColors.Purple)
                .Probe(27, 25, SKColors.Purple)
                .Probe(1, 1, SKColors.Transparent);

            yield return new Case("clip-path",
                "<svg width='30' height='20'><clipPath id='c'><rect width='15' height='20'/></clipPath>" +
                "<rect width='30' height='20' fill='red' clip-path='url(#c)'/></svg>",
                30, 20)
                .Probe(7, 10, SKColors.Red)
                .Probe(25, 10, SKColors.Transparent);

            yield return new Case("viewbox-par-xMinYMin",
                "<svg width='40' height='40' viewBox='0 0 80 20' preserveAspectRatio='xMinYMin'>" +
                "<rect width='80' height='20' fill='red'/></svg>",
                40, 40)
                .Probe(20, 5, SKColors.Red)
                .Probe(20, 30, SKColors.Transparent);

            yield return new Case("viewbox-par-slice",
                "<svg width='20' height='20' viewBox='0 0 40 20' preserveAspectRatio='xMidYMid slice'>" +
                "<rect width='40' height='20' fill='dodgerblue'/></svg>",
                20, 20, minCoverage: 0.99)
                .Probe(10, 10, SKColors.DodgerBlue);

            yield return new Case("image-data-uri",
                ImageCase(), 20, 20, minCoverage: 0.10)
                .Probe(3, 4, SKColors.Transparent)
                .Probe(9, 8, SKColors.Red);

            yield return new Case("malformed-recoverable",
                "<svg width='20' height='20'><rect width='14' height='20' fill='red'><unclosed></rect>" +
                "</weird><circle cx='17' cy='10' r='3' fill='blue'/></svg>",
                20, 20)
                .Probe(5, 5, SKColors.Red)
                .Probe(17, 10, SKColors.Blue);

            yield return new Case("basic-text",
                // Font rasterization is platform-dependent, so this carries a
                // semantic foreground expectation rather than pixel comparison.
                "<svg width='40' height='20'><text x='2' y='14' font-size='12'>Hi</text></svg>",
                40, 20);
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
        public void FirstPartyRender_MatchesSpecDerivedExpectation(Case item)
        {
            using var result = new FenSvgRenderer().Render(item.Svg);

            Assert.True(result.Success, $"[{item.Name}] {result.ErrorMessage}");
            Assert.NotNull(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.False(result.RequiresFallback, $"[{item.Name}] {string.Join("; ", result.Warnings)}");
            Assert.False(result.HadResourceRejection);
            Assert.True(SvgRenderResult.IsAdmissible(result));

            Assert.Equal(item.Width, result.Bitmap.Width);
            Assert.Equal(item.Height, result.Bitmap.Height);
            Assert.Equal(item.Width, (int)result.Width);
            Assert.Equal(item.Height, (int)result.Height);

            double coverage = ForegroundRatio(result.Bitmap);
            if (item.ForegroundExpected)
            {
                Assert.True(coverage > 0,
                    $"[{item.Name}] expected visible foreground pixels");
            }
            else
            {
                Assert.Equal(0d, coverage);
            }

            Assert.True(coverage >= item.MinCoverage,
                $"[{item.Name}] foreground coverage {coverage:F4} below floor {item.MinCoverage:F4}");

            foreach (var (x, y, expected, tolerance) in item.Probes)
            {
                SKColor actual = result.Bitmap.GetPixel(x, y);
                if (expected.Alpha == 0)
                {
                    Assert.Equal(0, actual.Alpha);
                }
                else
                {
                    AssertColorClose(item.Name, x, y, expected, actual, tolerance);
                }
            }
        }

        [Theory]
        [MemberData(nameof(Corpus))]
        public void FirstPartyRender_IsBitForBitDeterministic(Case item)
        {
            ulong first = Checksum(item.Svg);
            ulong second = Checksum(item.Svg);

            Assert.Equal(first, second);
        }

        [Fact]
        public void CorpusCoversDistinctCanonicalSurfaces()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Case item in BuildCorpus())
            {
                Assert.True(names.Add(item.Name), $"duplicate corpus case '{item.Name}'");
            }

            Assert.True(names.Count >= 19, $"expected a broad corpus, found {names.Count}");
        }

        private static ulong Checksum(string svg)
        {
            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);

            ulong hash = 1469598103934665603UL;
            for (int y = 0; y < result.Bitmap.Height; y++)
            {
                for (int x = 0; x < result.Bitmap.Width; x++)
                {
                    SKColor color = result.Bitmap.GetPixel(x, y);
                    hash ^= (uint)(color.Alpha << 24 | color.Red << 16 | color.Green << 8 | color.Blue);
                    hash *= 1099511628211UL;
                }
            }

            return hash;
        }

        private static double ForegroundRatio(SKBitmap bitmap)
        {
            int foreground = 0;
            int total = bitmap.Width * bitmap.Height;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha > 0)
                    {
                        foreground++;
                    }
                }
            }

            return total == 0 ? 0d : (double)foreground / total;
        }

        private static void AssertColorClose(
            string name,
            int x,
            int y,
            SKColor expected,
            SKColor actual,
            int tolerance)
        {
            Assert.True(Math.Abs(actual.Alpha - expected.Alpha) <= tolerance,
                $"[{name}] probe ({x},{y}) alpha {actual.Alpha} != {expected.Alpha}");
            Assert.True(Math.Abs(actual.Red - expected.Red) <= tolerance,
                $"[{name}] probe ({x},{y}) red {actual.Red} != {expected.Red}");
            Assert.True(Math.Abs(actual.Green - expected.Green) <= tolerance,
                $"[{name}] probe ({x},{y}) green {actual.Green} != {expected.Green}");
            Assert.True(Math.Abs(actual.Blue - expected.Blue) <= tolerance,
                $"[{name}] probe ({x},{y}) blue {actual.Blue} != {expected.Blue}");
        }
    }
}
