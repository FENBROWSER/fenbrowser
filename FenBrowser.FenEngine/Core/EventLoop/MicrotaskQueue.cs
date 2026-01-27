using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Core.EventLoop
{
    /// <summary>
    /// Microtask queue for the event loop.
    /// Microtasks are drained completely at each checkpoint before proceeding.
    /// Sources: Promise reactions, queueMicrotask(), MutationObserver callbacks
    /// </summary>
    public class MicrotaskQueue
    {
        private readonly Queue<Action> _microtasks = new();
        private readonly object _lock = new();
        private bool _isDraining = false;
        private int _drainDepth = 0;
        private const int MaxDrainDepth = 1000; // Prevent infinite loops

        /// <summary>
        /// Enqueue a microtask for execution at the next checkpoint
        /// </summary>
        public void Enqueue(Action microtask)
        {
            if (microtask  == null) throw new ArgumentNullException(nameof(microtask));

            lock (_lock)
            {
                _microtasks.Enqueue(microtask);
                FenLogger.Debug($"[MicrotaskQueue] Enqueued microtask (Count: {_microtasks.Count})", LogCategory.JavaScript);
            }
        }

        /// <summary>
        /// Drain ALL microtasks until the queue is empty.
        /// Microtasks enqueued during draining are also processed.
        /// </summary>
        public void DrainAll()
        {
            lock (_lock)
            {
                if (_isDraining)
                {
                    FenLogger.Debug("[MicrotaskQueue] Already draining, skipping", LogCategory.JavaScript);
                    return;
                }
                _isDraining = true;
                _drainDepth = 0;
            }

            try
            {
                while (true)
                {
                    Action microtask;
                    lock (_lock)
                    {
                        if (_microtasks.Count == 0)
                            break;

                        _drainDepth++;
                        if (_drainDepth > MaxDrainDepth)
                        {
                            FenLogger.Debug($"[MicrotaskQueue] Max drain depth ({MaxDrainDepth}) exceeded, clearing queue", LogCategory.Errors);
                            _microtasks.Clear();
                            break;
                        }

                        microtask = _microtasks.Dequeue();
                    }

                    try
                    {
                        microtask();
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Debug($"[MicrotaskQueue] Microtask error: {ex.Message}", LogCategory.Errors);
                    }
                }

                FenLogger.Debug($"[MicrotaskQueue] Drain complete (processed: {_drainDepth})", LogCategory.JavaScript);
            }
            finally
            {
                lock (_lock)
                {
                    _isDraining = false;
                }
            }
        }

        /// <summary>
        /// Check if there are pending microtasks
        /// </summary>
        public bool HasPendingMicrotasks
        {
            get
            {
                lock (_lock)
                {
                    return _microtasks.Count > 0;
                }
            }
        }

        /// <summary>
        /// Number of pending microtasks
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _microtasks.Count;
                }
            }
        }

        /// <summary>
        /// Check if currently draining
        /// </summary>
        public bool IsDraining
        {
            get
            {
                lock (_lock)
                {
                    return _isDraining;
                }
            }
        }

        /// <summary>
        /// Clear all pending microtasks
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _microtasks.Clear();
                FenLogger.Debug("[MicrotaskQueue] Cleared all microtasks", LogCategory.JavaScript);
            }
        }
    }
}
