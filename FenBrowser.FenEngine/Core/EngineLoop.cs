using System;
using FenBrowser.Core;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core.EventLoop;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents the main execution loop of the browser engine.
    /// Orchestrates the sequence of: Tasks -> Microtasks -> Rendering -> Observers -> Animation.
    /// Acts as the central heartbeat of the engine instance.
    /// </summary>
    public class EngineLoop
    {
        private Element _root;
        private bool _layoutRequested;
        private readonly EventLoopCoordinator _coordinator;

        public EngineLoop()
        {
            _coordinator = EventLoopCoordinator.Instance;
        }

        public void SetRoot(Element root)
        {
            _root = root;
        }

        public void InvalidateLayout()
        {
            _layoutRequested = true;
            _coordinator.NotifyLayoutDirty();
        }

        /// <summary>
        /// Executes a single frame/tick of the engine loop.
        /// Should be called repeatedly by the host (e.g., via a timer or message loop).
        /// </summary>
        public void RunFrame()
        {
            // Delegate core processing to the coordinator, which implements the spec-compliant order.
            // 1. Process one macro-task
            // 2. Checkpoint Microtasks
            // 3. Render (if dirty)
            // 4. Observers
            // 5. Animation Frames
            
            // Note: ProcessNextTask inside Coordinator handles the sequence for the most part,
            // but we might want to ensure we drive it correctly here.
            
            // ProcessNextTask returns true if it did work (task execution).
            // Even if it returns false (no task), we might need to run rendering/animation.
            // But Coordinator.ProcessNextTask logic includes "ProcessRenderingUpdate()" in the false branch too.
            // So calling ProcessNextTask once is roughly equivalent to one "tick" if a task exists, 
            // or just a render check if no task exists.
            
            // However, spec says "event loop processing" happens continuously.
            // Our "RunFrame" is a single pulse from the UI thread timer.
            // We should arguably process *all* ready tasks or at least one, then render.
            // For now, mapping 1:1 with Pulse to avoid freezing UI thread with too many tasks.
            
            try
            {
                _coordinator.ProcessNextTask();
                
                // Ensure microtasks are drained even if ProcessNextTask didn't (e.g. exceptions or taskless path issues)
                // verify: Coordinator.ProcessNextTask calls PerformMicrotaskCheckpoint.
                // We'll trust the Coordinator.
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[EngineLoop] Critical Error in RunFrame: {ex}", LogCategory.General);
            }
        }
    }
}
