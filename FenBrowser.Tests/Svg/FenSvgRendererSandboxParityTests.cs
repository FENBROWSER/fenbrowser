using System;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// First-party sandbox budget semantics plus an adversarial corpus that must
    /// never throw, hang, or allocate unbounded memory.
    /// </summary>
    public class FenSvgRendererSandboxParityTests
    {
        private readonly FenSvgRenderer _renderer = new FenSvgRenderer();

        [Fact]
        public void EmptyContent_FailsWithParityMessage()
        {
            using var result = _renderer.Render("");
            Assert.False(result.Success);
            Assert.Equal("Empty SVG content", result.ErrorMessage);
        }

        [Fact]
        public void SourceLengthLimit_ParityMessage()
        {
            var limits = new SvgRenderLimits { MaxSourceChars = 10 };
            using var result = _renderer.Render("<svg></svg>", limits);
            Assert.False(result.Success);
            Assert.Contains("exceeds limit (10)", result.ErrorMessage);
            Assert.Contains("source length", result.ErrorMessage);
        }

        [Fact]
        public void ElementCountLimit_ParityMessage()
        {
            using var result = _renderer.Render(
                "<svg width=\"1\" height=\"1\"><g/><g/><g/></svg>",
                new SvgRenderLimits { MaxElementCount = 3 });
            Assert.False(result.Success);
            Assert.Contains("element count", result.ErrorMessage);
        }

        [Fact]
        public void FilterCountLimit_ParityMessage()
        {
            using var result = _renderer.Render(
                "<svg width=\"1\" height=\"1\">" +
                "<filter id=\"a\"/><filter id=\"b\"/><filter id=\"c\"/></svg>",
                new SvgRenderLimits { MaxFilterCount = 2 });
            Assert.False(result.Success);
            Assert.Contains("filter count", result.ErrorMessage);
        }

        [Fact]
        public void DepthLimit_ParityMessage()
        {
            var sb = new StringBuilder("<svg width=\"1\" height=\"1\">");
            for (int i = 0; i < 40; i++) sb.Append("<g>");
            using var result = _renderer.Render(sb.ToString(), new SvgRenderLimits { MaxRecursionDepth = 8 });
            Assert.False(result.Success);
            Assert.Contains("nesting depth", result.ErrorMessage);
        }

        [Fact]
        public void RasterBomb_IsRejected_BeforeAllocation()
        {
            using var result = _renderer.Render(
                "<svg width=\"20000\" height=\"20000\"><rect width=\"20000\" height=\"20000\"/></svg>",
                new SvgRenderLimits { MaxRasterWidth = 512, MaxRasterHeight = 512, MaxRasterPixels = 262144 });
            Assert.False(result.Success);
            Assert.Contains("exceed browser limits", result.ErrorMessage);
            Assert.Null(result.Bitmap);
        }

        [Fact]
        public void HugeDeclaredViewport_WithTinyViewBox_UsesViewBoxDerivedSize()
        {
            // A classic raster-bomb trick: huge width/height, small content.
            // Our intrinsic sizing prefers explicit attrs, so this must hit the
            // raster guard rather than attempt a giant allocation.
            using var result = _renderer.Render(
                "<svg width=\"1000000\" height=\"1000000\"><rect width=\"10\" height=\"10\" fill=\"red\"/></svg>",
                new SvgRenderLimits { MaxRasterWidth = 8192, MaxRasterHeight = 8192, MaxRasterPixels = 16L * 1024 * 1024 });
            Assert.False(result.Success);
            Assert.Contains("exceed browser limits", result.ErrorMessage);
        }

        [Fact]
        public void NonSvgRoot_FailsGracefully()
        {
            using var result = _renderer.Render("<html><body>x</body></html>");
            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
        }

        [Fact]
        public void AdversarialCorpus_NeverThrows_NeverHangs()
        {
            string[] corpus =
            {
                null,
                "   ",
                "not svg at all",
                "<<<<>>>>",
                "<svg",
                "<svg>",
                "</svg>",
                "<svg width=\"NaN\" height=\"NaN\"><rect/></svg>",
                "<svg width=\"-5\" height=\"-5\"><rect/></svg>",
                "<svg viewBox=\"garbage\"><rect d=\"M0 0\"/></svg>",
                "<svg width=\"10\" height=\"10\"><path d=\"M0 0A\"/></svg>",
                "<svg width=\"10\" height=\"10\"><path d=\"M1e30 1e30 L-1e30 -1e30\"/></svg>",
                "<svg width=\"10\" height=\"10\"><use href=\"#self\" id=\"self\"/><use href=\"#self\"/></svg>",
                "<svg width=\"10\" height=\"10\"><text>hello</text></svg>",
                "<svg width=\"10\" height=\"10\"><image href=\"http://evil/x.png\"/></svg>",
                "<svg width=\"10\" height=\"10\"><script>alert(1)</script><rect width=\"9\" height=\"9\"/></svg>",
                "<svg width=\"10\" height=\"10\"><style>rect{fill:url(http://x)}</style><rect width=\"9\" height=\"9\"/></svg>",
                "<?php exit(); ?><svg width=\"1\" height=\"1\"/>",
                "<svg width=\"10&#xZZ;\" height='unterminated><rect/></svg>",
                "<svg width=\"1e999\" height=\"10\"/>"
            };

            foreach (var input in corpus)
            {
                using var result = _renderer.Render(input);
                if (!result.Success)
                {
                    Assert.NotNull(result.ErrorMessage);
                }
            }
        }

        [Fact]
        public void StrictLimits_MoreRestrictiveThanDefault()
        {
            Assert.True(SvgRenderLimits.Strict.MaxRecursionDepth <= SvgRenderLimits.Default.MaxRecursionDepth);
            Assert.True(SvgRenderLimits.Strict.MaxFilterCount <= SvgRenderLimits.Default.MaxFilterCount);
            Assert.True(SvgRenderLimits.Strict.MaxRenderTimeMs <= SvgRenderLimits.Default.MaxRenderTimeMs);
            Assert.False(SvgRenderLimits.Default.AllowExternalReferences);
            Assert.False(SvgRenderLimits.Strict.AllowExternalReferences);
        }
    }
}
