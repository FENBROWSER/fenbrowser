// SpecRef: WHATWG HTML, Web application APIs, Event loops
// CapabilityId: EVENTLOOP-MACROTASK-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Collections.Generic;
using System.Threading;
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
        private const int MaxMicrotaskCheckpointPasses = 1024;
        private static EventLoopCoordinator s_sharedInstance;
        private static readonly ThreadLocal<EventLoopCoordinator> s_threadDefault = new ThreadLocal<EventLoopCoordinator>(() => new EventLoopCoordinator());
        private static readonly AsyncLocal<EventLoopCoordinator> s_boundInstance = new AsyncLocal<EventLoopCoordinator>();
        public static EventLoopCoordinator Instance => s_boundInstance.Value ?? s_threadDefault.Value;
        public static EventLoopCoordinator ThreadDefault => s_sharedInstance ??= new EventLoopCoordinator();

        private sealed class DelayedTaskEntry
        {
            public DelayedTaskEntry(ScheduledTask task, long dueTimeMs, long sequence, string timerId)
            {
                Task = task;
                DueTimeMs = dueTimeMs;
                Sequence = sequence;
                TimerId = timerId;
            }

            public ScheduledTask Task { get; }
            public long DueTimeMs { get; }
            public long Sequence { get; }
            public string TimerId { get; }
        }

        private sealed class AnimationFrameEntry
        {
            public AnimationFrameEntry(Action callback)
            {
                Callback = callback ?? throw new ArgumentNullException(nameof(callback));
                TraceId = EventLoopTrace.NextId("raf");
            }

            public Action Callback { get; }
            public string TraceId { get; }
        }

        private readonly TaskQueue _taskQueue = new();
        private readonly MicrotaskQueue _microtaskQueue = new();
        private readonly Queue<AnimationFrameEntry> _animationFrameCallbacks = new();
        private readonly object _animationLock = new();
        private readonly Queue<Action> _mutationObserverCallbacks = new();
        private readonly object _moLock = new object();
        private readonly List<DelayedTaskEntry> _delayedTasks = new();
        private readonly object _delayedTaskLock = new();

        private bool _layoutDirty = false;
        private long _lastRenderTime = 0;
        private long _nextDelayedTaskSequence = 0;
        private Action _renderCallback = null;
        private Action _observerCallback = null;

        public EnginePhase CurrentPhase => EngineContext.Current.CurrentPhase;

        public event Action OnWorkEnqueued;

        #region Task Scheduling

        public static EventLoopCoordinator CreateIsolated()
        {
            return new EventLoopCoordinator();
        }

        public static IDisposable Bind(EventLoopCoordinator coordinator)
        {
            var previous = s_boundInstance.Value;
            s_boundInstance.Value = coordinator;
            return new BindingScope(previous);
        }

        private sealed class BindingScope : IDisposable
        {
            private readonly EventLoopCoordinator _previous;
            private bool _disposed;

            public BindingScope(EventLoopCoordinator previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                s_boundInstance.Value = _previous;
            }
        }

        public void ScheduleTask(Action callback, TaskSource source, string description = null)
        {
            if (callback == null) return;
            _taskQueue.Enqueue(callback, source, description);
            OnWorkEnqueued?.Invoke();
        }

        public void ScheduleDelayedTask(Action callback, int delayMs, TaskSource source, string description = null)
        {
            if (callback == null)
            {
                return;
            }

            var safeDelay = Math.Max(0, delayMs);
            if (safeDelay == 0)
            {
                ScheduleTask(callback, source, description);
                return;
            }

            var delayedTask = new DelayedTaskEntry(
                new ScheduledTask(callback, source, description),
                Environment.TickCount64 + safeDelay,
                Interlocked.Increment(ref _nextDelayedTaskSequence),
                EventLoopTrace.NextId("timer"));

            lock (_delayedTaskLock)
            {
                _delayedTasks.Add(delayedTask);
            }

            EventLoopTrace.Write(
                "TimerScheduled",
                LogSeverity.Debug,
                "[EventLoop] Timer scheduled",
                delayedTask.TimerId,
                new Dictionary<string, object>
                {
                    ["delayMs"] = safeDelay,
                    ["source"] = source.ToString(),
                    ["description"] = description ?? source.ToString(),
                    ["taskId"] = delayedTask.Task.TraceId
                });
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

        private bool DeliverMutationObserverRecords()
        {
            List<Action> toDeliver;
            lock (_moLock)
            {
                if (_mutationObserverCallbacks.Count == 0)
                {
                    return false;
                }
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
                    EngineLogCompat.Warn($"[EventLoop] MutationObserver callback error: {ex.Message}", LogCategory.DOM);
                }
            }

            return true;
        }

        private bool HasQueuedMutationObserverCallbacks
        {
            get
            {
                lock (_moLock)
                {
                    return _mutationObserverCallbacks.Count > 0;
                }
            }
        }

        #endregion

        #region Animation Frame

        public void ScheduleAnimationFrame(Action callback)
        {
            if (callback == null) return;
            var entry = new AnimationFrameEntry(callback);
            int pendingCount;
            lock (_animationLock)
            {
                _animationFrameCallbacks.Enqueue(entry);
                pendingCount = _animationFrameCallbacks.Count;
            }
            EventLoopTrace.Write(
                "RequestAnimationFrameScheduled",
                LogSeverity.Debug,
                "[EventLoop] requestAnimationFrame scheduled",
                entry.TraceId,
                new Dictionary<string, object>
                {
                    ["pendingCount"] = pendingCount
                });
            OnWorkEnqueued?.Invoke();
        }

        #endregion

        #region Rendering Integration

        public void NotifyLayoutDirty()
        {
            Volatile.Write(ref _layoutDirty, true);
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

public TaskProcessingResult ProcessNextTaskDetailed(bool prioritizeInteractive = false, FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
{
PromoteDueDelayedTasks();

var task = _taskQueue.Dequeue(prioritizeInteractive, out var priorityGroup);
            if (task == null)
            {
                if (_microtaskQueue.HasPendingMicrotasks)
                {
                    PerformMicrotaskCheckpoint(deadline);
                    ProcessRenderingUpdate(deadline);
                    EnsureIdlePhase();
                    return new TaskProcessingResult(true, TaskSource.Other, TaskPriorityGroup.Background);
                }

                ProcessRenderingUpdate(deadline);
                return TaskProcessingResult.None;
            }

EngineContext.Current.BeginPhase(EnginePhase.JSExecution);
EventLoopTrace.Write(
    "TaskStarted",
    LogSeverity.Debug,
    "[EventLoop] Task started",
    task.TraceId,
    new Dictionary<string, object>
    {
        ["source"] = task.Source.ToString(),
        ["priority"] = priorityGroup.ToString(),
        ["description"] = task.Description ?? string.Empty
    });
bool taskFailed = false;
string taskErrorType = string.Empty;
string taskError = string.Empty;
try
{
EngineLogCompat.Debug($"[EventLoop] Executing task: {task.Description}", LogCategory.JavaScript);
task.Callback.Invoke();
}
catch (Exception ex)
{
EngineLogCompat.Debug($"[EventLoop] Task Exception: {ex.Message}", LogCategory.Errors);
taskFailed = true;
taskErrorType = ex.GetType().Name;
taskError = ex.Message;
EventLoopTrace.Write(
    "TaskFailed",
    LogSeverity.Warn,
    "[EventLoop] Task failed",
    task.TraceId,
    new Dictionary<string, object>
    {
        ["source"] = task.Source.ToString(),
        ["priority"] = priorityGroup.ToString(),
        ["description"] = task.Description ?? string.Empty,
        ["errorType"] = taskErrorType,
        ["error"] = taskError
    },
    LogMarker.EngineBug);
}
finally
{
EngineContext.Current.EndPhase();
EventLoopTrace.Write(
    "TaskCompleted",
    taskFailed ? LogSeverity.Warn : LogSeverity.Debug,
    "[EventLoop] Task completed",
    task.TraceId,
    new Dictionary<string, object>
    {
        ["source"] = task.Source.ToString(),
        ["priority"] = priorityGroup.ToString(),
        ["description"] = task.Description ?? string.Empty,
        ["success"] = !taskFailed,
        ["errorType"] = taskErrorType,
        ["error"] = taskError
    });
}

PerformMicrotaskCheckpoint(deadline);
ProcessRenderingUpdate(deadline);
EnsureIdlePhase();
return new TaskProcessingResult(true, task.Source, priorityGroup);
        }

        public void PerformMicrotaskCheckpoint(FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
        {
            EngineContext.Current.AssertNotInPhase(EnginePhase.Microtasks);

            var checkpointId = EventLoopTrace.NextId("microtask-checkpoint");
            var processedMicrotasks = 0;
            var deliveredMutationObserverBatches = 0;
            var passes = 0;
            var forcedExit = false;
            EventLoopTrace.Write(
                "MicrotaskCheckpointStarted",
                LogSeverity.Debug,
                "[EventLoop] Microtask checkpoint started",
                checkpointId,
                new Dictionary<string, object>
                {
                    ["pendingMicrotasks"] = _microtaskQueue.Count,
                    ["pendingMutationObservers"] = HasQueuedMutationObserverCallbacks
                });
            EngineContext.Current.BeginPhase(EnginePhase.Microtasks);
            try
            {
                while (true)
                {
                    deadline?.Check();
                    processedMicrotasks += _microtaskQueue.DrainAll();
                    var deliveredMutationObservers = DeliverMutationObserverRecords();
                    if (deliveredMutationObservers)
                    {
                        deliveredMutationObserverBatches++;
                    }

                    if (!deliveredMutationObservers &&
                        !_microtaskQueue.HasPendingMicrotasks &&
                        !HasQueuedMutationObserverCallbacks)
                    {
                        break;
                    }

                    passes++;
                    if (passes >= MaxMicrotaskCheckpointPasses)
                    {
                        EngineLogCompat.Warn(
                            $"[EventLoop] Microtask checkpoint pass limit ({MaxMicrotaskCheckpointPasses}) exceeded; forcing checkpoint exit",
                            LogCategory.Errors);
                        forcedExit = true;
                        break;
                    }
                }
            }
            finally
            {
                EngineContext.Current.EndPhase();
                EventLoopTrace.Write(
                    "MicrotaskCheckpointCompleted",
                    forcedExit ? LogSeverity.Warn : LogSeverity.Debug,
                    "[EventLoop] Microtask checkpoint completed",
                    checkpointId,
                    new Dictionary<string, object>
                    {
                        ["processedMicrotasks"] = processedMicrotasks,
                        ["mutationObserverBatches"] = deliveredMutationObserverBatches,
                        ["passes"] = passes,
                        ["forcedExit"] = forcedExit,
                        ["pendingMicrotasks"] = _microtaskQueue.Count,
                        ["pendingMutationObservers"] = HasQueuedMutationObserverCallbacks
                    });
            }
        }

        // HTML §8.1.4.3 "Update the rendering" step sequence (simplified):
        //   resize/scroll/MQL steps -> ResizeObserver broadcast -> animation events ->
        //   animation frame callbacks -> update intersection observations -> paint.
        // Observers therefore run BEFORE animation frames, and animation frames run BEFORE paint.
        public void ProcessRenderingUpdate(FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
        {
            var now = Environment.TickCount64;
            bool hasRenderingOpportunity = (now - _lastRenderTime) >= 16 || Volatile.Read(ref _layoutDirty);
            if (!hasRenderingOpportunity)
            {
                return;
            }

            var renderOpportunityId = EventLoopTrace.NextId("render-opportunity");
            var observerRan = false;
            var renderRan = false;
            EventLoopTrace.Write(
                "RenderOpportunityStarted",
                LogSeverity.Debug,
                "[EventLoop] Render opportunity started",
                renderOpportunityId,
                new Dictionary<string, object>
                {
                    ["layoutDirty"] = Volatile.Read(ref _layoutDirty),
                    ["hasObserverCallback"] = _observerCallback != null,
                    ["pendingAnimationFrames"] = PendingAnimationFrameCount,
                    ["hasRenderCallback"] = _renderCallback != null
                });
            try
            {
                if (_observerCallback != null)
                {
                    EngineContext.Current.BeginPhase(EnginePhase.Observers);
                    try
                    {
                        observerRan = true;
                        _observerCallback.Invoke();
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug($"[EventLoop] Observer Exception: {ex.Message}", LogCategory.Errors);
                    }
                    finally
                    {
                        EngineContext.Current.EndPhase();
                    }

                    PerformMicrotaskCheckpoint(deadline);
                }

                ProcessAnimationFrames(deadline);

                if (_layoutDirty && _renderCallback != null)
                {
                    _lastRenderTime = now;
                    EngineContext.Current.BeginPhase(EnginePhase.Layout);
                    try
                    {
                        EngineLogCompat.Debug("[EventLoop] Rendering update (layout dirty)", LogCategory.Rendering);
                        renderRan = true;
                        _renderCallback.Invoke();
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug($"[EventLoop] Render Exception: {ex.Message}", LogCategory.Errors);
                    }
                    finally
                    {
                        Volatile.Write(ref _layoutDirty, false);
                        EngineContext.Current.EndPhase();
                    }
                }
            }
            finally
            {
                EventLoopTrace.Write(
                    "RenderOpportunityCompleted",
                    LogSeverity.Debug,
                    "[EventLoop] Render opportunity completed",
                    renderOpportunityId,
                    new Dictionary<string, object>
                    {
                        ["observerRan"] = observerRan,
                        ["renderRan"] = renderRan,
                        ["layoutDirty"] = Volatile.Read(ref _layoutDirty),
                        ["pendingAnimationFrames"] = PendingAnimationFrameCount
                    });
                EnsureIdlePhase();
            }
        }

        private void ProcessAnimationFrames(FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
        {
            Queue<AnimationFrameEntry> callbacks;
            lock (_animationLock)
            {
                if (_animationFrameCallbacks.Count == 0)
                {
                    return;
                }

                callbacks = new Queue<AnimationFrameEntry>(_animationFrameCallbacks);
                _animationFrameCallbacks.Clear();
            }

            while (callbacks.Count > 0)
            {
                var callback = callbacks.Dequeue();
                EventLoopTrace.Write(
                    "RequestAnimationFrameFired",
                    LogSeverity.Debug,
                    "[EventLoop] requestAnimationFrame fired",
                    callback.TraceId,
                    new Dictionary<string, object>
                    {
                        ["remainingInBatch"] = callbacks.Count
                    });
                EngineContext.Current.BeginPhase(EnginePhase.Animation);
                try
                {
                    callback.Callback.Invoke();
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Debug($"[EventLoop] RAF Exception: {ex.Message}", LogCategory.Errors);
                }
                finally
                {
                    EngineContext.Current.EndPhase();
                }

                PerformMicrotaskCheckpoint(deadline);
            }
        }

        private static void EnsureIdlePhase()
        {
            if (EngineContext.Current.CurrentPhase != EnginePhase.Idle)
            {
                EngineLogCompat.Warn(
                    $"[EventLoop] Phase leak detected: {EngineContext.Current.CurrentPhase}. Forcing Idle recovery.",
                    LogCategory.Errors);
                EngineContext.Current.EndPhase();
            }
        }

        public void RunUntilEmpty()
        {
            while (_taskQueue.HasPendingTasks ||
                   HasPendingDelayedTasks ||
                   HasPendingAnimationFrames ||
                   _microtaskQueue.HasPendingMicrotasks)
            {
                PromoteDueDelayedTasks();

                if (_taskQueue.HasPendingTasks || HasPendingAnimationFrames)
                {
                    ProcessNextTask();
                    continue;
                }

                if (HasPendingDelayedTasks)
                {
                    WaitUntilNextDelayedTaskDue();
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
            lock (_delayedTaskLock)
            {
                _delayedTasks.Clear();
            }
            Volatile.Write(ref _layoutDirty, false);
            _lastRenderTime = 0;
            _nextDelayedTaskSequence = 0;
            EngineLogCompat.Debug("[EventLoop] All queues cleared", LogCategory.JavaScript);
        }

        public static void ResetInstance()
        {
            s_boundInstance.Value = null;
            s_sharedInstance = new EventLoopCoordinator();
        }

        public int TaskCount => _taskQueue.Count;
        public int MicrotaskCount => _microtaskQueue.Count;
        public bool HasPendingTasks => _taskQueue.HasPendingTasks;
        public bool HasPendingMicrotasks => _microtaskQueue.HasPendingMicrotasks;
        public bool HasPendingDelayedTasks
        {
            get
            {
                lock (_delayedTaskLock)
                {
                    return _delayedTasks.Count > 0;
                }
            }
        }

        private bool HasPendingAnimationFrames
        {
            get
            {
                lock (_animationLock)
                {
                    return _animationFrameCallbacks.Count > 0;
                }
            }
        }

        private int PendingAnimationFrameCount
        {
            get
            {
                lock (_animationLock)
                {
                    return _animationFrameCallbacks.Count;
                }
            }
        }

        public int GetSuggestedWaitMilliseconds(int maxWaitMs = 50)
        {
            if (HasPendingTasks || HasPendingMicrotasks || HasPendingAnimationFrames)
            {
                return 0;
            }

            lock (_delayedTaskLock)
            {
                if (_delayedTasks.Count == 0)
                {
                    return -1;
                }

                var now = Environment.TickCount64;
                long nextDueTime = long.MaxValue;
                foreach (var delayedTask in _delayedTasks)
                {
                    if (delayedTask.DueTimeMs < nextDueTime)
                    {
                        nextDueTime = delayedTask.DueTimeMs;
                    }
                }

                var waitMs = Math.Max(0, nextDueTime - now);
                return (int)Math.Min(waitMs, Math.Max(0, maxWaitMs));
            }
        }

        public TaskQueueSnapshot GetTaskSnapshot() => _taskQueue.GetSnapshot();
        public bool HasPendingTasksFor(TaskSource source) => _taskQueue.HasPendingTasksFor(source);
        public bool HasPendingTasksFor(TaskPriorityGroup group) => _taskQueue.HasPendingTasksFor(group);

        #endregion

        private void PromoteDueDelayedTasks()
        {
            List<DelayedTaskEntry> dueTasks = null;
            var now = Environment.TickCount64;

            lock (_delayedTaskLock)
            {
                for (int index = _delayedTasks.Count - 1; index >= 0; index--)
                {
                    var delayedTask = _delayedTasks[index];
                    if (delayedTask.DueTimeMs > now)
                    {
                        continue;
                    }

                    dueTasks ??= new List<DelayedTaskEntry>();
                    dueTasks.Add(delayedTask);
                    _delayedTasks.RemoveAt(index);
                }
            }

            if (dueTasks == null || dueTasks.Count == 0)
            {
                return;
            }

            dueTasks.Sort((left, right) =>
            {
                var dueComparison = left.DueTimeMs.CompareTo(right.DueTimeMs);
                return dueComparison != 0 ? dueComparison : left.Sequence.CompareTo(right.Sequence);
            });

            foreach (var delayedTask in dueTasks)
            {
                EventLoopTrace.Write(
                    "TimerFired",
                    LogSeverity.Debug,
                    "[EventLoop] Timer fired",
                    delayedTask.TimerId,
                    new Dictionary<string, object>
                    {
                        ["taskId"] = delayedTask.Task.TraceId,
                        ["source"] = delayedTask.Task.Source.ToString(),
                        ["description"] = delayedTask.Task.Description ?? string.Empty
                    });
                _taskQueue.Enqueue(delayedTask.Task);
            }
        }

        private void WaitUntilNextDelayedTaskDue()
        {
            long waitMs = 0;
            var now = Environment.TickCount64;

            lock (_delayedTaskLock)
            {
                if (_delayedTasks.Count == 0)
                {
                    return;
                }

                var nextDueTime = long.MaxValue;
                foreach (var delayedTask in _delayedTasks)
                {
                    if (delayedTask.DueTimeMs < nextDueTime)
                    {
                        nextDueTime = delayedTask.DueTimeMs;
                    }
                }

                waitMs = Math.Max(0, nextDueTime - now);
            }

            if (waitMs <= 0)
            {
                return;
            }

            Thread.Sleep((int)Math.Min(waitMs, 50));
        }
    }
}

