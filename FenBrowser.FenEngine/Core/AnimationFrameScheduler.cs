// =============================================================================
// AnimationFrameScheduler.cs
// FenBrowser requestAnimationFrame Implementation
// 
// SPEC REFERENCE: WHATWG HTML §8.11.3 - Animation Frames
//                 https://html.spec.whatwg.org/multipage/imagebitmap-and-animations.html#animation-frames
// 
// PURPOSE: Manages requestAnimationFrame callbacks with proper timing
//          and cancellation support.
// 
// STATUS: ✅ Implemented
// =============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Callback for requestAnimationFrame.
    /// </summary>
    /// <param name="timestamp">High-resolution timestamp in milliseconds since time origin.</param>
    public delegate void AnimationFrameCallback(double timestamp);

    /// <summary>
    /// Represents a registered animation frame callback.
    /// </summary>
    public sealed class AnimationFrameHandle
    {
        /// <summary>Unique identifier for this callback.</summary>
        public readonly long Id;

        /// <summary>The callback to invoke.</summary>
        public readonly AnimationFrameCallback Callback;

        /// <summary>Whether this callback has been cancelled.</summary>
        public bool IsCancelled { get; internal set; }

        /// <summary>When this callback was registered.</summary>
        public readonly DateTime RegisteredAt;

        internal AnimationFrameHandle(long id, AnimationFrameCallback callback)
        {
            Id = id;
            Callback = callback;
            IsCancelled = false;
            RegisteredAt = DateTime.UtcNow;
        }

        public override string ToString()
        {
            return $"RAF[{Id}, Cancelled={IsCancelled}]";
        }
    }

    /// <summary>
    /// Manages requestAnimationFrame callbacks per WHATWG spec.
    /// 
    /// Key behaviors:
    /// 1. Callbacks are collected into a snapshot before processing
    /// 2. New callbacks registered during processing run NEXT frame
    /// 3. Cancelled callbacks are skipped
    /// 4. High-resolution timestamp is passed to each callback
    /// </summary>
    public sealed class AnimationFrameScheduler
    {
        private static AnimationFrameScheduler _instance;
        
        /// <summary>
        /// Singleton instance.
        /// </summary>
        public static AnimationFrameScheduler Instance => _instance ??= new AnimationFrameScheduler();

        // Counter for generating unique IDs
        private long _nextId = 1;

        // Pending callbacks (to be processed next frame)
        private readonly List<AnimationFrameHandle> _pendingCallbacks = new();
        private readonly object _pendingLock = new();

        // Currently executing callbacks (snapshot for this frame)
        private List<AnimationFrameHandle> _executingCallbacks = new();

        // Cancelled callback IDs
        private readonly HashSet<long> _cancelledIds = new();
        private readonly object _cancelledLock = new();

        // Time origin for high-resolution timestamps
        private readonly DateTime _timeOrigin = DateTime.UtcNow;

        // Statistics
        private long _totalScheduled = 0;
        private long _totalExecuted = 0;
        private long _totalCancelled = 0;

        private AnimationFrameScheduler() { }

        #region Public API

        /// <summary>
        /// Request an animation frame callback (requestAnimationFrame).
        /// </summary>
        /// <param name="callback">Callback to invoke on next frame.</param>
        /// <returns>Handle that can be used to cancel the callback.</returns>
        public AnimationFrameHandle RequestAnimationFrame(AnimationFrameCallback callback)
        {
            if (callback  == null)
                throw new ArgumentNullException(nameof(callback));

            var id = Interlocked.Increment(ref _nextId);
            var handle = new AnimationFrameHandle(id, callback);

            lock (_pendingLock)
            {
                _pendingCallbacks.Add(handle);
            }

            Interlocked.Increment(ref _totalScheduled);

#if DEBUG
            FenLogger.Debug($"[RAF] Scheduled callback {id}", LogCategory.JavaScript);
#endif

            return handle;
        }

        /// <summary>
        /// Request an animation frame callback (requestAnimationFrame).
        /// Returns the callback ID for cancellation.
        /// </summary>
        public long RequestAnimationFrame(Action<double> callback)
        {
            var handle = RequestAnimationFrame(new AnimationFrameCallback(callback));
            return handle.Id;
        }

        /// <summary>
        /// Cancel a pending animation frame callback (cancelAnimationFrame).
        /// </summary>
        /// <param name="handle">Handle returned from RequestAnimationFrame.</param>
        public void CancelAnimationFrame(AnimationFrameHandle handle)
        {
            if (handle  == null) return;
            CancelAnimationFrame(handle.Id);
            handle.IsCancelled = true;
        }

        /// <summary>
        /// Cancel a pending animation frame callback by ID.
        /// </summary>
        /// <param name="callbackId">ID returned from RequestAnimationFrame.</param>
        public void CancelAnimationFrame(long callbackId)
        {
            lock (_cancelledLock)
            {
                _cancelledIds.Add(callbackId);
            }

            Interlocked.Increment(ref _totalCancelled);

#if DEBUG
            FenLogger.Debug($"[RAF] Cancelled callback {callbackId}", LogCategory.JavaScript);
#endif
        }

        /// <summary>
        /// Number of pending callbacks waiting for next frame.
        /// </summary>
        public int PendingCount
        {
            get
            {
                lock (_pendingLock)
                {
                    return _pendingCallbacks.Count;
                }
            }
        }

        /// <summary>
        /// Returns true if there are pending callbacks.
        /// </summary>
        public bool HasPendingCallbacks => PendingCount > 0;

        /// <summary>
        /// Current high-resolution timestamp in milliseconds since time origin.
        /// </summary>
        public double CurrentTimestamp => (DateTime.UtcNow - _timeOrigin).TotalMilliseconds;

        /// <summary>
        /// Total callbacks scheduled since creation.
        /// </summary>
        public long TotalScheduled => _totalScheduled;

        /// <summary>
        /// Total callbacks executed since creation.
        /// </summary>
        public long TotalExecuted => _totalExecuted;

        /// <summary>
        /// Total callbacks cancelled since creation.
        /// </summary>
        public long TotalCancelled => _totalCancelled;

        #endregion

        #region Frame Processing

        /// <summary>
        /// Process all pending animation frame callbacks.
        /// Called once per frame by the event loop.
        /// </summary>
        /// <returns>Number of callbacks executed.</returns>
        public int ProcessAnimationFrames()
        {
            // STEP 1: Take a snapshot of pending callbacks
            List<AnimationFrameHandle> snapshot;
            lock (_pendingLock)
            {
                if (_pendingCallbacks.Count == 0)
                    return 0;

                snapshot = new List<AnimationFrameHandle>(_pendingCallbacks);
                _pendingCallbacks.Clear();
            }

            // STEP 2: Get current timestamp
            var timestamp = CurrentTimestamp;

            // STEP 3: Get cancelled IDs and clear
            HashSet<long> cancelled;
            lock (_cancelledLock)
            {
                cancelled = new HashSet<long>(_cancelledIds);
                _cancelledIds.Clear();
            }

            // STEP 4: Execute non-cancelled callbacks
            int executed = 0;
            _executingCallbacks = snapshot;

            foreach (var handle in snapshot)
            {
                // Skip cancelled callbacks
                if (cancelled.Contains(handle.Id) || handle.IsCancelled)
                {
                    continue;
                }

                try
                {
                    handle.Callback(timestamp);
                    executed++;
                    Interlocked.Increment(ref _totalExecuted);
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[RAF] Callback {handle.Id} threw: {ex.Message}", LogCategory.Errors);
                }
            }

            _executingCallbacks = new List<AnimationFrameHandle>();

#if DEBUG
            if (executed > 0)
            {
                FenLogger.Debug($"[RAF] Executed {executed} callbacks at t={timestamp:F2}ms", LogCategory.JavaScript);
            }
#endif

            return executed;
        }

        /// <summary>
        /// Clear all pending callbacks.
        /// </summary>
        public void Clear()
        {
            lock (_pendingLock)
            {
                _pendingCallbacks.Clear();
            }
            lock (_cancelledLock)
            {
                _cancelledIds.Clear();
            }
            _executingCallbacks.Clear();
        }

        /// <summary>
        /// Reset singleton (for testing).
        /// </summary>
        public static void ResetInstance()
        {
            _instance = new AnimationFrameScheduler();
        }

        #endregion

        public override string ToString()
        {
            return $"AnimationFrameScheduler[Pending={PendingCount}, Total={TotalScheduled}, Executed={TotalExecuted}, Cancelled={TotalCancelled}]";
        }
    }
}
