using System;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class FrameBudgetAdaptivePolicyTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(16.67);

        [Fact]
        public void BelowBudget_DoesNotSuppressRebuild()
        {
            var policy = new FrameBudgetAdaptivePolicy(sustainedThreshold: 4);

            // Feed 10 frames well under budget.
            for (int i = 0; i < 10; i++)
            {
                policy.ObserveFrame(TimeSpan.FromMilliseconds(10));
            }

            Assert.False(policy.ShouldSuppressForcedRebuild(Budget));
            Assert.False(policy.IsSuppressing);
        }

        [Fact]
        public void SustainedAboveBudget_SuppressesForcedRebuild()
        {
            // sustainedThreshold = 4: need 4 consecutive over-budget frames.
            var policy = new FrameBudgetAdaptivePolicy(emaAlpha: 1.0, sustainedThreshold: 4);

            // With alpha=1.0 (no smoothing), each observation sets smoothed = raw.
            // Feed 4 over-budget frames.
            for (int i = 0; i < 4; i++)
            {
                policy.ObserveFrame(TimeSpan.FromMilliseconds(33)); // ~2x budget
                policy.ShouldSuppressForcedRebuild(Budget);         // update counter
            }

            Assert.True(policy.ShouldSuppressForcedRebuild(Budget));
        }

        [Fact]
        public void RecoveryAfterBudgetRelief_ReenablesRebuild()
        {
            var policy = new FrameBudgetAdaptivePolicy(emaAlpha: 1.0, sustainedThreshold: 4);

            // Trigger suppression.
            for (int i = 0; i < 5; i++)
            {
                policy.ObserveFrame(TimeSpan.FromMilliseconds(33));
                policy.ShouldSuppressForcedRebuild(Budget);
            }

            Assert.True(policy.ShouldSuppressForcedRebuild(Budget), "Should be suppressing.");

            // One frame back under budget resets.
            policy.ObserveFrame(TimeSpan.FromMilliseconds(8));
            Assert.False(policy.ShouldSuppressForcedRebuild(Budget),
                "Should stop suppressing after recovering below budget.");
        }

        [Fact]
        public void EmaSmoothing_DoesNotReactToSingleSpike()
        {
            // Default alpha = 0.15: a single spike is heavily smoothed away.
            var policy = new FrameBudgetAdaptivePolicy(emaAlpha: 0.15, sustainedThreshold: 4);

            // Warm up with 20 fast frames.
            for (int i = 0; i < 20; i++)
            {
                policy.ObserveFrame(TimeSpan.FromMilliseconds(10));
            }

            // Single spike at 100 ms.
            policy.ObserveFrame(TimeSpan.FromMilliseconds(100));

            // After spike the smoothed value should still be well under budget.
            // With 20 frames at 10ms: smoothed ≈ 10ms.
            // After spike: smoothed = 0.15*100 + 0.85*10 ≈ 23.5ms, but not yet sustained.
            // Check: not yet suppressing (only 1 over-budget query).
            bool suppress = policy.ShouldSuppressForcedRebuild(Budget);

            // 1 frame above budget (count = 1) < threshold (4) → no suppression.
            Assert.False(suppress, "A single spike with EMA smoothing should not trigger suppression.");
        }
    }
}
