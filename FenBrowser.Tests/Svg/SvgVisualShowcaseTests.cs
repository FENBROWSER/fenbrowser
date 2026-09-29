using System;
using System.Collections.Generic;
using System.IO;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// Single deliberately complex but first-party-supported showcase document.
    /// The corpus exercises nested transforms, isolated group opacity, solid and
    /// gradient paint, a repeating pattern paint server, clip path, luminance
    /// mask, markers, text, use/symbol instantiation, a bounded filter chain and
    /// a frozen SMIL interval, then proves the result is admissible, non-blank,
    /// and reproducible.
    /// <para>
    /// Rendering reads process-wide typeface resolution, and the determinism case
    /// is bit-for-bit, so the class runs inside
    /// <see cref="SvgRendererBackendStateCollection"/>.
    /// </para>
    /// </summary>
    [Collection(SvgRendererBackendStateCollection.Name)]
    public sealed class SvgVisualShowcaseTests
    {
        private const int CanvasWidth = 1200;
        private const int CanvasHeight = 800;
        private const double DocumentTimeSeconds = 2.5d;
        private const double MinimumOpaqueCoverage = 0.95d;
        private const int MinimumDistinctColors = 4096;
        private const string ArtifactFileName = "svg-hardest-showcase.png";

        private const string Showcase = "<svg xmlns='http://www.w3.org/2000/svg' width='1200' height='800' viewBox='0 0 1200 800'>" +
            "<defs>" +
            "<linearGradient id='sky' x1='0' y1='0' x2='0' y2='1'>" +
            "<stop offset='0' stop-color='#071426'/>" +
            "<stop offset='.5' stop-color='#17456f'/>" +
            "<stop offset='1' stop-color='#3f7d4f'/>" +
            "</linearGradient>" +
            "<linearGradient id='bar' x1='0' y1='0' x2='1' y2='0'>" +
            "<stop offset='0' stop-color='#ff5f6d'/>" +
            "<stop offset='1' stop-color='#ffc371'/>" +
            "</linearGradient>" +
            "<linearGradient id='fade' x1='0' y1='0' x2='1' y2='0'>" +
            "<stop offset='0' stop-color='#ffffff'/>" +
            "<stop offset='1' stop-color='#000000'/>" +
            "</linearGradient>" +
            "<radialGradient id='sun' cx='.5' cy='.5' r='.5'>" +
            "<stop offset='0' stop-color='#fff3b0'/>" +
            "<stop offset='.6' stop-color='#ffd166' stop-opacity='.85'/>" +
            "<stop offset='1' stop-color='#ffd166' stop-opacity='0'/>" +
            "</radialGradient>" +
            "<pattern id='weave' patternUnits='userSpaceOnUse' width='20' height='20' patternTransform='rotate(30)'>" +
            "<rect width='20' height='20' fill='#0d2033'/>" +
            "<path d='M0 0V20' stroke='#2b6f9e' stroke-width='5'/>" +
            "<circle cx='10' cy='10' r='2' fill='#7fd1e8'/>" +
            "</pattern>" +
            "<clipPath id='round'><circle cx='960' cy='600' r='180'/></clipPath>" +
            "<mask id='sweep' maskUnits='userSpaceOnUse' x='0' y='0' width='1200' height='800'>" +
            "<rect x='0' y='0' width='1200' height='800' fill='url(#fade)'/>" +
            "</mask>" +
            "<marker id='tip' markerUnits='userSpaceOnUse' markerWidth='18' markerHeight='18' " +
            "refX='15' refY='9' orient='auto' overflow='visible'>" +
            "<path d='M0 1 L16 9 L0 17 Z' fill='#ffe066' stroke='#7a4b00' stroke-width='1.5'/>" +
            "</marker>" +
            "<marker id='dot' markerUnits='userSpaceOnUse' markerWidth='10' markerHeight='10' refX='5' refY='5'>" +
            "<circle cx='5' cy='5' r='4' fill='#ff4d6d'/></marker>" +
            "<filter id='soft' filterUnits='userSpaceOnUse' x='40' y='40' width='1120' height='720' " +
            "color-interpolation-filters='sRGB'><feGaussianBlur stdDeviation='3'/></filter>" +
            "<filter id='duo' filterUnits='userSpaceOnUse' x='0' y='0' width='1200' height='800' " +
            "color-interpolation-filters='sRGB'>" +
            "<feColorMatrix type='saturate' values='.35'/>" +
            "<feComponentTransfer>" +
            "<feFuncR type='linear' slope='1.1' intercept='-.02'/>" +
            "<feFuncG type='linear' slope='1'/>" +
            "<feFuncB type='linear' slope='.9' intercept='.05'/>" +
            "</feComponentTransfer>" +
            "</filter>" +
            "<symbol id='chip' viewBox='0 0 40 40'>" +
            "<rect x='2' y='2' width='36' height='36' rx='6' fill='#12263a' stroke='#7fd1e8' stroke-width='2'/>" +
            "<path d='M10 26 L18 12 L24 22 L30 16' fill='none' stroke='#ffd166' stroke-width='3' " +
            "stroke-linecap='round' stroke-linejoin='round'/>" +
            "</symbol>" +
            "<g id='pip'><circle r='6' fill='#8ecae6'/><circle r='2.5' fill='#023047'/></g>" +
            "</defs>" +
            "<rect x='0' y='0' width='1200' height='800' fill='url(#sky)'/>" +
            "<circle cx='960' cy='180' r='260' fill='url(#sun)'/>" +
            "<g transform='translate(40 470)'><g transform='rotate(-6)'><g opacity='0.85'>" +
            "<rect x='0' y='0' width='520' height='280' fill='url(#weave)' stroke='#7fd1e8' stroke-width='3'/>" +
            "<rect x='20' y='20' width='480' height='240' fill='none' stroke='url(#bar)' stroke-width='10'/>" +
            "</g></g></g>" +
            "<g mask='url(#sweep)'>" +
            "<path d='M60 300 C220 240 300 380 460 320 C600 268 700 150 880 190' fill='none' " +
            "stroke='#ffd166' stroke-width='14' stroke-linecap='round'/>" +
            "</g>" +
            "<g clip-path='url(#round)'>" +
            "<rect x='780' y='420' width='360' height='360' fill='url(#weave)'/>" +
            "<rect x='780' y='420' width='360' height='360' fill='#0b2b45' fill-opacity='.45'/>" +
            "</g>" +
            "<path d='M60 740 C300 660 500 780 720 700' fill='none' stroke='#f1faee' stroke-width='6' " +
            "marker-start='url(#dot)' marker-mid='url(#dot)' marker-end='url(#tip)'/>" +
            "<use href='#chip' x='60' y='60' width='120' height='120'/>" +
            "<use href='#chip' x='210' y='60' width='90' height='90'/>" +
            "<use href='#pip' x='640' y='120'/><use href='#pip' x='700' y='120'/><use href='#pip' x='760' y='120'/>" +
            "<g filter='url(#soft)' opacity='0.9'>" +
            "<rect x='380' y='56' width='200' height='120' rx='12' fill='#ef476f'/>" +
            "<rect x='430' y='92' width='200' height='120' rx='12' fill='#06d6a0' fill-opacity='0.8'/>" +
            "</g>" +
            "<g filter='url(#duo)'>" +
            "<g transform='translate(940 300) rotate(12)'>" +
            "<polygon points='0,0 90,20 70,110 -20,80' fill='url(#bar)' stroke='#023047' stroke-width='4'/>" +
            "</g>" +
            "<rect x='0' y='690' width='1200' height='110' fill='#023047' fill-opacity='0.75'/>" +
            "</g>" +
            "<text x='60' y='250' font-family='sans-serif' font-size='54' font-weight='bold' fill='#f1faee'>FenBrowser SVG</text>" +
            "<text x='60' y='292' font-family='monospace' font-size='26' fill='#7fd1e8'>first-party renderer showcase</text>" +
            "<rect x='860' y='700' width='60' height='40' rx='6' fill='#ffd166'>" +
            "<animate attributeName='x' from='640' to='860' begin='1s' dur='4s' fill='freeze'/></rect>" +
            "<circle cx='0' cy='120' r='18' fill='#ff4d6d'>" +
            "<animateColor attributeName='fill' from='#ff4d6d' to='#8ecae6' dur='4s' fill='freeze'/>" +
            "<animate attributeName='cx' from='120' to='1180' dur='4s' fill='freeze'/>" +
            "</circle>" +
            "</svg>";

        [Fact]
        public void HardestSupportedShowcase_RendersAdmissiblyAndWritesArtifact()
        {
            using var result = RenderShowcase();

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result), SvgRenderResult.DescribeRejection(result));
            Assert.NotNull(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(result.HadResourceRejection);
            Assert.Empty(result.Warnings);

            Assert.Equal(CanvasWidth, result.Bitmap.Width);
            Assert.Equal(CanvasHeight, result.Bitmap.Height);
            Assert.Equal(CanvasWidth, (int)result.Width);
            Assert.Equal(CanvasHeight, (int)result.Height);

            double coverage = OpaqueRatio(result.Bitmap);
            Assert.True(
                coverage >= MinimumOpaqueCoverage,
                $"non-transparent coverage {coverage:F4} below floor {MinimumOpaqueCoverage:F4}");
            Assert.True(
                DistinctColorCount(result.Bitmap) >= MinimumDistinctColors,
                "showcase is visually flat");

            string artifactPath = WriteArtifact(result.Bitmap);

            Assert.True(File.Exists(artifactPath), artifactPath);
            Assert.True(new FileInfo(artifactPath).Length > 0, artifactPath);
        }

        [Fact]
        public void HardestSupportedShowcase_RepeatsBitForBit()
        {
            using var first = RenderShowcase();
            using var second = RenderShowcase();

            Assert.True(first.Success, first.ErrorMessage);
            Assert.True(second.Success, second.ErrorMessage);

            Assert.Equal(FrameChecksum(first.Bitmap), FrameChecksum(second.Bitmap));
            Assert.Equal(EncodePngBytes(first.Bitmap), EncodePngBytes(second.Bitmap));
        }

        private static SvgRenderResult RenderShowcase()
        {
            var limits = SvgRenderLimits.Default;
            limits.MaxRenderTimeMs = 20000;
            return new FenSvgRenderer().Render(new SvgRenderRequest(Showcase, limits)
            {
                DocumentTimeSeconds = DocumentTimeSeconds
            });
        }

        private static string WriteArtifact(SKBitmap bitmap)
        {
            string logsDirectory = Path.Combine(RepoRoot(), "logs");
            Directory.CreateDirectory(logsDirectory);
            string artifactPath = Path.Combine(logsDirectory, ArtifactFileName);
            using (SKData data = EncodePng(bitmap))
            using (FileStream stream = File.Create(artifactPath))
            {
                data.SaveTo(stream);
            }

            return artifactPath;
        }

        private static SKData EncodePng(SKBitmap bitmap)
        {
            using var image = SKImage.FromBitmap(bitmap);
            return image.Encode(SKEncodedImageFormat.Png, 100);
        }

        private static byte[] EncodePngBytes(SKBitmap bitmap)
        {
            using SKData data = EncodePng(bitmap);
            return data.ToArray();
        }

        private static ulong FrameChecksum(SKBitmap bitmap)
        {
            ulong hash = 1469598103934665603UL;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    SKColor color = bitmap.GetPixel(x, y);
                    hash ^= (uint)(color.Alpha << 24 | color.Red << 16 | color.Green << 8 | color.Blue);
                    hash *= 1099511628211UL;
                }
            }

            return hash;
        }

        private static double OpaqueRatio(SKBitmap bitmap)
        {
            int opaque = 0;
            int total = bitmap.Width * bitmap.Height;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha > 0)
                    {
                        opaque++;
                    }
                }
            }

            return total == 0 ? 0d : (double)opaque / total;
        }

        private static int DistinctColorCount(SKBitmap bitmap)
        {
            var seen = new HashSet<uint>();
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    SKColor color = bitmap.GetPixel(x, y);
                    seen.Add((uint)(color.Alpha << 24 | color.Red << 16 | color.Green << 8 | color.Blue));
                }
            }

            return seen.Count;
        }

        private static string RepoRoot()
        {
            var probe = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(probe); i++)
            {
                if (File.Exists(Path.Combine(probe, "FenBrowser.sln")))
                {
                    return probe;
                }

                probe = Path.GetDirectoryName(probe);
            }

            return AppContext.BaseDirectory;
        }
    }
}
