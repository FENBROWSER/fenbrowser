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
        public Action Callback { get; }
        public TaskSource Source { get; }
        public long ScheduledTime { get; }
        public string Description { get; }

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

            lock (_lock)
            {
                if (!_tasksBySource.TryGetValue(task.Source, out var queue))
                {
                    queue = new Queue<ScheduledTask>();
                    _tasksBySource[task.Source] = queue;
                }

                queue.Enqueue(task);
                _count++;

                if (_activeSourceSet.Add(task.Source))
                {
                    _activeSources.Enqueue(task.Source);
                }

                EngineLogCompat.Debug(
                    $"[TaskQueue] Enqueued: {task.Description} (Source: {task.Source}, Priority: {ClassifyPriority(task.Source)}, SourceCount: {queue.Count}, TotalCount: {_count})",
                    LogCategory.JavaScript);
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

                ScheduledTask task = null;

                if (prioritizeInteractive)
                {
                    task = TryDequeueMatchingLocked(
                        source => ClassifyPriority(source) == TaskPriorityGroup.Interactive,
                        out priorityGroup);

                    task ??= TryDequeueMatchingLocked(
                        source => ClassifyPriority(source) == TaskPriorityGroup.UserVisible,
                        out priorityGroup);
                }

                task ??= TryDequeueMatchingLocked(_ => true, out priorityGroup);
                return task;
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

        private ScheduledTask TryDequeueMatchingLocked(Func<TaskSource, bool> sourcePredicate, out TaskPriorityGroup priorityGroup)
        {
            priorityGroup = TaskPriorityGroup.Background;
            if (_activeSources.Count == 0)
            {
                return null;
            }

            int attempts = _activeSources.Count;
            var skippedSources = new Queue<TaskSource>();

            for (int i = 0; i < attempts; i++)
            {
                var source = _activeSources.Dequeue();
                if (!_tasksBySource.TryGetValue(source, out var queue) || queue.Count == 0)
                {
                    _activeSourceSet.Remove(source);
                    continue;
                }

                if (!sourcePredicate(source))
                {
                    skippedSources.Enqueue(source);
                    continue;
                }

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

                while (skippedSources.Count > 0)
                {
                    _activeSources.Enqueue(skippedSources.Dequeue());
                }

                EngineLogCompat.Debug(
                    $"[TaskQueue] Dequeued: {task.Description} (Source: {task.Source}, Priority: {priorityGroup}, RemainingSourceCount: {queue.Count}, RemainingTotal: {_count})",
                    LogCategory.JavaScript);
                return task;
            }

            while (skippedSources.Count > 0)
            {
                _activeSources.Enqueue(skippedSources.Dequeue());
            }

            return null;
        }
    }
}
