using System;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
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
        private Node _root;
        private readonly EventLoopCoordinator _coordinator;
        private bool _treeIsDirty = false;
        
        private const double FrameBudgetMs = 16.0;

        public EngineLoop()
        {
            _coordinator = EventLoopCoordinator.Instance;
        }

        public void SetRoot(Node root)
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
// using FenBrowser.FenEngine.System;
                    var deadline = new FenBrowser.Core.Deadlines.FrameDeadline(FrameBudgetMs, "FullRender");
                    PerformRendering(deadline);
                }
                
                // 3. Check Frame Budget
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                if (elapsed > FrameBudgetMs)
                {
                   FenLogger.Warn($"[EngineLoop] Frame exceeded budget: {elapsed:F2}ms", LogCategory.Performance);
                }
            }
            // Use fully qualified name if namespace mismatch, or add using
            catch (FenBrowser.Core.Deadlines.DeadlineExceededException ex)
            {
                FenLogger.Warn($"[EngineLoop] Frame aborted due to deadline: {ex.Phase}", LogCategory.Performance);
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[EngineLoop] Critical Error in RunFrame: {ex}", LogCategory.General);
            }
        }
        
        private void PerformRendering(FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
        {
            if (_root  == null) return;

            var styleDirty = HasStyleDirty(_root);
            var layoutDirty = HasLayoutDirty(_root);
            var paintDirty = HasPaintDirty(_root);

            // Phase 1: Style Recalc - currently represented by clearing style dirty flags.
            if (styleDirty)
            {
                deadline?.Check();
                ClearDirtyRecursive(_root, InvalidationKind.Style, deadline);
            }

            // Phase 2: Layout - currently represented by clearing layout dirty flags.
            if (layoutDirty)
            {
                deadline?.Check();
                ClearDirtyRecursive(_root, InvalidationKind.Layout, deadline);
            }

            // Phase 3: Paint - currently represented by clearing paint dirty flags.
            if (paintDirty)
            {
                deadline?.Check();
                ClearDirtyRecursive(_root, InvalidationKind.Paint, deadline);
            }

            _treeIsDirty = false;
        }

        private static bool HasStyleDirty(Node node)
            => node != null && (node.StyleDirty || node.ChildStyleDirty);

        private static bool HasLayoutDirty(Node node)
            => node != null && (node.LayoutDirty || node.ChildLayoutDirty);

        private static bool HasPaintDirty(Node node)
            => node != null && (node.PaintDirty || node.ChildPaintDirty);

        private static void ClearDirtyRecursive(Node node, InvalidationKind kind, FenBrowser.Core.Deadlines.FrameDeadline deadline)
        {
            if (node == null) return;
            deadline?.Check();

            node.ClearDirty(kind);

            if (node is ContainerNode container)
            {
                for (var child = container.FirstChild; child != null; child = child.NextSibling)
                    ClearDirtyRecursive(child, kind, deadline);
            }
        }
    }
}

