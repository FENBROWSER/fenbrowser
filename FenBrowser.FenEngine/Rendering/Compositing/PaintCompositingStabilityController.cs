using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Tracks short-window invalidation pressure and enforces bounded
    /// paint-tree rebuild windows during invalidation storms.
    /// </summary>
    public sealed class PaintCompositingStabilityController
    {
        private readonly Queue<long> _invalidationTicks = new Queue<long>();
        private readonly Func<long> _tickProvider;
        private readonly long _windowTicks;
        private readonly int _burstThreshold;
        private readonly int _forcedRebuildFrames;

        private int _forcedRebuildFramesRemaining;
        private long _lastObservedTick;
        private bool _hasObservedTick;

        public PaintCompositingStabilityController(
            int burstThreshold = 6,
            int forcedRebuildFrames = 4,
            TimeSpan burstWindow = default,
            Func<long> tickProvider = null)
        {
            _burstThreshold = Math.Max(1, burstThreshold);
            _forcedRebuildFrames = Math.Max(1, forcedRebuildFrames);

            var effectiveWindow = burstWindow == default
                ? TimeSpan.FromMilliseconds(250)
                : burstWindow;
            if (effectiveWindow <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(burstWindow), "Burst window must be positive.");
            }

            if (tickProvider == null)
            {
                _tickProvider = Stopwatch.GetTimestamp;
                _windowTicks = Math.Max(
                    1,
                    checked((long)Math.Ceiling(effectiveWindow.TotalSeconds * Stopwatch.Frequency)));
            }
            else
            {
                // Preserve the existing custom-provider contract used by callers that
                // inject TimeSpan-style ticks for deterministic simulations.
                _tickProvider = tickProvider;
                _windowTicks = effectiveWindow.Ticks;
            }
        }

        public bool ShouldForcePaintRebuild => _forcedRebuildFramesRemaining > 0;

        public int ForceRebuildFramesRemaining => _forcedRebuildFramesRemaining;

        public int RecentInvalidationCount => _invalidationTicks.Count;

        public void ObserveFrame(bool hasPaintInvalidationSignal, bool rebuiltPaintTree)
        {
            var now = _tickProvider();

            // A custom clock should be monotonic too, but fail safely if it moves
            // backwards: old invalidations must not suddenly look recent forever.
            if (_hasObservedTick && now < _lastObservedTick)
            {
                _invalidationTicks.Clear();
            }
            _lastObservedTick = now;
            _hasObservedTick = true;

            TrimWindow(now);

            if (hasPaintInvalidationSignal)
            {
                _invalidationTicks.Enqueue(now);
                TrimWindow(now);

                if (_invalidationTicks.Count >= _burstThreshold)
                {
                    _forcedRebuildFramesRemaining = Math.Max(
                        _forcedRebuildFramesRemaining,
                        _forcedRebuildFrames);
                }
            }

            if (rebuiltPaintTree && _forcedRebuildFramesRemaining > 0)
            {
                _forcedRebuildFramesRemaining--;
            }
        }

        public void Reset()
        {
            _invalidationTicks.Clear();
            _forcedRebuildFramesRemaining = 0;
            _lastObservedTick = 0;
            _hasObservedTick = false;
        }

        private void TrimWindow(long now)
        {
            while (_invalidationTicks.Count > 0)
            {
                var age = now - _invalidationTicks.Peek();
                if (age >= 0 && age <= _windowTicks)
                {
                    break;
                }

                _invalidationTicks.Dequeue();
            }
        }
    }
}
