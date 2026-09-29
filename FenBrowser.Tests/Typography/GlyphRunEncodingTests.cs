using System;
using FenBrowser.FenEngine.Typography;
using Xunit;

namespace FenBrowser.Tests.Typography
{
    public class GlyphRunEncodingTests
    {
        private static GlyphRun s_sink;

        [Fact]
        public void GlyphRun_KeepsTheGlyphPathAllocationSize()
        {
            for (var warmup = 0; warmup < 100; warmup++)
            {
                s_sink = new GlyphRun();
            }

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 1_000; iteration++)
            {
                s_sink = new GlyphRun();
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.Equal(72_000, allocated);
        }

        [Fact]
        public void WasShaped_SurvivesObjectInitializerOrder()
        {
            var flagFirst = new GlyphRun { WasShaped = true, FontSize = 16f };
            var sizeFirst = new GlyphRun { FontSize = 16f, WasShaped = true };

            Assert.True(flagFirst.WasShaped);
            Assert.True(sizeFirst.WasShaped);
            Assert.Equal(16f, flagFirst.FontSize);
            Assert.Equal(16f, sizeFirst.FontSize);
        }

        [Fact]
        public void WasShaped_DefaultsFalseAndClears()
        {
            var run = new GlyphRun { FontSize = 12f };
            Assert.False(run.WasShaped);

            run.WasShaped = true;
            Assert.True(run.WasShaped);

            run.WasShaped = false;
            Assert.False(run.WasShaped);
            Assert.Equal(12f, run.FontSize);
        }

        [Fact]
        public void FontSize_IsUnaffectedByTheShapedFlag()
        {
            var sizes = new[]
            {
                0f,
                0.01f,
                8.5f,
                16f,
                1024f,
                float.MaxValue,
                float.Epsilon,
                float.PositiveInfinity
            };

            foreach (var size in sizes)
            {
                var run = new GlyphRun { FontSize = size, WasShaped = true };
                Assert.Equal(size, run.FontSize);
                Assert.True(run.WasShaped);

                run.FontSize = size;
                Assert.Equal(size, run.FontSize);
                Assert.True(run.WasShaped);
            }

            var notANumber = new GlyphRun { FontSize = float.NaN, WasShaped = true };
            Assert.True(float.IsNaN(notANumber.FontSize));
            Assert.True(notANumber.WasShaped);
        }

        [Fact]
        public void FontSize_DoesNotDisturbOtherRunState()
        {
            var run = new GlyphRun
            {
                Glyphs = new[] { new PositionedGlyph { GlyphId = 7, X = 3f, Y = 4f, AdvanceX = 5f } },
                FontSize = 18f,
                Width = 42.5f,
                SourceText = "fen",
                WasShaped = true
            };

            run.FontSize = 20f;
            run.Width = 43.5f;
            run.SourceText = "fen2";

            Assert.Equal(20f, run.FontSize);
            Assert.Equal(43.5f, run.Width);
            Assert.Equal("fen2", run.SourceText);
            Assert.True(run.WasShaped);
            Assert.Equal(1, run.Count);
            Assert.Equal(7, run.Glyphs[0].GlyphId);
        }

        [Fact]
        public void ShapedRun_ReportsItsFontSizeAndShapedFlag()
        {
            var fontService = new SkiaFontService();
            var run = fontService.ShapeText("fenbrowser", "Arial", 16);

            Assert.Equal(16f, run.FontSize);
            Assert.True(run.WasShaped);

            run.FontSize = 24f;
            Assert.Equal(24f, run.FontSize);
            Assert.True(run.WasShaped);
        }
    }
}
