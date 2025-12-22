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
    /// Task queue for the event loop - processes tasks in FIFO order
    /// </summary>
    public class TaskQueue
    {
        private readonly Queue<ScheduledTask> _tasks = new();
        private readonly object _lock = new();

        /// <summary>
        /// Enqueue a task for execution
        /// </summary>
        public void Enqueue(ScheduledTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            lock (_lock)
            {
                _tasks.Enqueue(task);
                FenLogger.Debug($"[TaskQueue] Enqueued: {task.Description} (Source: {task.Source}, Count: {_tasks.Count})", LogCategory.JavaScript);
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
                if (_tasks.Count == 0)
                    return null;

                var task = _tasks.Dequeue();
                FenLogger.Debug($"[TaskQueue] Dequeued: {task.Description} (Remaining: {_tasks.Count})", LogCategory.JavaScript);
                return task;
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
                    return _tasks.Count > 0;
                }
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
                    return _tasks.Count;
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
                _tasks.Clear();
                FenLogger.Debug("[TaskQueue] Cleared all tasks", LogCategory.JavaScript);
            }
        }
    }
}
