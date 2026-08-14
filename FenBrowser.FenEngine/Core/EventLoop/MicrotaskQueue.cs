// SpecRef: WHATWG HTML, microtask checkpoint processing
// CapabilityId: EVENTLOOP-MICROTASK-01
// Determinism: strict
// FallbackPolicy: spec-defined
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
        private sealed class ScheduledMicrotask
        {
            public ScheduledMicrotask(Action callback)
            {
                Callback = callback ?? throw new ArgumentNullException(nameof(callback));
                TraceId = EventLoopTrace.NextId("microtask");
            }

            public Action Callback { get; }
            public string TraceId { get; }
        }

        private readonly Queue<ScheduledMicrotask> _microtasks = new();
        private readonly object _lock = new();
        private bool _isDraining = false;
        private const int MaxMicrotasksPerDrainPass = 1000;

        /// <summary>
        /// Enqueue a microtask for execution at the next checkpoint
        /// </summary>
        public void Enqueue(Action microtask)
        {
            if (microtask == null) throw new ArgumentNullException(nameof(microtask));

            var entry = new ScheduledMicrotask(microtask);
            int pendingCount;
            lock (_lock)
            {
                _microtasks.Enqueue(entry);
                pendingCount = _microtasks.Count;
                EngineLogCompat.Debug($"[MicrotaskQueue] Enqueued microtask (Count: {pendingCount})", LogCategory.JavaScript);
            }

            EventLoopTrace.Write(
                "MicrotaskQueued",
                LogSeverity.Debug,
                "[EventLoop] Microtask queued",
                entry.TraceId,
                new Dictionary<string, object>
                {
                    ["pendingCount"] = pendingCount
                });
        }

        /// <summary>
        /// Drain a bounded pass of microtasks. Microtasks enqueued during draining
        /// remain eligible in the same pass until the pass budget is reached.
        /// Re-entrant calls return immediately; the outer checkpoint owns draining.
        ///
        /// The pass budget is a liveness guard, not a data-loss policy. Remaining
        /// microtasks stay queued so EventLoopCoordinator can continue the checkpoint
        /// or yield safely without silently dropping Promise/queueMicrotask work.
        /// </summary>
        public int DrainAll()
        {
            lock (_lock)
            {
                if (_isDraining)
                {
                    EngineLogCompat.Debug("[MicrotaskQueue] Already draining, skipping", LogCategory.JavaScript);
                    return 0;
                }
                _isDraining = true;
            }

            int processed = 0;
            bool passBudgetReached = false;
            try
            {
                while (processed < MaxMicrotasksPerDrainPass)
                {
                    ScheduledMicrotask microtask;
                    lock (_lock)
                    {
                        if (_microtasks.Count == 0)
                            break;

                        microtask = _microtasks.Dequeue();
                    }

                    processed++;
                    try
                    {
                        microtask.Callback();
                        EventLoopTrace.Write(
                            "MicrotaskExecuted",
                            LogSeverity.Debug,
                            "[EventLoop] Microtask executed",
                            microtask.TraceId,
                            new Dictionary<string, object>
                            {
                                ["drainIndex"] = processed
                            });
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug($"[MicrotaskQueue] Microtask error: {ex.Message}", LogCategory.Errors);
                        EventLoopTrace.Write(
                            "MicrotaskFailed",
                            LogSeverity.Warn,
                            "[EventLoop] Microtask failed",
                            microtask.TraceId,
                            new Dictionary<string, object>
                            {
                                ["drainIndex"] = processed,
                                ["errorType"] = ex.GetType().Name,
                                ["error"] = ex.Message
                            },
                            LogMarker.EngineBug);
                    }
                }

                lock (_lock)
                {
                    passBudgetReached = processed >= MaxMicrotasksPerDrainPass && _microtasks.Count > 0;
                }

                if (passBudgetReached)
                {
                    EngineLogCompat.Warn(
                        $"[MicrotaskQueue] Drain pass budget ({MaxMicrotasksPerDrainPass}) reached; preserving remaining microtasks for the checkpoint",
                        LogCategory.Errors);
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isDraining = false;
                }
            }

            EngineLogCompat.Debug(
                $"[MicrotaskQueue] Drain pass complete (processed: {processed}, budgetReached: {passBudgetReached})",
                LogCategory.JavaScript);
            return processed;
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
                EngineLogCompat.Debug("[MicrotaskQueue] Cleared all microtasks", LogCategory.JavaScript);
            }
        }
    }
}
