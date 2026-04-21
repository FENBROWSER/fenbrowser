using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Compatibility
{
    public enum WebCompatibilityPipelineStage
    {
        Cascade,
        Layout,
        Paint,
        Scripting,
        Networking
    }

    public enum WebCompatibilityBehaviorClass
    {
        IntrinsicSizing,
        ReplacedElementSizing,
        FloatPlacement,
        ContainingBlockResolution,
        PercentageHeightChains,
        FormControlSizing,
        LegacyDisplayDefaults,
        TextMetrics
    }

    public sealed class WebCompatibilityInterventionContext
    {
        public WebCompatibilityInterventionContext(
            WebCompatibilityPipelineStage stage,
            Element element,
            CssComputed style,
            DateTimeOffset timestampUtc)
        {
            Stage = stage;
            Element = element;
            Style = style;
            TimestampUtc = timestampUtc;
        }

        public WebCompatibilityPipelineStage Stage { get; }
        public Element Element { get; }
        public CssComputed Style { get; }
        public DateTimeOffset TimestampUtc { get; }
        public string TagNameUpper => Element?.TagName?.ToUpperInvariant() ?? string.Empty;
    }

    public sealed class WebCompatibilityIntervention
    {
        public WebCompatibilityIntervention(
            string id,
            WebCompatibilityBehaviorClass behaviorClass,
            WebCompatibilityPipelineStage stage,
            DateTimeOffset expiresAtUtc,
            Func<WebCompatibilityInterventionContext, bool> predicate,
            Action<WebCompatibilityInterventionContext> apply,
            string description)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Intervention id is required.", nameof(id));
            }

            Id = id.Trim();
            BehaviorClass = behaviorClass;
            Stage = stage;
            ExpiresAtUtc = expiresAtUtc;
            Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
            Apply = apply ?? throw new ArgumentNullException(nameof(apply));
            Description = description?.Trim() ?? string.Empty;
        }

        public string Id { get; }
        public WebCompatibilityBehaviorClass BehaviorClass { get; }
        public WebCompatibilityPipelineStage Stage { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public Func<WebCompatibilityInterventionContext, bool> Predicate { get; }
        public Action<WebCompatibilityInterventionContext> Apply { get; }
        public string Description { get; }

        public bool IsExpired(DateTimeOffset nowUtc)
        {
            return nowUtc >= ExpiresAtUtc;
        }
    }

    public sealed class WebCompatibilityInterventionMetricSnapshot
    {
        public string Id { get; init; }
        public WebCompatibilityBehaviorClass BehaviorClass { get; init; }
        public WebCompatibilityPipelineStage Stage { get; init; }
        public long Evaluations { get; init; }
        public long Applications { get; init; }
        public long ExpiredSkips { get; init; }
        public long DisabledSkips { get; init; }
    }

    internal sealed class WebCompatibilityInterventionMetricState
    {
        public long Evaluations;
        public long Applications;
        public long ExpiredSkips;
        public long DisabledSkips;
    }

    /// <summary>
    /// Centralized intervention registry: behavior-class keyed, measurable, and globally kill-switchable.
    /// </summary>
    public sealed class WebCompatibilityInterventionRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, WebCompatibilityIntervention> _interventions = new Dictionary<string, WebCompatibilityIntervention>(StringComparer.Ordinal);
        private readonly Dictionary<string, WebCompatibilityInterventionMetricState> _metrics = new Dictionary<string, WebCompatibilityInterventionMetricState>(StringComparer.Ordinal);
        private bool _enabled;

        private static readonly Lazy<WebCompatibilityInterventionRegistry> _instance =
            new Lazy<WebCompatibilityInterventionRegistry>(() => new WebCompatibilityInterventionRegistry());

        public static WebCompatibilityInterventionRegistry Instance => _instance.Value;

        private WebCompatibilityInterventionRegistry()
        {
            _enabled = ReadEnabledFromEnvironment();
        }

        public bool Enabled
        {
            get
            {
                lock (_gate)
                {
                    return _enabled;
                }
            }
        }

        public void SetGlobalEnabled(bool enabled)
        {
            lock (_gate)
            {
                _enabled = enabled;
            }
        }

        public void Register(WebCompatibilityIntervention intervention)
        {
            if (intervention == null) throw new ArgumentNullException(nameof(intervention));

            lock (_gate)
            {
                if (_interventions.ContainsKey(intervention.Id))
                {
                    throw new InvalidOperationException($"Intervention '{intervention.Id}' is already registered.");
                }

                _interventions[intervention.Id] = intervention;
                _metrics[intervention.Id] = new WebCompatibilityInterventionMetricState();
            }
        }

        public bool Unregister(string interventionId)
        {
            if (string.IsNullOrWhiteSpace(interventionId))
            {
                return false;
            }

            lock (_gate)
            {
                bool removed = _interventions.Remove(interventionId);
                _metrics.Remove(interventionId);
                return removed;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _interventions.Clear();
                _metrics.Clear();
            }
        }

        public bool Apply(WebCompatibilityInterventionContext context)
        {
            if (context == null || context.Style == null)
            {
                return false;
            }

            List<WebCompatibilityIntervention> interventions;
            bool enabled;
            lock (_gate)
            {
                interventions = _interventions.Values
                    .Where(i => i.Stage == context.Stage)
                    .ToList();
                enabled = _enabled;
            }

            bool anyApplied = false;
            var nowUtc = DateTimeOffset.UtcNow;
            foreach (var intervention in interventions)
            {
                if (!enabled)
                {
                    Increment(intervention.Id, metric => metric.DisabledSkips++);
                    continue;
                }

                Increment(intervention.Id, metric => metric.Evaluations++);

                if (intervention.IsExpired(nowUtc))
                {
                    Increment(intervention.Id, metric => metric.ExpiredSkips++);
                    continue;
                }

                bool shouldApply;
                try
                {
                    shouldApply = intervention.Predicate(context);
                }
                catch (Exception ex)
                {
                    FenBrowser.Core.EngineLogCompat.Warn($"[COMPAT] Predicate failure for '{intervention.Id}': {ex.Message}", LogCategory.CSS);
                    continue;
                }

                if (!shouldApply)
                {
                    continue;
                }

                try
                {
                    intervention.Apply(context);
                    anyApplied = true;
                    Increment(intervention.Id, metric => metric.Applications++);
                }
                catch (Exception ex)
                {
                    FenBrowser.Core.EngineLogCompat.Warn($"[COMPAT] Apply failure for '{intervention.Id}': {ex.Message}", LogCategory.CSS);
                }
            }

            return anyApplied;
        }

        public IReadOnlyList<WebCompatibilityInterventionMetricSnapshot> GetMetricsSnapshot()
        {
            lock (_gate)
            {
                return _interventions.Values
                    .Select(intervention =>
                    {
                        _metrics.TryGetValue(intervention.Id, out var metricState);
                        metricState ??= new WebCompatibilityInterventionMetricState();
                        return new WebCompatibilityInterventionMetricSnapshot
                        {
                            Id = intervention.Id,
                            BehaviorClass = intervention.BehaviorClass,
                            Stage = intervention.Stage,
                            Evaluations = metricState.Evaluations,
                            Applications = metricState.Applications,
                            ExpiredSkips = metricState.ExpiredSkips,
                            DisabledSkips = metricState.DisabledSkips
                        };
                    })
                    .ToList();
            }
        }

        private void Increment(string interventionId, Action<WebCompatibilityInterventionMetricState> update)
        {
            lock (_gate)
            {
                if (_metrics.TryGetValue(interventionId, out var metric))
                {
                    update(metric);
                }
            }
        }

        private static bool ReadEnabledFromEnvironment()
        {
            string raw = Environment.GetEnvironmentVariable("FEN_COMPAT_INTERVENTIONS");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            string normalized = raw.Trim().ToLowerInvariant();
            return normalized != "0" &&
                   normalized != "false" &&
                   normalized != "off" &&
                   normalized != "no";
        }
    }
}
