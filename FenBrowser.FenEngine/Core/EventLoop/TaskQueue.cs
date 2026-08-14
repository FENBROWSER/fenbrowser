using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Core.EventLoop
{
    /// <summary>
    /// Task source enumeration per HTML spec.
    /// </summary>
    public enum TaskSource
    {
        UserInteraction,
        Timer,
        Networking,
        Messaging,
        IndexedDB,
        DOMManipulation,
        History,
        Animation,
        Other
    }

    public enum TaskPriorityGroup
    {
        Interactive,
        UserVisible,
        Background
    }

    public readonly record struct TaskQueueSnapshot(
        int TotalCount,
        int InteractiveCount,
        int UserVisibleCount,
        int BackgroundCount,
        int ActiveSourceCount);

    /// <summary>
    /// Represents a scheduled task in the event loop.
    /// </summary>
    public class ScheduledTask
    {
        private string _traceId;

        public Action Callback { get; }
        public TaskSource Source { get; }
        public long ScheduledTime { get; }
        public string Description { get; }
        public string TraceId => _traceId ??= EventLoopTrace.NextId("task");

        public ScheduledTask(Action callback, TaskSource source, string description = null)
        {
            Callback = callback ?? throw new ArgumentNullException(nameof(callback));
            Source = source;
            ScheduledTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Description = description ?? source.ToString();
        }
    }

    /// <summary>
    /// Task queue for the event loop.
    /// Tasks are FIFO within a source and scheduled round-robin across active sources.
    /// </summary>
    public class TaskQueue
    {
        private readonly Dictionary<TaskSource, Queue<ScheduledTask>> _tasksBySource = new();
        private readonly Queue<TaskSource> _activeSources = new();
        private readonly HashSet<TaskSource> _activeSourceSet = new();
        private readonly object _lock = new();
        private int _count;

        public void Enqueue(ScheduledTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            TaskPriorityGroup priority;
            int sourceCount;
            int totalCount;
            lock (_lock)
            {
                if (!_tasksBySource.TryGetValue(task.Source, out var queue))
                {
                    queue = new Queue<ScheduledTask>();
                    _tasksBySource[task.Source] = queue;
                }

                queue.Enqueue(task);
                _count++;
                sourceCount = queue.Count;
                totalCount = _count;
                priority = ClassifyPriority(task.Source);

                if (_activeSourceSet.Add(task.Source))
                {
                    _activeSources.Enqueue(task.Source);
                }

                EngineLogCompat.Log(
                    LogCategory.JavaScript,
                    LogLevel.Debug,
                    $"[TaskQueue] Enqueued: {task.Description} (Source: {task.Source}, Priority: {priority}, SourceCount: {sourceCount}, TotalCount: {totalCount})");
            }

            if (EventLoopTrace.IsEnabled(LogSeverity.Debug))
            {
                EventLoopTrace.Write(
                    "TaskQueued",
                    LogSeverity.Debug,
                    "[EventLoop] Task queued",
                    task.TraceId,
                    new Dictionary<string, object>
                    {
                        ["source"] = task.Source.ToString(),
                        ["priority"] = priority.ToString(),
                        ["description"] = task.Description ?? string.Empty,
                        ["sourceCount"] = sourceCount,
                        ["totalCount"] = totalCount
                    });
            }
        }

        public void Enqueue(Action callback, TaskSource source, string description = null)
        {
            Enqueue(new ScheduledTask(callback, source, description));
        }

        public ScheduledTask Dequeue()
        {
            return Dequeue(prioritizeInteractive: false, out _);
        }

        public ScheduledTask Dequeue(bool prioritizeInteractive, out TaskPriorityGroup priorityGroup)
        {
            lock (_lock)
            {
                priorityGroup = TaskPriorityGroup.Background;
                if (_count == 0)
                {
                    return null;
                }

                if (prioritizeInteractive)
                {
                    var task = TryDequeuePriorityLocked(TaskPriorityGroup.Interactive, out priorityGroup);
                    task ??= TryDequeuePriorityLocked(TaskPriorityGroup.UserVisible, out priorityGroup);
                    if (task != null)
                    {
                        return task;
                    }
                }

                return DequeueAnyLocked(out priorityGroup);
            }
        }

        public bool HasPendingTasks
        {
            get
            {
                lock (_lock)
                {
                    return _count > 0;
                }
            }
        }

        public bool HasPendingTasksFor(TaskSource source)
        {
            lock (_lock)
            {
                return _tasksBySource.TryGetValue(source, out var queue) && queue.Count > 0;
            }
        }

        public bool HasPendingTasksFor(TaskPriorityGroup group)
        {
            lock (_lock)
            {
                foreach (var pair in _tasksBySource)
                {
                    if (pair.Value.Count > 0 && ClassifyPriority(pair.Key) == group)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

        public int CountFor(TaskSource source)
        {
            lock (_lock)
            {
                return _tasksBySource.TryGetValue(source, out var queue) ? queue.Count : 0;
            }
        }

        public TaskQueueSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                int interactive = 0;
                int userVisible = 0;
                int background = 0;

                foreach (var pair in _tasksBySource)
                {
                    int sourceCount = pair.Value.Count;
                    if (sourceCount == 0)
                    {
                        continue;
                    }

                    switch (ClassifyPriority(pair.Key))
                    {
                        case TaskPriorityGroup.Interactive:
                            interactive += sourceCount;
                            break;
                        case TaskPriorityGroup.UserVisible:
                            userVisible += sourceCount;
                            break;
                        default:
                            background += sourceCount;
                            break;
                    }
                }

                return new TaskQueueSnapshot(_count, interactive, userVisible, background, _activeSourceSet.Count);
            }
        }

        public int ActiveSourceCount
        {
            get
            {
                lock (_lock)
                {
                    return _activeSourceSet.Count;
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _tasksBySource.Clear();
                _activeSources.Clear();
                _activeSourceSet.Clear();
                _count = 0;
                EngineLogCompat.Debug("[TaskQueue] Cleared all tasks", LogCategory.JavaScript);
            }
        }

        public static TaskPriorityGroup ClassifyPriority(TaskSource source)
        {
            return source switch
            {
                TaskSource.UserInteraction => TaskPriorityGroup.Interactive,
                TaskSource.Animation => TaskPriorityGroup.Interactive,
                TaskSource.DOMManipulation => TaskPriorityGroup.UserVisible,
                TaskSource.History => TaskPriorityGroup.UserVisible,
                TaskSource.Networking => TaskPriorityGroup.UserVisible,
                TaskSource.Messaging => TaskPriorityGroup.UserVisible,
                _ => TaskPriorityGroup.Background
            };
        }

        /// <summary>
        /// Fast ordinary round-robin dequeue. The old generic matcher allocated a
        /// temporary Queue&lt;TaskSource&gt; and a predicate delegate for every task even
        /// though this path accepts the first live source. Keep it allocation-free.
        /// </summary>
        private ScheduledTask DequeueAnyLocked(out TaskPriorityGroup priorityGroup)
        {
            priorityGroup = TaskPriorityGroup.Background;
            while (_activeSources.Count > 0)
            {
                var source = _activeSources.Dequeue();
                if (!_tasksBySource.TryGetValue(source, out var queue) || queue.Count == 0)
                {
                    _activeSourceSet.Remove(source);
                    continue;
                }

                return DequeueFromSourceLocked(source, queue, out priorityGroup);
            }

            return null;
        }

        /// <summary>
        /// Searches the bounded set of task sources for a priority class while
        /// preserving the scheduler's existing queue order. TaskSource is a small
        /// fixed enum, so skipped sources fit in stack memory instead of allocating a
        /// Queue on every prioritized dequeue.
        /// </summary>
        private ScheduledTask TryDequeuePriorityLocked(
            TaskPriorityGroup desiredPriority,
            out TaskPriorityGroup priorityGroup)
        {
            priorityGroup = TaskPriorityGroup.Background;
            if (_activeSources.Count == 0)
            {
                return null;
            }

            var attempts = _activeSources.Count;
            Span<TaskSource> skippedSources = attempts <= 32
                ? stackalloc TaskSource[attempts]
                : new TaskSource[attempts];
            var skippedCount = 0;

            for (var i = 0; i < attempts; i++)
            {
                var source = _activeSources.Dequeue();
                if (!_tasksBySource.TryGetValue(source, out var queue) || queue.Count == 0)
                {
                    _activeSourceSet.Remove(source);
                    continue;
                }

                if (ClassifyPriority(source) != desiredPriority)
                {
                    skippedSources[skippedCount++] = source;
                    continue;
                }

                var task = DequeueFromSourceLocked(source, queue, out priorityGroup);
                for (var skippedIndex = 0; skippedIndex < skippedCount; skippedIndex++)
                {
                    _activeSources.Enqueue(skippedSources[skippedIndex]);
                }
                return task;
            }

            for (var skippedIndex = 0; skippedIndex < skippedCount; skippedIndex++)
            {
                _activeSources.Enqueue(skippedSources[skippedIndex]);
            }

            return null;
        }

        private ScheduledTask DequeueFromSourceLocked(
            TaskSource source,
            Queue<ScheduledTask> queue,
            out TaskPriorityGroup priorityGroup)
        {
            var task = queue.Dequeue();
            _count--;
            priorityGroup = ClassifyPriority(source);

            if (queue.Count > 0)
            {
                _activeSources.Enqueue(source);
            }
            else
            {
                _activeSourceSet.Remove(source);
            }

            EngineLogCompat.Log(
                LogCategory.JavaScript,
                LogLevel.Debug,
                $"[TaskQueue] Dequeued: {task.Description} (Source: {task.Source}, Priority: {priorityGroup}, RemainingSourceCount: {queue.Count}, RemainingTotal: {_count})");
            return task;
        }
    }
}
