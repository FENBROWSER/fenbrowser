using System.Collections.Generic;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class DamageRegionNormalizationPolicyTests
    {
        [Fact]
        public void Normalize_ClampsInflatesAndSnapsOutward()
        {
            var policy = new DamageRegionNormalizationPolicy(inflatePx: 1f, mergeGapPx: 0f, maxNormalizedRegions: 16);
            var viewport = new SKRect(0, 0, 100, 100);

            var normalized = policy.Normalize(
                new List<SKRect> { new SKRect(10.2f, 10.2f, 20.8f, 20.8f) },
                viewport);

            Assert.Single(normalized);
            Assert.Equal(new SKRect(9, 9, 22, 22), normalized[0]);
        }

        [Fact]
        public void Normalize_MergesNearbyRegions()
        {
            var policy = new DamageRegionNormalizationPolicy(inflatePx: 0f, mergeGapPx: 1f, maxNormalizedRegions: 16);
            var viewport = new SKRect(0, 0, 100, 100);

            var normalized = policy.Normalize(
                new List<SKRect>
                {
                    new SKRect(0, 0, 10, 10),
                    new SKRect(10.5f, 0, 20, 10),
                },
                viewport);

            Assert.Single(normalized);
            Assert.Equal(new SKRect(0, 0, 20, 10), normalized[0]);
        }

        [Fact]
        public void Normalize_OverBudgetCollapsesToSingleUnionRegion()
        {
            var policy = new DamageRegionNormalizationPolicy(inflatePx: 0f, mergeGapPx: 0f, maxNormalizedRegions: 2);
            var viewport = new SKRect(0, 0, 100, 100);

            var normalized = policy.Normalize(
                new List<SKRect>
                {
                    new SKRect(0, 0, 10, 10),
                    new SKRect(20, 20, 30, 30),
                    new SKRect(40, 40, 50, 50),
                },
                viewport);

            Assert.Single(normalized);
            Assert.Equal(new SKRect(0, 0, 50, 50), normalized[0]);
        }

        [Fact]
        public void Normalize_DropsRegionsOutsideViewport()
        {
            var policy = new DamageRegionNormalizationPolicy();
            var viewport = new SKRect(0, 0, 100, 100);

            var normalized = policy.Normalize(
                new List<SKRect> { new SKRect(200, 200, 300, 300) },
                viewport);

            Assert.Empty(normalized);
        }

        [Fact]
        public void Normalize_ReturnsRegionsInStableTopLeftOrder()
        {
            var policy = new DamageRegionNormalizationPolicy(inflatePx: 0f, mergeGapPx: 0f, maxNormalizedRegions: 16);
            var viewport = new SKRect(0, 0, 100, 100);

            var normalized = policy.Normalize(
                new List<SKRect>
                {
                    new SKRect(50, 10, 60, 20),
                    new SKRect(10, 10, 20, 20),
                    new SKRect(10, 5, 20, 8),
                },
                viewport);

            Assert.Equal(3, normalized.Count);
            Assert.Equal(new SKRect(10, 5, 20, 8), normalized[0]);
            Assert.Equal(new SKRect(10, 10, 20, 20), normalized[1]);
            Assert.Equal(new SKRect(50, 10, 60, 20), normalized[2]);
        }
    }
}
