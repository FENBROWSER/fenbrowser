using System;
using FenBrowser.Core;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core.EventLoop;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents the main execution loop of the browser engine.
    /// Orchestrates the sequence of: PumpEvents -> Style/Layout/Paint -> Present.
    /// Enforces Frame Budget (16ms).
    /// </summary>
    public class EngineLoop
    {
        private Element _root;
        private readonly EventLoopCoordinator _coordinator;
        private bool _treeIsDirty = false;
        
        private const double FrameBudgetMs = 16.0;

        public EngineLoop()
        {
            _coordinator = EventLoopCoordinator.Instance;
        }

        public void SetRoot(Element root)
        {
            _root = root;
            
            // Sub to document dirty event if root is Document
            if (_root is Document doc)
            {
                doc.OnTreeDirty += OnTreeDirty;
            }
            else if (_root?.OwnerDocument != null)
            {
                 _root.OwnerDocument.OnTreeDirty += OnTreeDirty;
            }
        }
        
        private void OnTreeDirty()
        {
            _treeIsDirty = true;
        }

        public void InvalidateLayout()
        {
            // Legacy hook - now just marks flag
            _treeIsDirty = true;
            _coordinator.NotifyLayoutDirty();
        }

        /// <summary>
        /// Executes a single authoritative frame.
        /// </summary>
        public void RunFrame()
        {
            var startTime = DateTime.Now;

            try
            {
                // 1. Pump Events (User Input, Timers, Network, Microtasks)
                // This delegates to Coordinator but we limit how much we process to avoid starving render
                _coordinator.ProcessNextTask(); 
                
                // Ensure microtasks are drained before rendering
                // _coordinator.PerformMicrotaskCheckpoint(); // Assumed handled by ProcessNextTask

                // 2. Check Dirty Flags & Render
                if (_treeIsDirty)
                {
                    PerformRendering();
                }
                
                // 3. Check Frame Budget
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                if (elapsed > FrameBudgetMs)
                {
                   FenLogger.Warn($"[EngineLoop] Frame exceeded budget: {elapsed:F2}ms", LogCategory.Performance);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[EngineLoop] Critical Error in RunFrame: {ex}", LogCategory.General);
            }
        }
        
        private void PerformRendering()
        {
            if (_root == null) return;
            
            // Phase 1: Style Recalc (TODO: Traverse checking StyleDirty)
            
            // Phase 2: Layout (TODO: Traverse checking LayoutDirty)
            
            // Phase 3: Paint (TODO: Traverse checking PaintDirty)
            
            // For now, we just acknowledge the flag was handled to prevent infinite loop loops in logs,
            // but in reality the legacy system (SkiaDomRenderer) acts on _coordinator.NotifyLayoutDirty() separately.
            // As we refactor, we will move the calls here.
            
            _treeIsDirty = false;
        }
    }
}
