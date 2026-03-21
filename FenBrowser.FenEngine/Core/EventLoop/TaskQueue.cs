using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Core.EventLoop
{
    /// <summary>
    /// Task source enumeration per HTML spec
    /// </summary>
    public enum TaskSource
    {
        UserInteraction,    // Mouse, keyboard, touch
        Timer,              // setTimeout, setInterval
        Networking,         // fetch, XHR
        Messaging,          // postMessage
        IndexedDB,          // IDB callbacks
        DOMManipulation,    // Script-triggered DOM changes
        History,            // Navigation events
        Animation,          // requestAnimationFrame
        Other
    }

    /// <summary>
    /// Represents a scheduled task in the event loop
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
    /// Tasks are FIFO within a source and scheduled round-robin across active sources
    /// so one source cannot starve the others.
    /// </summary>
    public class TaskQueue
    {
        private readonly Dictionary<TaskSource, Queue<ScheduledTask>> _tasksBySource = new();
        private readonly Queue<TaskSource> _activeSources = new();
        private readonly HashSet<TaskSource> _activeSourceSet = new();
        private readonly object _lock = new();
        private int _count;

        /// <summary>
        /// Enqueue a task for execution
        /// </summary>
        public void Enqueue(ScheduledTask task)
        {
            if (task  == null) throw new ArgumentNullException(nameof(task));

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

                FenLogger.Debug(
                    $"[TaskQueue] Enqueued: {task.Description} (Source: {task.Source}, SourceCount: {queue.Count}, TotalCount: {_count})",
                    LogCategory.JavaScript);
            }
        }

        /// <summary>
        /// Enqueue a callback as a task
        /// </summary>
        public void Enqueue(Action callback, TaskSource source, string description = null)
        {
            Enqueue(new ScheduledTask(callback, source, description));
        }

        /// <summary>
        /// Dequeue the next task, or null if empty
        /// </summary>
        public ScheduledTask Dequeue()
        {
            lock (_lock)
            {
                if (_count == 0)
                    return null;

                while (_activeSources.Count > 0)
                {
                    var source = _activeSources.Dequeue();
                    if (!_tasksBySource.TryGetValue(source, out var queue) || queue.Count == 0)
                    {
                        _activeSourceSet.Remove(source);
                        continue;
                    }

                    var task = queue.Dequeue();
                    _count--;

                    if (queue.Count > 0)
                    {
                        _activeSources.Enqueue(source);
                    }
                    else
                    {
                        _activeSourceSet.Remove(source);
                    }

                    FenLogger.Debug(
                        $"[TaskQueue] Dequeued: {task.Description} (Source: {task.Source}, RemainingSourceCount: {queue.Count}, RemainingTotal: {_count})",
                        LogCategory.JavaScript);
                    return task;
                }

                _count = 0;
                return null;
            }
        }

        /// <summary>
        /// Check if there are pending tasks
        /// </summary>
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

        /// <summary>
        /// Check if there are pending tasks for a specific source.
        /// </summary>
        public bool HasPendingTasksFor(TaskSource source)
        {
            lock (_lock)
            {
                return _tasksBySource.TryGetValue(source, out var queue) && queue.Count > 0;
            }
        }

        /// <summary>
        /// Number of pending tasks
        /// </summary>
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

        /// <summary>
        /// Number of pending tasks for a specific source.
        /// </summary>
        public int CountFor(TaskSource source)
        {
            lock (_lock)
            {
                return _tasksBySource.TryGetValue(source, out var queue) ? queue.Count : 0;
            }
        }

        /// <summary>
        /// Number of currently active task sources.
        /// </summary>
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

        /// <summary>
        /// Clear all pending tasks
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _tasksBySource.Clear();
                _activeSources.Clear();
                _activeSourceSet.Clear();
                _count = 0;
                FenLogger.Debug("[TaskQueue] Cleared all tasks", LogCategory.JavaScript);
            }
        }
    }
}
