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
        /// Microtasks enqueued during draining are also processed (WHATWG HTML §8.1.7.3).
        /// Re-entrant calls (e.g. from within a microtask callback) return immediately;
        /// the outer drain loop will pick up any newly-enqueued items on its next iteration.
        /// </summary>
        public void DrainAll()
        {
            // Guard check and set are atomic within the same lock acquisition so no
            // other thread can slip between them.
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

            int processed = 0;
            try
            {
                while (true)
                {
                    Action microtask;
                    lock (_lock)
                    {
                        if (_microtasks.Count == 0)
                            break;

                        if (_drainDepth >= MaxDrainDepth)
                        {
                            FenLogger.Debug($"[MicrotaskQueue] Max drain depth ({MaxDrainDepth}) exceeded, clearing queue", LogCategory.Errors);
                            _microtasks.Clear();
                            break;
                        }

                        _drainDepth++;
                        microtask = _microtasks.Dequeue();
                    }

                    processed++;
                    try
                    {
                        microtask();
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Debug($"[MicrotaskQueue] Microtask error: {ex.Message}", LogCategory.Errors);
                    }
                }
            }
            finally
            {
                // Reset _isDraining while holding the lock so that any producer thread
                // that enqueued after the while-loop's empty check but before this reset
                // is guaranteed to see _isDraining = false on its next DrainAll() call
                // (or will have already enqueued into a queue the next checkpoint will drain).
                lock (_lock)
                {
                    _isDraining = false;
                }
            }

            FenLogger.Debug($"[MicrotaskQueue] Drain complete (processed: {processed})", LogCategory.JavaScript);
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
