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
        [ThreadStatic]
        private static RenderPhase _currentPhase = RenderPhase.Idle;
        [ThreadStatic]
        private static long _frameSequence;
        [ThreadStatic]
        private static DateTime _frameStartedUtc;
        [ThreadStatic]
        private static TimeSpan _lastFrameDuration;
        [ThreadStatic]
        private static bool _firstLayoutLogged;
        [ThreadStatic]
        private static bool _firstPaintLogged;

        public static bool StrictInvariants { get; set; } = true;
        public static TimeSpan FrameBudget { get; set; } = TimeSpan.FromMilliseconds(16.67);

        public static RenderPhase CurrentPhase => _currentPhase;
        public static long FrameSequence => _frameSequence;
        public static TimeSpan LastFrameDuration => _lastFrameDuration;
        public static bool LastFrameExceededBudget => _lastFrameDuration > FrameBudget;

        public static void Reset()
        {
            _currentPhase = RenderPhase.Idle;
            _frameStartedUtc = default;
            _lastFrameDuration = TimeSpan.Zero;
            _firstLayoutLogged = false;
            _firstPaintLogged = false;
        }

        public static void EnterLayout()
        {
            RequirePhase(RenderPhase.Idle, nameof(EnterLayout));
            _frameSequence++;
            _frameStartedUtc = DateTime.UtcNow;
            _currentPhase = RenderPhase.Layout;
        }

        public static void EndLayout()
        {
            RequirePhase(RenderPhase.Layout, nameof(EndLayout));
            _currentPhase = RenderPhase.LayoutFrozen;
            if (!_firstLayoutLogged)
            {
                _firstLayoutLogged = true;
                EngineLogCompat.Info("[DOC][INFO] First layout complete", LogCategory.Layout);
            }
        }

        public static void EnterPaint()
        {
            RequirePhase(RenderPhase.LayoutFrozen, nameof(EnterPaint));
            _currentPhase = RenderPhase.Paint;
        }

        public static void EndPaint()
        {
            RequirePhase(RenderPhase.Paint, nameof(EndPaint));
            _currentPhase = RenderPhase.Composite;
            if (!_firstPaintLogged)
            {
                _firstPaintLogged = true;
                EngineLogCompat.Info("[DOC][INFO] First paint submitted", LogCategory.Paint);
            }
        }

        public static void EnterPresent()
        {
            RequirePhase(RenderPhase.Composite, nameof(EnterPresent));
            _currentPhase = RenderPhase.Present;
        }

        public static void EndFrame()
        {
            RequirePhase(RenderPhase.Present, nameof(EndFrame));
            if (_frameStartedUtc != default)
            {
                _lastFrameDuration = DateTime.UtcNow - _frameStartedUtc;
                EngineLogCompat.Debug(
                    $"[PIPELINE][SUMMARY] frame={FrameSequence} durationMs={_lastFrameDuration.TotalMilliseconds:F2} phase={_currentPhase}",
                    LogCategory.Rendering);
                if (LastFrameExceededBudget)
                {
                    EngineLogCompat.Warn($"[PIPELINE] Frame {FrameSequence} exceeded budget: {_lastFrameDuration.TotalMilliseconds:F2}ms > {FrameBudget.TotalMilliseconds:F2}ms", LogCategory.Performance);
                }
            }

            _currentPhase = RenderPhase.Idle;
            _frameStartedUtc = default;
        }

        public static void AssertPhase(RenderPhase expected)
        {
            if (_currentPhase != expected)
            {
                HandleViolation($"AssertPhase failed. Expected {expected}, actual {_currentPhase}", null);
            }
        }

        /// <summary>
        /// Assert that we are NOT in a specific phase (e.g., No layout during Paint).
        /// </summary>
        public static void AssertNotPhase(RenderPhase forbidden)
        {
            if (_currentPhase == forbidden)
            {
                HandleViolation($"AssertNotPhase failed. Forbidden phase {forbidden} is active.", null);
            }
        }
        
        /// <summary>
        /// S-02: Assert we are drawing to the correct layer.
        /// </summary>
        public static void AssertLayerSeparation(bool isDebugOrOverlay)
        {
            if (isDebugOrOverlay && _currentPhase != RenderPhase.Composite && _currentPhase != RenderPhase.Present)
            {
                HandleViolation($"Debug/overlay drawing must happen in Composite/Present. Actual: {_currentPhase}", null);
            }
        }

        private static void RequirePhase(RenderPhase expected, string operation)
        {
            if (_currentPhase != expected)
            {
                HandleViolation($"{operation} requires phase {expected}, actual {_currentPhase}", expected);
            }
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
