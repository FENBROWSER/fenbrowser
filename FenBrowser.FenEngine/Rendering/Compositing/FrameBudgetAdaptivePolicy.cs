using System;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Tracks smoothed frame duration via exponential moving average (EMA) and
    /// suppresses <see cref="PaintCompositingStabilityController"/> forced-rebuild
    /// requests when the renderer is already sustaining budget overruns.
    ///
    /// Rationale:
    ///   When the GPU is overloaded (every frame > budget), triggering extra forced
    ///   paint-tree rebuilds from the stability controller compounds the problem —
    ///   rebuilds consume CPU time that cannot be recovered within the frame window.
    ///   Suppressing them during sustained overload is the correct tradeoff: we accept
    ///   slightly stale paint trees rather than making frame drops worse.
    ///
    /// Suppression criteria:
    ///   smoothedDuration > budget AND sustainedOverBudgetFrames >= threshold.
    ///
    /// Recovery:
    ///   Once smoothedDuration falls below budget, suppression lifts within one frame.
    /// </summary>
    public sealed class FrameBudgetAdaptivePolicy
    {
        private readonly double _emaAlpha;
        private readonly int _sustainedThreshold;

        private double _smoothedDurationMs;
        private int _sustainedOverBudgetCount;
        private bool _initialized;

        /// <param name="emaAlpha">
        /// EMA smoothing factor (0 = infinite memory, 1 = no smoothing).
        /// Default 0.15 — reacts to trends over ~6-7 frames.
        /// </param>
        /// <param name="sustainedThreshold">
        /// Number of consecutive over-budget frames before suppression activates.
        /// Default 4 frames (~67 ms at 60 fps).
        /// </param>
        public FrameBudgetAdaptivePolicy(double emaAlpha = 0.15, int sustainedThreshold = 4)
        {
            _emaAlpha = Math.Clamp(emaAlpha, 0.01, 1.0);
            _sustainedThreshold = Math.Max(1, sustainedThreshold);
        }

        /// <summary>Smoothed frame duration in milliseconds.</summary>
        public double SmoothedDurationMs => _smoothedDurationMs;

        /// <summary>True when forced paint rebuilds should be suppressed.</summary>
        public bool IsSuppressing => _sustainedOverBudgetCount >= _sustainedThreshold;

        /// <summary>
        /// Record the duration of the completed frame and update internal state.
        /// Must be called once per rendered frame, after <c>RenderPipeline.EndFrame()</c>.
        /// </summary>
        public void ObserveFrame(TimeSpan frameDuration)
        {
            double ms = frameDuration.TotalMilliseconds;
            if (!_initialized)
            {
                _smoothedDurationMs = ms;
                _initialized = true;
            }
            else
            {
                _smoothedDurationMs = _emaAlpha * ms + (1.0 - _emaAlpha) * _smoothedDurationMs;
            }
        }

        /// <summary>
        /// Returns <c>true</c> when forced paint-tree rebuilds should be blocked
        /// because the renderer is sustaining frame-budget pressure.
        /// </summary>
        public bool ShouldSuppressForcedRebuild(TimeSpan budget)
        {
            if (!_initialized)
            {
                return false;
            }

            bool overBudget = _smoothedDurationMs > budget.TotalMilliseconds;
            if (overBudget)
            {
                _sustainedOverBudgetCount = Math.Min(_sustainedOverBudgetCount + 1, _sustainedThreshold + 1);
            }
            else
            {
                // Recovery: reset immediately when below budget.
                _sustainedOverBudgetCount = 0;
            }

            return _sustainedOverBudgetCount >= _sustainedThreshold;
        }

        /// <summary>Reset all tracked state (e.g., on navigation or explicit reset).</summary>
        public void Reset()
        {
            _smoothedDurationMs = 0;
            _sustainedOverBudgetCount = 0;
            _initialized = false;
        }
    }
}
