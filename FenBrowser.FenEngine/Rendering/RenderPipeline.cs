// SpecRef: FenBrowser pipeline stage authority contract (parse -> DOM -> style -> layout -> paint -> raster)
// CapabilityId: PIPELINE-STAGE-AUTHORITY-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
using System;
using FenBrowser.Core.Logging;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    public enum RenderPhase
    {
        Idle,
        Layout,
        LayoutFrozen,
        Paint,
        Composite,
        Present
    }

    public static class RenderPipeline
    {
        private static RenderPhase _currentPhase = RenderPhase.Idle;
        private static long _frameSequence;
        private static DateTime _frameStartedUtc;
        private static TimeSpan _lastFrameDuration;
        private static bool _firstLayoutLogged;
        private static bool _firstPaintLogged;
        private static int _ownerThreadId;
        private static readonly object s_stateLock = new object();

        public static bool StrictInvariants { get; set; } = true;
        public static TimeSpan FrameBudget { get; set; } = TimeSpan.FromMilliseconds(16.67);

        public static RenderPhase CurrentPhase
        {
            get
            {
                lock (s_stateLock)
                {
                    return _currentPhase;
                }
            }
        }

        public static long FrameSequence
        {
            get
            {
                lock (s_stateLock)
                {
                    return _frameSequence;
                }
            }
        }

        public static TimeSpan LastFrameDuration
        {
            get
            {
                lock (s_stateLock)
                {
                    return _lastFrameDuration;
                }
            }
        }

        public static bool LastFrameExceededBudget
        {
            get
            {
                lock (s_stateLock)
                {
                    return _lastFrameDuration > FrameBudget;
                }
            }
        }

        public static void Reset()
        {
            lock (s_stateLock)
            {
                _currentPhase = RenderPhase.Idle;
                _frameStartedUtc = default;
                _lastFrameDuration = TimeSpan.Zero;
                _firstLayoutLogged = false;
                _firstPaintLogged = false;
                _ownerThreadId = 0;
            }
        }

        public static void EnterLayout()
        {
            lock (s_stateLock)
            {
                EnsureThreadAffinity(nameof(EnterLayout), acquireIfUnclaimed: true);
                RequirePhase(RenderPhase.Idle, nameof(EnterLayout));
                _frameSequence++;
                _frameStartedUtc = DateTime.UtcNow;
                _currentPhase = RenderPhase.Layout;
            }
        }

        public static void EndLayout()
        {
            lock (s_stateLock)
            {
                EnsureThreadAffinity(nameof(EndLayout));
                RequirePhase(RenderPhase.Layout, nameof(EndLayout));
                _currentPhase = RenderPhase.LayoutFrozen;
                if (!_firstLayoutLogged)
                {
                    _firstLayoutLogged = true;
                    EngineLogCompat.Info("[DOC][INFO] First layout complete", LogCategory.Layout);
                }
            }
        }

        public static void EnterPaint()
        {
            lock (s_stateLock)
            {
                EnsureThreadAffinity(nameof(EnterPaint));
                RequirePhase(RenderPhase.LayoutFrozen, nameof(EnterPaint));
                _currentPhase = RenderPhase.Paint;
            }
        }

        public static void EndPaint()
        {
            lock (s_stateLock)
            {
                EnsureThreadAffinity(nameof(EndPaint));
                RequirePhase(RenderPhase.Paint, nameof(EndPaint));
                _currentPhase = RenderPhase.Composite;
                if (!_firstPaintLogged)
                {
                    _firstPaintLogged = true;
                    EngineLogCompat.Info("[DOC][INFO] First paint submitted", LogCategory.Paint);
                }
            }
        }

        public static void EnterPresent()
        {
            lock (s_stateLock)
            {
                EnsureThreadAffinity(nameof(EnterPresent));
                RequirePhase(RenderPhase.Composite, nameof(EnterPresent));
                _currentPhase = RenderPhase.Present;
            }
        }

        public static void EndFrame()
        {
            lock (s_stateLock)
            {
                EnsureThreadAffinity(nameof(EndFrame));
                RequirePhase(RenderPhase.Present, nameof(EndFrame));
                if (_frameStartedUtc != default)
                {
                    _lastFrameDuration = DateTime.UtcNow - _frameStartedUtc;
                    EngineLogCompat.Debug(
                        $"[PIPELINE][SUMMARY] frame={_frameSequence} durationMs={_lastFrameDuration.TotalMilliseconds:F2} phase={_currentPhase}",
                        LogCategory.Rendering);
                    if (_lastFrameDuration > FrameBudget)
                    {
                        EngineLogCompat.Warn($"[PIPELINE] Frame {_frameSequence} exceeded budget: {_lastFrameDuration.TotalMilliseconds:F2}ms > {FrameBudget.TotalMilliseconds:F2}ms", LogCategory.Performance);
                    }
                }

                _currentPhase = RenderPhase.Idle;
                _frameStartedUtc = default;
                _ownerThreadId = 0;
            }
        }

        public static void AssertPhase(RenderPhase expected)
        {
            lock (s_stateLock)
            {
                if (_currentPhase != expected)
                {
                    HandleViolation($"AssertPhase failed. Expected {expected}, actual {_currentPhase}", null);
                }
            }
        }

        /// <summary>
        /// Assert that we are NOT in a specific phase (e.g., No layout during Paint).
        /// </summary>
        public static void AssertNotPhase(RenderPhase forbidden)
        {
            lock (s_stateLock)
            {
                if (_currentPhase == forbidden)
                {
                    HandleViolation($"AssertNotPhase failed. Forbidden phase {forbidden} is active.", null);
                }
            }
        }
        
        /// <summary>
        /// S-02: Assert we are drawing to the correct layer.
        /// </summary>
        public static void AssertLayerSeparation(bool isDebugOrOverlay)
        {
            lock (s_stateLock)
            {
                if (isDebugOrOverlay && _currentPhase != RenderPhase.Composite && _currentPhase != RenderPhase.Present)
                {
                    HandleViolation($"Debug/overlay drawing must happen in Composite/Present. Actual: {_currentPhase}", null);
                }
            }
        }

        private static void RequirePhase(RenderPhase expected, string operation)
        {
            if (_currentPhase != expected)
            {
                HandleViolation($"{operation} requires phase {expected}, actual {_currentPhase}", expected);
            }
        }

        private static void EnsureThreadAffinity(string operation, bool acquireIfUnclaimed = false)
        {
            var currentThreadId = Environment.CurrentManagedThreadId;
            if (_ownerThreadId == 0 && acquireIfUnclaimed)
            {
                _ownerThreadId = currentThreadId;
                return;
            }

            if (_ownerThreadId == 0 || _ownerThreadId == currentThreadId)
            {
                return;
            }

            HandleViolation(
                $"{operation} called on thread {currentThreadId}, but active frame is owned by thread {_ownerThreadId}",
                null);
        }

        private static void HandleViolation(string message, RenderPhase? recoverTo)
        {
            if (StrictInvariants)
            {
                throw new RenderPipelineInvariantException(message);
            }

            EngineLogCompat.Warn($"[PIPELINE RECOVERY] {message}", LogCategory.Rendering);
            if (recoverTo.HasValue)
            {
                _currentPhase = recoverTo.Value;
            }
        }
    }

    public sealed class RenderPipelineInvariantException : InvalidOperationException
    {
        public RenderPipelineInvariantException(string message) : base(message)
        {
        }
    }
}
