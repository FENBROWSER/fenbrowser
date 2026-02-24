using System;
using System.Collections.Generic;

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

        public PaintCompositingStabilityController(
            int burstThreshold = 6,
            int forcedRebuildFrames = 4,
            TimeSpan burstWindow = default,
            Func<long> tickProvider = null)
        {
            _burstThreshold = Math.Max(1, burstThreshold);
            _forcedRebuildFrames = Math.Max(1, forcedRebuildFrames);
            _windowTicks = (burstWindow == default ? TimeSpan.FromMilliseconds(250) : burstWindow).Ticks;
            _tickProvider = tickProvider ?? (() => DateTime.UtcNow.Ticks);
        }

        public bool ShouldForcePaintRebuild => _forcedRebuildFramesRemaining > 0;

        public int ForceRebuildFramesRemaining => _forcedRebuildFramesRemaining;

        public int RecentInvalidationCount => _invalidationTicks.Count;

        public void ObserveFrame(bool hasPaintInvalidationSignal, bool rebuiltPaintTree)
        {
            long now = _tickProvider();
            TrimWindow(now);

            if (hasPaintInvalidationSignal)
            {
                _invalidationTicks.Enqueue(now);
                TrimWindow(now);

                if (_invalidationTicks.Count >= _burstThreshold)
                {
                    _forcedRebuildFramesRemaining = Math.Max(_forcedRebuildFramesRemaining, _forcedRebuildFrames);
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
        }

        private void TrimWindow(long now)
        {
            while (_invalidationTicks.Count > 0 && (now - _invalidationTicks.Peek()) > _windowTicks)
            {
                _invalidationTicks.Dequeue();
            }
        }
    }
}
