using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Backends;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// A rounded box whose border-colors differ per side is how essentially every
    /// CSS loading spinner is built - reCAPTCHA's is a 36px box with a 6px border,
    /// a 50% radius, and 'border-bottom-color: transparent;
    /// border-left-color: transparent', spun by a keyframe. Painting each side as
    /// a straight line clipped to the outer shape turns the visible half of that
    /// ring into two flat bars, so the wheel renders with square ends instead of
    /// as an arc.
    /// </summary>
    public sealed class RoundedBorderArcTests
    {
        private const int Size = 36;
        private const float BorderWidth = 6f;
        private const float Radius = 18f;

        [Fact]
        public void HalfTransparentRoundedBorder_PaintsAnArcThroughTheCorner()
        {
            using var bitmap = RenderSpinnerRing();

            // The middle of the ring at 45 degrees, in the corner the two painted
            // sides share. On an arc it is inside the ring; with the sides drawn
            // as straight bars it falls between them - below the top bar and
            // inside the right one - and nothing paints it.
            // Two points on the ring inside the shared corner, one either side of
            // the miter. Both sit past where a straight top bar ends (y > 6) and
            // short of where a straight right bar starts (x < 30), so the old
            // clipped-line painting left both blank.
            Assert.True(
                IsPainted(bitmap, 28, 7),
                "The top side should carry its half of the corner curve.");
            Assert.True(
                IsPainted(bitmap, 29, 8),
                "The right side should carry its half of the corner curve.");

            // The same point one quarter turn either way, still on the painted
            // half, so a chord-shaped top or right side would miss these too.
            Assert.True(IsPainted(bitmap, 18, 3), "Top of the ring should be painted.");
            Assert.True(IsPainted(bitmap, 33, 18), "Right of the ring should be painted.");
        }

        [Fact]
        public void HalfTransparentRoundedBorder_LeavesTheTransparentSidesUnpainted()
        {
            using var bitmap = RenderSpinnerRing();

            Assert.False(IsPainted(bitmap, 7, 29), "The transparent corner must stay open.");
            Assert.False(IsPainted(bitmap, 18, 33), "border-bottom-color: transparent must not paint.");
            Assert.False(IsPainted(bitmap, 3, 18), "border-left-color: transparent must not paint.");
        }

        [Fact]
        public void RoundedBorder_LeavesThePaddingBoxUnpainted()
        {
            using var bitmap = RenderSpinnerRing();

            // The ring is the area between the border box and the padding box; a
            // filled disc would swallow the checkbox the spinner sits over.
            Assert.False(IsPainted(bitmap, 18, 18), "The centre is inside the padding box.");
            Assert.False(IsPainted(bitmap, 18, 10), "8px down is inside the padding box.");
        }

        // Segmented styles repeat along the edge and cannot be expressed as a
        // solid fill of the ring, so they keep the straight-line path. They must
        // still paint something.
        [Fact]
        public void DashedSideOnARoundedBox_StillPaints()
        {
            using var bitmap = new SKBitmap(Size, Size);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            new SkiaRenderBackend(canvas).DrawBorder(
                new SKRect(0, 0, Size, Size),
                SpinnerBorder(dashedTop: true));

            var painted = 0;
            for (var x = 0; x < Size; x++)
            {
                for (var y = 0; y < Size; y++)
                {
                    if (IsPainted(bitmap, x, y))
                    {
                        painted++;
                    }
                }
            }

            Assert.True(painted > 0, "A dashed rounded border should still paint.");
        }

        private static SKBitmap RenderSpinnerRing()
        {
            var bitmap = new SKBitmap(Size, Size);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            new SkiaRenderBackend(canvas).DrawBorder(
                new SKRect(0, 0, Size, Size),
                SpinnerBorder(dashedTop: false));

            return bitmap;
        }

        private static BorderStyle SpinnerBorder(bool dashedTop)
        {
            var blue = new SKColor(0x4d, 0x90, 0xfe);
            return new BorderStyle
            {
                TopWidth = BorderWidth,
                RightWidth = BorderWidth,
                BottomWidth = BorderWidth,
                LeftWidth = BorderWidth,
                TopColor = blue,
                RightColor = blue,
                BottomColor = SKColors.Transparent,
                LeftColor = SKColors.Transparent,
                TopStyle = dashedTop ? "dashed" : "solid",
                RightStyle = "solid",
                BottomStyle = "solid",
                LeftStyle = "solid",
                TopLeftRadius = new SKPoint(Radius, Radius),
                TopRightRadius = new SKPoint(Radius, Radius),
                BottomRightRadius = new SKPoint(Radius, Radius),
                BottomLeftRadius = new SKPoint(Radius, Radius)
            };
        }

        // Antialiasing puts partial coverage on the ring's edges, so read a pixel
        // as painted only once it is mostly opaque.
        private static bool IsPainted(SKBitmap bitmap, int x, int y) =>
            bitmap.GetPixel(x, y).Alpha > 128;
    }
}
