using System;
using System.Collections.Generic;
using System.Threading;
using FenBrowser.Core.Engine;

namespace FenBrowser.FenEngine.Core.EventLoop
{
    /// <summary>
    /// Coordinates the main execution loop of the browser engine.
    /// Manages Task and Microtask queues and their synchronization with Rendering.
    /// </summary>
    public class EventLoopCoordinator
    {
        private static EventLoopCoordinator _instance;
        public static EventLoopCoordinator Instance => _instance ??= new EventLoopCoordinator();

        private readonly Queue<Action> _taskQueue = new Queue<Action>();
        private readonly Queue<Action> _microtaskQueue = new Queue<Action>();
        private readonly object _lock = new object();

        // Flag to prevent recursive microtask checkpoints
        private bool _isPerformingCheckpoint = false;

        /// <summary>
        /// Enqueue a Macro-Task (e.g. setTimeout, UI event).
        /// These run one per loop iteration.
        /// </summary>
        public void EnqueueTask(Action task)
        {
            if (task == null) return;
            lock (_lock)
            {
                _taskQueue.Enqueue(task);
            }
        }

        /// <summary>
        /// Enqueue a Microtask (e.g. Promise.then, queueMicrotask).
        /// These are drained completely at the end of the current Task.
        /// </summary>
        public void EnqueueMicrotask(Action microtask)
        {
            if (microtask == null) return;
            lock (_lock)
            {
                _microtaskQueue.Enqueue(microtask);
            }
        }

        /// <summary>
        /// Processes the next Task from the queue, then performs a Microtask Checkpoint.
        /// Returns true if a task was processed.
        /// </summary>
        public bool ProcessNextTask()
        {
            Action task = null;
            lock (_lock)
            {
                if (_taskQueue.Count > 0)
                {
                    task = _taskQueue.Dequeue();
                }
            }

            if (task != null)
            {
                EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
                try
                {
                    task.Invoke();
                }
                catch (Exception ex)
                {
                    // Log but don't crash loop
                    System.Diagnostics.Debug.WriteLine($"[EventLoop] Task Exception: {ex}");
                }
                finally
                {
                    // Always checkpoint after a task
                    PerformMicrotaskCheckpoint();
                    EnginePhaseManager.TryEnterIdle();
                }
                return true;
            }
            
            // Even if no task, we might have microtasks (e.g. from external sources/IO)
            // But spec usually says checkpoint happens after a task. 
            // However, we should check if we have pending microtasks to clear them?
            // For now, adhere to: Checkpoint happens after user script execution.
            return false;
        }

        /// <summary>
        /// Drains the Microtask queue until empty.
        /// See: HTML Spec "Microtask Checkpoint"
        /// </summary>
        public void PerformMicrotaskCheckpoint()
        {
            if (_isPerformingCheckpoint) return; // Re-entrancy protection
            _isPerformingCheckpoint = true;

            try
            {
                while (true)
                {
                    Action microtask = null;
                    lock (_lock)
                    {
                        if (_microtaskQueue.Count > 0)
                            microtask = _microtaskQueue.Dequeue();
                    }

                    if (microtask == null) break;

                    try
                    {
                        microtask.Invoke();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[EventLoop] Microtask Exception: {ex}");
                    }
                }
            }
            finally
            {
                _isPerformingCheckpoint = false;
            }
        }

        /// <summary>
        /// Clear queues (Navigation/Reset).
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _taskQueue.Clear();
                _microtaskQueue.Clear();
            }
        }
        
        // For Testing
        public int TaskCount { get { lock(_lock) return _taskQueue.Count; } }
        public int MicrotaskCount { get { lock(_lock) return _microtaskQueue.Count; } }
    }
}
