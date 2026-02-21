using System.Collections.Generic;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class ScrollDamageComputerTests
    {
        private static readonly SKRect Viewport = new SKRect(0, 0, 1280, 800);
        private static readonly SKSize ViewportSize = new SKSize(Viewport.Width, Viewport.Height);

        [Fact]
        public void NoScrollChange_ProducesNoDamage()
        {
            var computer = new ScrollDamageComputer();

            var damage = computer.ComputeScrollDamage(
                previousScrollY: 100f,
                currentScrollY: 100f,
                previousViewport: ViewportSize,
                currentViewport: Viewport);

            Assert.Empty(damage);
        }

        [Fact]
        public void SmallScrollDelta_ProducesTwoStrips()
        {
            var computer = new ScrollDamageComputer(stripThresholdPx: 120f);

            // Scroll down 40 px — well below the 120 px strip threshold.
            var damage = computer.ComputeScrollDamage(
                previousScrollY: 0f,
                currentScrollY: 40f,
                previousViewport: ViewportSize,
                currentViewport: Viewport);

            // Expect two non-empty strips, both within the viewport.
            Assert.Equal(2, damage.Count);

            foreach (var strip in damage)
            {
                Assert.True(strip.Width > 0 && strip.Height > 0, "Strip must be non-empty.");
                // Both strips must be within the viewport bounds.
                Assert.True(strip.Left >= Viewport.Left - 0.01f);
                Assert.True(strip.Top >= Viewport.Top - 0.01f);
                Assert.True(strip.Right <= Viewport.Right + 0.01f);
                Assert.True(strip.Bottom <= Viewport.Bottom + 0.01f);
            }

            // The exposed bottom strip should end at the viewport bottom.
            var exposedStrip = damage[0];
            Assert.Equal(Viewport.Bottom, exposedStrip.Bottom, precision: 1);

            // The outgoing top strip should start at the viewport top.
            var outgoingStrip = damage[1];
            Assert.Equal(Viewport.Top, outgoingStrip.Top, precision: 1);
        }

        [Fact]
        public void LargeScrollDelta_ProducesFullViewportDamage()
        {
            var computer = new ScrollDamageComputer(stripThresholdPx: 120f);

            // Scroll down 500 px — far beyond the strip threshold.
            var damage = computer.ComputeScrollDamage(
                previousScrollY: 0f,
                currentScrollY: 500f,
                previousViewport: ViewportSize,
                currentViewport: Viewport);

            Assert.Single(damage);
            Assert.Equal(Viewport, damage[0]);
        }

        [Fact]
        public void ViewportSizeChange_ProducesFullViewportDamage()
        {
            var computer = new ScrollDamageComputer();
            var newViewport = new SKRect(0, 0, 1920, 1080);

            // Even with no scroll change, a resize → full damage.
            var damage = computer.ComputeScrollDamage(
                previousScrollY: 200f,
                currentScrollY: 200f,
                previousViewport: ViewportSize,     // 1280×800
                currentViewport: newViewport);       // 1920×1080

            Assert.Single(damage);
            Assert.Equal(newViewport, damage[0]);
        }
    }
}
