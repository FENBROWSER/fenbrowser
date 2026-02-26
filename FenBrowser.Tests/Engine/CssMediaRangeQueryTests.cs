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

        private static bool EvaluateMediaQuery(string query, double viewportWidth)
        {
            var method = typeof(CssLoader).GetMethod("EvaluateMediaQuery", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var result = method.Invoke(null, new object[] { query, viewportWidth });
            return result is bool b && b;
        }
    }
}
