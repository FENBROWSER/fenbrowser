using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering.Css
{
    public class ViewportUnitsTests
    {
        [Fact]
        public void TryPx_ViewportUnits_ResolveCorrectly()
        {
            // By default:
            // MediaViewportWidth = 1920
            // MediaViewportHeight = 1080

            // 1. Regular viewport units
            Assert.True(CssLoader.TryPx("10vw", out double vwPx));
            Assert.Equal(192.0, vwPx);

            Assert.True(CssLoader.TryPx("10vh", out double vhpPx));
            Assert.Equal(108.0, vhpPx);

            // 2. Dynamic/Small/Large viewport height units (dvh, svh, lvh)
            Assert.True(CssLoader.TryPx("10dvh", out double dvhPx));
            Assert.Equal(108.0, dvhPx);

            Assert.True(CssLoader.TryPx("50svh", out double svhPx));
            Assert.Equal(540.0, svhPx);

            Assert.True(CssLoader.TryPx("100lvh", out double lvhPx));
            Assert.Equal(1080.0, lvhPx);

            // 3. Dynamic/Small/Large viewport width units (dvw, svw, lvw)
            Assert.True(CssLoader.TryPx("10dvw", out double dvwPx));
            Assert.Equal(192.0, dvwPx);

            Assert.True(CssLoader.TryPx("50svw", out double svwPx));
            Assert.Equal(960.0, svwPx);

            Assert.True(CssLoader.TryPx("100lvw", out double lvwPx));
            Assert.Equal(1920.0, lvwPx);
        }
    }
}
