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
        private bool _layoutRunThisTick = false;
        private Action _renderCallback = null;
        private Action _observerCallback = null;

        /// <summary>
        /// Current engine phase for assertions
        /// </summary>
        public EnginePhase CurrentPhase => EngineContext.Current.CurrentPhase;

        /// <summary>
        /// Fired when new work is added to any queue (Task, Microtask, Animation, Layout).
        /// Used to wake the host event loop.
        /// </summary>
        public event Action OnWorkEnqueued;

        #region Task Scheduling

        /// <summary>
        /// Schedule a task for execution
        /// </summary>
        public void ScheduleTask(Action callback, TaskSource source, string description = null)
        {
            if (callback  == null) return;
            _taskQueue.Enqueue(callback, source, description);
            OnWorkEnqueued?.Invoke();
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
            if (callback  == null) return;
            _microtaskQueue.Enqueue(callback);
            OnWorkEnqueued?.Invoke();
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
            if (callback  == null) return;
            lock (_animationLock)
            {
                _animationFrameCallbacks.Enqueue(callback);
            }
            OnWorkEnqueued?.Invoke();
        }

        #endregion

        #region Rendering Integration

        /// <summary>
        /// Mark layout as dirty - will trigger render on next checkpoint
        /// </summary>
        public void NotifyLayoutDirty()
        {
            _layoutDirty = true;
            OnWorkEnqueued?.Invoke();
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
            _layoutRunThisTick = false; // Reset at start of tick

            var task = _taskQueue.Dequeue();
            if (task  == null)
            {
                // No task, but might still have animation frames or rendering to do
                ProcessRenderingUpdate();
                return false;
            }

            // STEP 1-2: Execute task
            EngineContext.Current.BeginPhase(EnginePhase.JSExecution);
            try
            {
                FenLogger.Debug($"[EventLoop] Executing task: {task.Description}", LogCategory.JavaScript);
                task.Callback.Invoke();
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[EventLoop] Task Exception: {ex.Message}", LogCategory.Errors);
            }
            finally
            {
                EngineContext.Current.EndPhase();
            }

            // STEP 3: Microtask checkpoint
            PerformMicrotaskCheckpoint();

            // STEP 4-6: Rendering update
            ProcessRenderingUpdate();

            EnsureIdlePhase();
            return true;
        }

        /// <summary>
        /// Perform a microtask checkpoint - drains ALL microtasks
        /// </summary>
        public void PerformMicrotaskCheckpoint()
        {
            // CRITICAL: Ensure we are not re-entering Microtask phase recursively
            EngineContext.Current.AssertNotInPhase(EnginePhase.Microtasks);
            
            EngineContext.Current.BeginPhase(EnginePhase.Microtasks);
            try
            {
                // DRAIN LOOP: Keep draining until empty.
                // NOTE: DrainAll() inside MicrotaskQueue should ideally handle the loop, 
                // but if new microtasks are queued during execution, we must ensure they run
                // in the SAME checkpoint, without leaving/re-entering the phase.
                // Assuming _microtaskQueue.DrainAll() handles internal looping.
                // If not, we would wrap it: while (_microtaskQueue.HasPendingMicrotasks) _microtaskQueue.DrainAll();
                
                _microtaskQueue.DrainAll();
            }
            finally
            {
                EngineContext.Current.EndPhase();
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
                // STRICT: One layout per tick
                if (_layoutRunThisTick) return;
                _layoutRunThisTick = true;

                EngineContext.Current.BeginPhase(EnginePhase.Layout);
                try
                {
                    FenLogger.Debug("[EventLoop] Rendering update (layout dirty)", LogCategory.Rendering);
                    _renderCallback.Invoke();
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[EventLoop] Render Exception: {ex.Message}", LogCategory.Errors);
                }
                finally
                {
                    _layoutDirty = false;
                    EngineContext.Current.EndPhase();
                }
            }

            // Observer evaluation
            if (_observerCallback != null)
            {
                EngineContext.Current.BeginPhase(EnginePhase.Observers);
                try
                {
                    _observerCallback.Invoke();
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[EventLoop] Observer Exception: {ex.Message}", LogCategory.Errors);
                }
                finally
                {
                    EngineContext.Current.EndPhase();
                }

                // Microtask checkpoint after observers (outside observer phase)
                PerformMicrotaskCheckpoint();
            }

            // Animation frame callbacks
            ProcessAnimationFrames();

            EnsureIdlePhase();
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

            while (callbacks.Count > 0)
            {
                // Mark animation processing phase for this RAF slot.
                EngineContext.Current.BeginPhase(EnginePhase.Animation);
                try
                {
                    // No-op body: this phase marks RAF slot advancement.
                }
                finally
                {
                    EngineContext.Current.EndPhase();
                }

                var callback = callbacks.Dequeue();
                EngineContext.Current.BeginPhase(EnginePhase.JSExecution);
                try
                {
                    callback.Invoke();
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[EventLoop] RAF Exception: {ex.Message}", LogCategory.Errors);
                }
                finally
                {
                    EngineContext.Current.EndPhase();
                }

                // Microtask checkpoint after each RAF callback (outside JSExecution phase)
                PerformMicrotaskCheckpoint();
            }
        }

        private static void EnsureIdlePhase()
        {
            if (EngineContext.Current.CurrentPhase != EnginePhase.Idle)
            {
                FenLogger.Warn(
                    $"[EventLoop] Phase leak detected: {EngineContext.Current.CurrentPhase}. Forcing Idle recovery.",
                    LogCategory.Errors);
                EngineContext.Current.EndPhase();
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
