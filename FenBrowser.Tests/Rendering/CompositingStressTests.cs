using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    /// <summary>
    /// PC-5: Extended compositing stress regression coverage.
    /// Exercises animation-heavy, rapid-invalidation, mixed-damage, and
    /// frame-budget scenarios that are required for the 90+ production gate.
    /// </summary>
    [Collection("Engine Tests")]
    public class CompositingStressTests
    {
        private static readonly SKRect Viewport = new SKRect(0, 0, 1280, 800);
        private static readonly SKSize ViewportSize = new SKSize(Viewport.Width, Viewport.Height);

        // ----------------------------------------------------------------
        // 1. Rapid-invalidation: stability controller produces forced rebuilds
        //    for exactly the configured window then stops.
        // ----------------------------------------------------------------
        [Fact]
        public void RapidInvalidation_StabilityController_BoundedForcedRebuildWindow()
        {
            const int forcedRebuildFrames = 4;
            var controller = new PaintCompositingStabilityController(
                burstThreshold: 6,
                forcedRebuildFrames: forcedRebuildFrames,
                burstWindow: TimeSpan.FromMilliseconds(250));

            // Fire 6 rapid invalidation signals → triggers forced-rebuild mode.
            for (int i = 0; i < 6; i++)
            {
                controller.ObserveFrame(hasPaintInvalidationSignal: true, rebuiltPaintTree: false);
            }

            Assert.True(controller.ShouldForcePaintRebuild, "Controller must be in forced-rebuild mode after burst.");

            // Simulate rebuild frames → counter decrements.
            int rebuildsConsumed = 0;
            for (int i = 0; i < forcedRebuildFrames + 2; i++)
            {
                if (controller.ShouldForcePaintRebuild)
                {
                    controller.ObserveFrame(hasPaintInvalidationSignal: false, rebuiltPaintTree: true);
                    rebuildsConsumed++;
                }
                else
                {
                    break;
                }
            }

            Assert.Equal(forcedRebuildFrames, rebuildsConsumed);
            Assert.False(controller.ShouldForcePaintRebuild, "Forced rebuild must stop after budget is consumed.");
        }

        // ----------------------------------------------------------------
        // 2. Mixed damage: normalization policy merges excess regions.
        // ----------------------------------------------------------------
        [Fact]
        public void MixedDamage_NormalizationPolicy_MergesExcessRegions()
        {
            const int maxNormalized = 4;
            var policy = new DamageRegionNormalizationPolicy(
                inflatePx: 0f,
                mergeGapPx: 0f,
                maxNormalizedRegions: maxNormalized);

            // 20 small scattered rects that do NOT overlap (gap > merge tolerance).
            var raw = new List<SKRect>();
            for (int i = 0; i < 20; i++)
            {
                float x = i * 60f;
                raw.Add(new SKRect(x, 0, x + 5f, 5f));
            }

            var normalized = policy.Normalize(raw, Viewport);

            // Must collapse to a single union rect when above the max.
            Assert.True(normalized.Count <= maxNormalized,
                $"Expected ≤{maxNormalized} regions, got {normalized.Count}.");
        }

        // ----------------------------------------------------------------
        // 3. Without base frame, damage rasterization policy must return false.
        // ----------------------------------------------------------------
        [Fact]
        public void AnimationBurst_DamageRasterization_NotActiveWithoutBaseFrame()
        {
            var policy = new DamageRasterizationPolicy(maxAreaRatio: 0.55f, maxDamageRegions: 8);
            var smallDamage = new List<SKRect> { new SKRect(0, 0, 100, 100) };

            bool result = policy.ShouldUseDamageRasterization(
                hasBaseFrame: false,
                damageRegions: smallDamage,
                viewport: Viewport,
                damageAreaRatio: out _);

            Assert.False(result, "Damage rasterization must be disabled when no base frame is seeded.");
        }

        // ----------------------------------------------------------------
        // 4. Large damage area ≥ max ratio → policy rejects damage rasterization.
        // ----------------------------------------------------------------
        [Fact]
        public void LargeSceneDamage_ExceedsAreaRatio_FallsBackToFullRaster()
        {
            var policy = new DamageRasterizationPolicy(maxAreaRatio: 0.55f, maxDamageRegions: 8);

            // Damage covers 90% of the viewport.
            var hugeDamage = new List<SKRect>
            {
                new SKRect(0, 0, Viewport.Width * 0.9f, Viewport.Height)
            };

            bool result = policy.ShouldUseDamageRasterization(
                hasBaseFrame: true,
                damageRegions: hugeDamage,
                viewport: Viewport,
                damageAreaRatio: out float ratio);

            Assert.False(result, "Policy must reject damage rasterization when area ratio exceeds max.");
            Assert.True(ratio >= 0.55f, $"Reported ratio ({ratio:P0}) should be ≥ 55%.");
        }

        // ----------------------------------------------------------------
        // 5. Scroll + tree-diff damage combined, normalized to viewport.
        // ----------------------------------------------------------------
        [Fact]
        public void ScrollAndDamageCombined_MergedCorrectly()
        {
            var scrollComputer = new ScrollDamageComputer(stripThresholdPx: 120f);
            var normPolicy = new DamageRegionNormalizationPolicy(
                inflatePx: 1f, mergeGapPx: 1f, maxNormalizedRegions: 16);

            // Small scroll down 30 px.
            var scrollDamage = scrollComputer.ComputeScrollDamage(0f, 30f, ViewportSize, Viewport);

            // Tree-diff damage in the center of the screen.
            var treeDamage = new List<SKRect> { new SKRect(300, 200, 600, 400) };

            // Merge.
            var allDamage = new List<SKRect>(scrollDamage);
            allDamage.AddRange(treeDamage);

            var normalized = normPolicy.Normalize(allDamage, Viewport);

            // All regions must be inside the viewport.
            foreach (var region in normalized)
            {
                Assert.True(region.Left >= Viewport.Left - 1f);
                Assert.True(region.Top >= Viewport.Top - 1f);
                Assert.True(region.Right <= Viewport.Right + 1f);
                Assert.True(region.Bottom <= Viewport.Bottom + 1f);
            }

            Assert.True(normalized.Count >= 1, "Must have at least one normalized damage region.");
        }

        // ----------------------------------------------------------------
        // 6. Base-frame reuse rejected when scroll offset changed.
        // ----------------------------------------------------------------
        [Fact]
        public void BaseFrameReuse_ScrollChange_Rejected()
        {
            bool canReuse = BaseFrameReusePolicy.CanReuseBaseFrame(
                hasBaseFrame: true,
                previousViewport: ViewportSize,
                currentViewport: ViewportSize,
                previousScrollY: 0f,
                currentScrollY: 50f,    // 50 px scroll → exceeds default epsilon 0.5 px
                viewportEpsilon: 0.5f,
                scrollEpsilon: 0.5f);

            Assert.False(canReuse, "Base frame must not be reused when scroll position changed.");
        }

        // ----------------------------------------------------------------
        // 7. Adaptive policy suppresses forced rebuilds during sustained overload.
        // ----------------------------------------------------------------
        [Fact]
        public void RapidInvalidation_FrameBudgetExceeded_SuppressesExtraRebuilds()
        {
            var budget = TimeSpan.FromMilliseconds(16.67);
            // alpha=1.0 for no smoothing — deterministic test.
            var adaptivePolicy = new FrameBudgetAdaptivePolicy(emaAlpha: 1.0, sustainedThreshold: 4);

            // Simulate 6 consecutive over-budget frames.
            for (int i = 0; i < 6; i++)
            {
                adaptivePolicy.ObserveFrame(TimeSpan.FromMilliseconds(33));
                adaptivePolicy.ShouldSuppressForcedRebuild(budget); // accumulate counter
            }

            bool suppressed = adaptivePolicy.ShouldSuppressForcedRebuild(budget);
            Assert.True(suppressed,
                "Adaptive policy must suppress forced rebuilds during sustained frame-budget overrun.");
        }
    }
}
