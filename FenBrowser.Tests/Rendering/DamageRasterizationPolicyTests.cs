using System.Collections.Generic;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class DamageRasterizationPolicyTests
    {
        [Fact]
        public void ShouldUseDamageRasterization_RequiresBaseFrame()
        {
            var policy = new DamageRasterizationPolicy();
            var viewport = new SKRect(0, 0, 800, 600);
            var damage = new List<SKRect> { new SKRect(0, 0, 10, 10) };

            var useDamage = policy.ShouldUseDamageRasterization(false, damage, viewport, out var ratio);

            Assert.False(useDamage);
            Assert.Equal(0f, ratio);
        }

        [Fact]
        public void ShouldUseDamageRasterization_UsesSmallDamageSet()
        {
            var policy = new DamageRasterizationPolicy(maxAreaRatio: 0.55f, maxDamageRegions: 8);
            var viewport = new SKRect(0, 0, 1000, 1000);
            var damage = new List<SKRect> { new SKRect(0, 0, 100, 100) }; // 1%

            var useDamage = policy.ShouldUseDamageRasterization(true, damage, viewport, out var ratio);

            Assert.True(useDamage);
            Assert.InRange(ratio, 0.009f, 0.011f);
        }

        [Fact]
        public void ShouldUseDamageRasterization_FallsBackForLargeDamage()
        {
            var policy = new DamageRasterizationPolicy(maxAreaRatio: 0.55f, maxDamageRegions: 8);
            var viewport = new SKRect(0, 0, 1000, 1000);
            var damage = new List<SKRect> { new SKRect(0, 0, 900, 900) }; // 81%

            var useDamage = policy.ShouldUseDamageRasterization(true, damage, viewport, out var ratio);

            Assert.False(useDamage);
            Assert.InRange(ratio, 0.80f, 0.82f);
        }

        [Fact]
        public void ShouldUseDamageRasterization_FallsBackForExcessiveRegionCount()
        {
            var policy = new DamageRasterizationPolicy(maxAreaRatio: 0.55f, maxDamageRegions: 2);
            var viewport = new SKRect(0, 0, 500, 500);
            var damage = new List<SKRect>
            {
                new SKRect(0, 0, 10, 10),
                new SKRect(20, 20, 30, 30),
                new SKRect(40, 40, 50, 50),
            };

            var useDamage = policy.ShouldUseDamageRasterization(true, damage, viewport, out var ratio);

            Assert.False(useDamage);
            Assert.Equal(0f, ratio);
        }
    }
}
