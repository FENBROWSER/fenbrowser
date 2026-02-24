using System;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Compatibility;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("WebCompatibilityInterventionRegistry")]
    public class WebCompatibilityInterventionRegistryTests
    {
        [Fact]
        public void Apply_WhenEnabledAndPredicateMatches_AppliesAndTracksMetrics()
        {
            var registry = WebCompatibilityInterventionRegistry.Instance;
            registry.Clear();
            registry.SetGlobalEnabled(true);

            const string interventionId = "layout-intrinsic-sizing-001";
            registry.Register(new WebCompatibilityIntervention(
                interventionId,
                WebCompatibilityBehaviorClass.IntrinsicSizing,
                WebCompatibilityPipelineStage.Cascade,
                DateTimeOffset.UtcNow.AddDays(7),
                context => context.TagNameUpper == "IMG",
                context => context.Style.TextDecoration = "none",
                "Normalize intrinsic image decoration fallback while standards fix lands."));

            var element = new Element("img");
            var style = new CssComputed();
            bool applied = registry.Apply(new WebCompatibilityInterventionContext(
                WebCompatibilityPipelineStage.Cascade,
                element,
                style,
                DateTimeOffset.UtcNow));

            var metrics = registry.GetMetricsSnapshot().First(m => m.Id == interventionId);

            Assert.True(applied);
            Assert.Equal("none", style.TextDecoration);
            Assert.Equal(1, metrics.Evaluations);
            Assert.Equal(1, metrics.Applications);
            Assert.Equal(0, metrics.ExpiredSkips);
            Assert.Equal(0, metrics.DisabledSkips);
        }

        [Fact]
        public void Apply_WhenRegistryDisabled_DoesNotApplyAndTracksDisabledSkips()
        {
            var registry = WebCompatibilityInterventionRegistry.Instance;
            registry.Clear();
            registry.SetGlobalEnabled(false);

            const string interventionId = "layout-float-placement-001";
            registry.Register(new WebCompatibilityIntervention(
                interventionId,
                WebCompatibilityBehaviorClass.FloatPlacement,
                WebCompatibilityPipelineStage.Layout,
                DateTimeOffset.UtcNow.AddDays(7),
                _ => true,
                context => context.Style.Display = "none",
                "Safety fallback for float band alignment regressions."));

            var style = new CssComputed();
            bool applied = registry.Apply(new WebCompatibilityInterventionContext(
                WebCompatibilityPipelineStage.Layout,
                new Element("div"),
                style,
                DateTimeOffset.UtcNow));

            var metrics = registry.GetMetricsSnapshot().First(m => m.Id == interventionId);

            Assert.False(applied);
            Assert.NotEqual("none", style.Display);
            Assert.Equal(0, metrics.Evaluations);
            Assert.Equal(0, metrics.Applications);
            Assert.Equal(0, metrics.ExpiredSkips);
            Assert.Equal(1, metrics.DisabledSkips);
            registry.SetGlobalEnabled(true);
        }

        [Fact]
        public void Apply_WhenInterventionExpired_DoesNotApplyAndTracksExpiredSkips()
        {
            var registry = WebCompatibilityInterventionRegistry.Instance;
            registry.Clear();
            registry.SetGlobalEnabled(true);

            const string interventionId = "layout-containing-block-001";
            registry.Register(new WebCompatibilityIntervention(
                interventionId,
                WebCompatibilityBehaviorClass.ContainingBlockResolution,
                WebCompatibilityPipelineStage.Layout,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                _ => true,
                context => context.Style.Display = "none",
                "Expired fix candidate for containing-block mismatch."));

            var style = new CssComputed();
            bool applied = registry.Apply(new WebCompatibilityInterventionContext(
                WebCompatibilityPipelineStage.Layout,
                new Element("div"),
                style,
                DateTimeOffset.UtcNow));

            var metrics = registry.GetMetricsSnapshot().First(m => m.Id == interventionId);

            Assert.False(applied);
            Assert.NotEqual("none", style.Display);
            Assert.Equal(1, metrics.Evaluations);
            Assert.Equal(0, metrics.Applications);
            Assert.Equal(1, metrics.ExpiredSkips);
            Assert.Equal(0, metrics.DisabledSkips);
        }

        [Fact]
        public void Register_WhenIdAlreadyExists_Throws()
        {
            var registry = WebCompatibilityInterventionRegistry.Instance;
            registry.Clear();
            registry.SetGlobalEnabled(true);

            const string interventionId = "layout-form-control-sizing-001";
            var intervention = new WebCompatibilityIntervention(
                interventionId,
                WebCompatibilityBehaviorClass.FormControlSizing,
                WebCompatibilityPipelineStage.Layout,
                DateTimeOffset.UtcNow.AddDays(7),
                _ => false,
                _ => { },
                "Tracks form control sizing parity fallback.");

            registry.Register(intervention);

            Assert.Throws<InvalidOperationException>(() => registry.Register(intervention));
        }
    }

    [CollectionDefinition("WebCompatibilityInterventionRegistry", DisableParallelization = true)]
    public class WebCompatibilityInterventionRegistryCollection
    {
    }
}
