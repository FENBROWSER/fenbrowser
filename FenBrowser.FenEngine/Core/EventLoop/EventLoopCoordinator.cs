using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Core.EventLoop
{
    /// <summary>
    /// Coordinates the main execution loop of the browser engine.
    /// Implements the execution order defined in EventLoopSemantics.md:
    /// Task → JSExecution → Microtasks → DOM Flush → Layout → Paint → Observers → Animation
    /// </summary>
    public class EventLoopCoordinator
    {
        private static EventLoopCoordinator _instance;
        public static EventLoopCoordinator Instance => _instance ??= new EventLoopCoordinator();

        private readonly TaskQueue _taskQueue = new();
        private readonly MicrotaskQueue _microtaskQueue = new();
        private readonly Queue<Action> _animationFrameCallbacks = new();
        private readonly object _animationLock = new();
        
        private bool _layoutDirty = false;
        private Action _renderCallback = null;
        private Action _observerCallback = null;

        /// <summary>
        /// Current engine phase for assertions
        /// </summary>
        public EnginePhase CurrentPhase => EnginePhaseManager.CurrentPhase;

        #region Task Scheduling

        /// <summary>
        /// Schedule a task for execution
        /// </summary>
        public void ScheduleTask(Action callback, TaskSource source, string description = null)
        {
            if (callback == null) return;
            _taskQueue.Enqueue(callback, source, description);
        }

        /// <summary>
        /// Legacy method - enqueue a task (backward compatible)
        /// </summary>
        public void EnqueueTask(Action task)
        {
            ScheduleTask(task, TaskSource.Other, "Legacy Task");
        }

        #endregion

        #region Microtask Scheduling

        /// <summary>
        /// Schedule a microtask for execution at next checkpoint
        /// </summary>
        public void ScheduleMicrotask(Action callback)
        {
            if (callback == null) return;
            _microtaskQueue.Enqueue(callback);
        }

        /// <summary>
        /// Legacy method - enqueue a microtask (backward compatible)
        /// </summary>
        public void EnqueueMicrotask(Action microtask)
        {
            ScheduleMicrotask(microtask);
        }

        #endregion

        #region Animation Frame

        /// <summary>
        /// Schedule a requestAnimationFrame callback
        /// </summary>
        public void ScheduleAnimationFrame(Action callback)
        {
            if (callback == null) return;
            lock (_animationLock)
            {
                _animationFrameCallbacks.Enqueue(callback);
            }
        }

        #endregion

        #region Rendering Integration

        /// <summary>
        /// Mark layout as dirty - will trigger render on next checkpoint
        /// </summary>
        public void NotifyLayoutDirty()
        {
            _layoutDirty = true;
        }

        /// <summary>
        /// Set the render callback to be invoked during rendering phase
        /// </summary>
        public void SetRenderCallback(Action callback)
        {
            _renderCallback = callback;
        }

        /// <summary>
        /// Set the observer callback to be invoked after rendering
        /// </summary>
        public void SetObserverCallback(Action callback)
        {
            _observerCallback = callback;
        }

        #endregion

        #region Event Loop Execution

        /// <summary>
        /// Process the next task according to spec ordering:
        /// 1. Dequeue and execute task
        /// 2. Microtask checkpoint (drain all)
        /// 3. DOM flush (if needed)
        /// 4. Rendering update (if layout dirty)
        /// 5. Observer evaluation
        /// 6. Animation frames
        /// </summary>
        public bool ProcessNextTask()
        {
            var task = _taskQueue.Dequeue();
            if (task == null)
            {
                // No task, but might still have animation frames or rendering to do
                ProcessRenderingUpdate();
                return false;
            }

            // STEP 1-2: Execute task
            EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
            try
            {
                FenLogger.Debug($"[EventLoop] Executing task: {task.Description}", LogCategory.JavaScript);
                task.Callback.Invoke();
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[EventLoop] Task Exception: {ex.Message}", LogCategory.Errors);
            }

            // STEP 3: Microtask checkpoint
            PerformMicrotaskCheckpoint();

            // STEP 4-6: Rendering update
            ProcessRenderingUpdate();
            
            EnginePhaseManager.TryEnterIdle();
            return true;
        }

        /// <summary>
        /// Perform a microtask checkpoint - drains ALL microtasks
        /// </summary>
        public void PerformMicrotaskCheckpoint()
        {
            EnginePhaseManager.EnterPhase(EnginePhase.Microtasks);
            try
            {
                _microtaskQueue.DrainAll();
            }
            finally
            {
                // Don't change phase here - let caller manage
            }
        }

        /// <summary>
        /// Process rendering update according to spec:
        /// - Layout (if dirty)
        /// - Paint
        /// - Observer evaluation
        /// - Animation frame callbacks
        /// </summary>
        public void ProcessRenderingUpdate()
        {
            // Layout phase - NO JS ALLOWED
            if (_layoutDirty && _renderCallback != null)
            {
                EnginePhaseManager.EnterPhase(EnginePhase.Layout);
                try
                {
                    FenLogger.Debug("[EventLoop] Rendering update (layout dirty)", LogCategory.Rendering);
                    _renderCallback.Invoke();
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[EventLoop] Render Exception: {ex.Message}", LogCategory.Errors);
                }
                _layoutDirty = false;
            }

            // Observer evaluation
            if (_observerCallback != null)
            {
                EnginePhaseManager.EnterPhase(EnginePhase.Observers);
                try
                {
                    _observerCallback.Invoke();
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[EventLoop] Observer Exception: {ex.Message}", LogCategory.Errors);
                }

                // Microtask checkpoint after observers
                PerformMicrotaskCheckpoint();
            }

            // Animation frame callbacks
            ProcessAnimationFrames();
        }

        /// <summary>
        /// Process all pending requestAnimationFrame callbacks
        /// </summary>
        private void ProcessAnimationFrames()
        {
            Queue<Action> callbacks;
            lock (_animationLock)
            {
                if (_animationFrameCallbacks.Count == 0)
                    return;

                // Swap to new queue so callbacks can schedule more
                callbacks = new Queue<Action>(_animationFrameCallbacks);
                _animationFrameCallbacks.Clear();
            }

            EnginePhaseManager.EnterPhase(EnginePhase.Animation);
            while (callbacks.Count > 0)
            {
                var callback = callbacks.Dequeue();
                EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
                try
                {
                    callback.Invoke();
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[EventLoop] RAF Exception: {ex.Message}", LogCategory.Errors);
                }

                // Microtask checkpoint after each RAF callback
                PerformMicrotaskCheckpoint();
            }
        }

        /// <summary>
        /// Run the event loop until no more tasks
        /// </summary>
        public void RunUntilEmpty()
        {
            while (_taskQueue.HasPendingTasks || _animationFrameCallbacks.Count > 0)
            {
                ProcessNextTask();
            }
        }

        #endregion

        #region Utility

        /// <summary>
        /// Clear all queues (for navigation/reset)
        /// </summary>
        public void Clear()
        {
            _taskQueue.Clear();
            _microtaskQueue.Clear();
            lock (_animationLock)
            {
                _animationFrameCallbacks.Clear();
            }
            _layoutDirty = false;
            FenLogger.Debug("[EventLoop] All queues cleared", LogCategory.JavaScript);
        }

        /// <summary>
        /// Reset singleton (for testing)
        /// </summary>
        public static void ResetInstance()
        {
            _instance = new EventLoopCoordinator();
        }

        // For Testing
        public int TaskCount => _taskQueue.Count;
        public int MicrotaskCount => _microtaskQueue.Count;
        public bool HasPendingTasks => _taskQueue.HasPendingTasks;
        public bool HasPendingMicrotasks => _microtaskQueue.HasPendingMicrotasks;

        #endregion
    }
}
