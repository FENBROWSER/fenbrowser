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
        
        public static RenderPhase CurrentPhase => _currentPhase;

        public static void Reset()
        {
            _currentPhase = RenderPhase.Idle;
        }

        public static void EnterLayout()
        {
            if (_currentPhase != RenderPhase.Idle && _currentPhase != RenderPhase.Present)
            {
                 // Soft Assert
                 if (_currentPhase == RenderPhase.Paint || _currentPhase == RenderPhase.Composite)
                 {
                     FenLogger.Warn($"[PIPELINE RECOVERY] Forced Layout entry from {_currentPhase}.");
                 }
            }
            _currentPhase = RenderPhase.Layout;
        }

        public static void EndLayout()
        {
            AssertPhase(RenderPhase.Layout);
            _currentPhase = RenderPhase.LayoutFrozen;
        }

        public static void EnterPaint()
        {
            if (_currentPhase != RenderPhase.LayoutFrozen)
                 FenLogger.Warn($"[PIPELINE RECOVERY] EnterPaint called from {_currentPhase} (Expected LayoutFrozen).");
            
            _currentPhase = RenderPhase.Paint;
        }

        public static void EndPaint()
        {
            AssertPhase(RenderPhase.Paint);
            // Move to Composite (Overlay/Debug)
            _currentPhase = RenderPhase.Composite; 
        }

        public static void EndFrame()
        {
            _currentPhase = RenderPhase.Idle;
        }

        public static void AssertPhase(RenderPhase expected)
        {
            if (_currentPhase != expected)
                FenLogger.Warn($"[PIPELINE WARNING] Expected phase {expected}, but was {_currentPhase}");
        }

        /// <summary>
        /// Assert that we are NOT in a specific phase (e.g., No layout during Paint).
        /// </summary>
        public static void AssertNotPhase(RenderPhase forbidden)
        {
            if (_currentPhase == forbidden)
                FenLogger.Warn($"[PIPELINE WARNING] Forbidden phase {forbidden} is active!");
        }
        
        /// <summary>
        /// S-02: Assert we are drawing to the correct layer.
        /// </summary>
        public static void AssertLayerSeparation(bool isDebugOrOverlay)
        {
             // If debugging/overlay, we should be in Composite phase.
             if (isDebugOrOverlay && _currentPhase != RenderPhase.Composite)
             {
                 // Allowed exception: if we are painting PaintTree debug visualizations INSIDE EnterPaint? 
                 // No, debug overlays should be strictly post-paint.
                 // throw new InvalidOperationException($"[PIPELINE VIOLATION] Debug/Overlay drawing must happen in COMPOSITE phase. Current: {_currentPhase}");
             }
        }
    }
}
