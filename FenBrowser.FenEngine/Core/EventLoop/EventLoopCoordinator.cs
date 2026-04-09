using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Core.EventLoop
{
    public readonly record struct TaskProcessingResult(
        bool Processed,
        TaskSource Source,
        TaskPriorityGroup PriorityGroup)
    {
        public static TaskProcessingResult None => new TaskProcessingResult(false, TaskSource.Other, TaskPriorityGroup.Background);
    }

    /// <summary>
    /// Coordinates the main execution loop of the browser engine.
    /// Implements the execution order defined in EventLoopSemantics.md:
    /// Task -> JSExecution -> Microtasks -> DOM Flush -> Layout -> Paint -> Observers -> Animation
    /// </summary>
    public class EventLoopCoordinator
    {
        private static EventLoopCoordinator _instance;
        public static EventLoopCoordinator Instance => _instance ??= new EventLoopCoordinator();

        private readonly TaskQueue _taskQueue = new();
        private readonly MicrotaskQueue _microtaskQueue = new();
        private readonly Queue<Action> _animationFrameCallbacks = new();
        private readonly object _animationLock = new();
        private readonly Queue<Action> _mutationObserverCallbacks = new();
        private readonly object _moLock = new object();

        private bool _layoutDirty = false;
        private long _lastRenderTime = 0;
        private Action _renderCallback = null;
        private Action _observerCallback = null;

        public EnginePhase CurrentPhase => EngineContext.Current.CurrentPhase;

        public event Action OnWorkEnqueued;

        #region Task Scheduling

        public void ScheduleTask(Action callback, TaskSource source, string description = null)
        {
            if (callback == null) return;
            _taskQueue.Enqueue(callback, source, description);
            OnWorkEnqueued?.Invoke();
        }

        public void EnqueueTask(Action task)
        {
            ScheduleTask(task, TaskSource.Other, "Legacy Task");
        }

        #endregion

        #region Microtask Scheduling

        public void ScheduleMicrotask(Action callback)
        {
            if (callback == null) return;
            _microtaskQueue.Enqueue(callback);
            OnWorkEnqueued?.Invoke();
        }

        public void EnqueueMicrotask(Action microtask)
        {
            ScheduleMicrotask(microtask);
        }

        #endregion

        #region MutationObserver Batch Delivery

        public void QueueMutationObserverMicrotask(Action callback)
        {
            if (callback == null) return;
            lock (_moLock)
            {
                _mutationObserverCallbacks.Enqueue(callback);
            }
        }

        private void DeliverMutationObserverRecords()
        {
            List<Action> toDeliver;
            lock (_moLock)
            {
                if (_mutationObserverCallbacks.Count == 0) return;
                toDeliver = new List<Action>(_mutationObserverCallbacks);
                _mutationObserverCallbacks.Clear();
            }

            foreach (var cb in toDeliver)
            {
                try
                {
                    cb();
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[EventLoop] MutationObserver callback error: {ex.Message}", LogCategory.DOM);
                }
            }

            _microtaskQueue.DrainAll();
        }

        #endregion

        #region Animation Frame

        public void ScheduleAnimationFrame(Action callback)
        {
            if (callback == null) return;
            lock (_animationLock)
            {
                _animationFrameCallbacks.Enqueue(callback);
            }
            OnWorkEnqueued?.Invoke();
        }

        #endregion

        #region Rendering Integration

        public void NotifyLayoutDirty()
        {
            _layoutDirty = true;
            OnWorkEnqueued?.Invoke();
        }

        public void SetRenderCallback(Action callback)
        {
            _renderCallback = callback;
        }

        public void SetObserverCallback(Action callback)
        {
            _observerCallback = callback;
        }

        #endregion

        #region Event Loop Execution

        public bool ProcessNextTask()
        {
            return ProcessNextTaskDetailed().Processed;
        }

        public TaskProcessingResult ProcessNextTaskDetailed(bool prioritizeInteractive = false)
        {
            var task = _taskQueue.Dequeue(prioritizeInteractive, out var priorityGroup);
            if (task == null)
            {
                if (_microtaskQueue.HasPendingMicrotasks)
                {
                    PerformMicrotaskCheckpoint();
                    ProcessRenderingUpdate();
                    EnsureIdlePhase();
                    return new TaskProcessingResult(true, TaskSource.Other, TaskPriorityGroup.Background);
                }

                ProcessRenderingUpdate();
                return TaskProcessingResult.None;
            }

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

            PerformMicrotaskCheckpoint();
            ProcessRenderingUpdate();
            EnsureIdlePhase();
            return new TaskProcessingResult(true, task.Source, priorityGroup);
        }

        public void PerformMicrotaskCheckpoint()
        {
            EngineContext.Current.AssertNotInPhase(EnginePhase.Microtasks);

            EngineContext.Current.BeginPhase(EnginePhase.Microtasks);
            try
            {
                _microtaskQueue.DrainAll();
            }
            finally
            {
                EngineContext.Current.EndPhase();
            }

            DeliverMutationObserverRecords();
        }

        public void ProcessRenderingUpdate()
        {
            var now = Environment.TickCount64;
            bool hasRenderingOpportunity = (now - _lastRenderTime) >= 16 || _layoutDirty;
            if (!hasRenderingOpportunity)
            {
                return;
            }

            ProcessAnimationFrames();

            if (_layoutDirty && _renderCallback != null)
            {
                _lastRenderTime = now;
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

                PerformMicrotaskCheckpoint();
            }

            EnsureIdlePhase();
        }

        private void ProcessAnimationFrames()
        {
            Queue<Action> callbacks;
            lock (_animationLock)
            {
                if (_animationFrameCallbacks.Count == 0)
                {
                    return;
                }

                callbacks = new Queue<Action>(_animationFrameCallbacks);
                _animationFrameCallbacks.Clear();
            }

            while (callbacks.Count > 0)
            {
                EngineContext.Current.BeginPhase(EnginePhase.Animation);
                try
                {
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

        public void RunUntilEmpty()
        {
            while (_taskQueue.HasPendingTasks ||
                   _animationFrameCallbacks.Count > 0 ||
                   _microtaskQueue.HasPendingMicrotasks)
            {
                if (_taskQueue.HasPendingTasks || _animationFrameCallbacks.Count > 0)
                {
                    ProcessNextTask();
                    continue;
                }

                PerformMicrotaskCheckpoint();
            }
        }

        #endregion

        #region Utility

        public void Clear()
        {
            _taskQueue.Clear();
            _microtaskQueue.Clear();
            lock (_animationLock)
            {
                _animationFrameCallbacks.Clear();
            }
            lock (_moLock)
            {
                _mutationObserverCallbacks.Clear();
            }
            _layoutDirty = false;
            _lastRenderTime = 0;
            FenLogger.Debug("[EventLoop] All queues cleared", LogCategory.JavaScript);
        }

        public static void ResetInstance()
        {
            _instance = new EventLoopCoordinator();
        }

        public int TaskCount => _taskQueue.Count;
        public int MicrotaskCount => _microtaskQueue.Count;
        public bool HasPendingTasks => _taskQueue.HasPendingTasks;
        public bool HasPendingMicrotasks => _microtaskQueue.HasPendingMicrotasks;
        public TaskQueueSnapshot GetTaskSnapshot() => _taskQueue.GetSnapshot();
        public bool HasPendingTasksFor(TaskSource source) => _taskQueue.HasPendingTasksFor(source);
        public bool HasPendingTasksFor(TaskPriorityGroup group) => _taskQueue.HasPendingTasksFor(group);

        #endregion
    }
}
