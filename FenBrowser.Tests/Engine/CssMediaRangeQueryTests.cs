using System;
using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssMediaRangeQueryTests
    {
        [Fact]
        public void MediaRange_ValueFirst_GreaterEqual_AppliesInsideRange()
        {
            Assert.True(EvaluateMediaQuery("(1200px >= width)", 800));
        }

        [Fact]
        public void MediaRange_ValueFirst_GreaterEqual_DoesNotApplyOutsideRange()
        {
            Assert.False(EvaluateMediaQuery("(1200px >= width)", 1400));
        }

        [Fact]
        public void MediaRange_ValueFirst_StrictGreater_FormIsSupported()
        {
            Assert.True(EvaluateMediaQuery("(1200px > width)", 1100));
            Assert.False(EvaluateMediaQuery("(1200px > width)", 1200));
        }

        [Fact]
        public void MediaQuery_MaxWidth_DoesNotApplyOnDesktopViewport()
        {
            Assert.False(EvaluateMediaQuery("(max-width: 833px)", 1920));
            Assert.True(EvaluateMediaQuery("(max-width: 833px)", 800));
        }

        [Fact]
        public void MediaQuery_OnlyScreenAnd_MaxWidth_IsEvaluated()
        {
            Assert.False(EvaluateMediaQuery("only screen and (max-width: 833px)", 1920));
            Assert.True(EvaluateMediaQuery("only screen and (max-width: 833px)", 800));
        }

        [Fact]
        public void MediaQuery_MinMaxConjunction_WithCompactAnd_IsEvaluated()
        {
            Assert.True(EvaluateMediaQuery("(min-width:736px)and (max-width:1069px)", 900));
            Assert.False(EvaluateMediaQuery("(min-width:736px)and (max-width:1069px)", 1280));
        }

        [Fact]
        public void MediaQuery_WithAtMediaPrefix_IsEvaluated()
        {
            Assert.False(EvaluateMediaQuery("@media (max-width: 833px)", 1920));
            Assert.True(EvaluateMediaQuery("@media (max-width: 833px)", 720));
        }

        private static bool EvaluateMediaQuery(string query, double viewportWidth)
        {
            var method = typeof(CssLoader).GetMethod("EvaluateMediaQuery", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var result = method.Invoke(null, new object[] { query, viewportWidth });
            return result is bool b && b;
        }
    }
}
