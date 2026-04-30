using System;
using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class SkiaRendererObjectPositionTests
    {
        [Fact]
        public void CalculateDestRect_ObjectPositionKeywords_ControlCoverAlignment()
        {
            var method = typeof(SkiaRenderer).GetMethod("CalculateDestRect", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var bounds = new SKRect(0, 0, 200, 100);
            var src = new SKRect(0, 0, 400, 100);

            var left = (SKRect)method!.Invoke(null, new object[] { bounds, src, "cover", "left" })!;
            var center = (SKRect)method!.Invoke(null, new object[] { bounds, src, "cover", "center" })!;
            var right = (SKRect)method!.Invoke(null, new object[] { bounds, src, "cover", "right" })!;

            Assert.Equal(0f, left.Left);
            Assert.Equal(-100f, center.Left);
            Assert.Equal(-200f, right.Left);
            Assert.Equal(100f, left.Height);
            Assert.Equal(100f, center.Height);
            Assert.Equal(100f, right.Height);
        }

        [Fact]
        public void CalculateDestRect_ObjectPositionPercent_OffsetsContainPlacement()
        {
            var method = typeof(SkiaRenderer).GetMethod("CalculateDestRect", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var bounds = new SKRect(0, 0, 200, 200);
            var src = new SKRect(0, 0, 200, 100);

            var rect = (SKRect)method!.Invoke(null, new object[] { bounds, src, "contain", "50% 30%" })!;

            Assert.Equal(0f, rect.Left);
            Assert.InRange(Math.Abs(rect.Top - 30f), 0f, 0.01f);
            Assert.Equal(200f, rect.Width);
            Assert.Equal(100f, rect.Height);
        }
    }
}
