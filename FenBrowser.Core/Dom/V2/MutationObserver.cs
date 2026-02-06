// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: MutationObserver.
    /// https://dom.spec.whatwg.org/#mutationobserver
    ///
    /// Thread-safe implementation with WeakReference to avoid memory leaks.
    /// </summary>
    public sealed class MutationObserver
    {
        private readonly Action<IReadOnlyList<MutationRecord>, MutationObserver> _callback;
        private readonly List<MutationRecord> _recordQueue = new();
        private readonly List<WeakReference<Node>> _observedNodes = new();

        // Instance-level lock for this observer's state
        private readonly object _instanceLock = new();

        // Static scheduling for microtask-like behavior
        private static readonly HashSet<MutationObserver> _pendingObservers = new();
        private static readonly object _staticLock = new();
        private static int _microtaskScheduled; // Use int for Interlocked operations

        /// <summary>
        /// Creates a new MutationObserver with the given callback.
        /// </summary>
        public MutationObserver(Action<IReadOnlyList<MutationRecord>, MutationObserver> callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        /// <summary>
        /// Begins observing the target node for mutations.
        /// https://dom.spec.whatwg.org/#dom-mutationobserver-observe
        /// Thread-safe.
        /// </summary>
        public void Observe(Node target, MutationObserverInit options)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            // Validate options (no locking needed - just reading parameters)
            if (!options.ChildList && !options.Attributes && !options.CharacterData)
            {
                throw new DomException("TypeError",
                    "At least one of childList, attributes, or characterData must be true");
            }

            if (options.AttributeOldValue && !options.Attributes)
                throw new DomException("TypeError",
                    "attributeOldValue requires attributes to be true");

            if (options.AttributeFilter != null && options.AttributeFilter.Length > 0 && !options.Attributes)
                throw new DomException("TypeError",
                    "attributeFilter requires attributes to be true");

            if (options.CharacterDataOldValue && !options.CharacterData)
                throw new DomException("TypeError",
                    "characterDataOldValue requires characterData to be true");

            // Register with the target node
            if (target is ContainerNode container)
                container.RegisterObserver(this, options);

            // Track for cleanup (instance-level lock)
            lock (_instanceLock)
            {
                // Remove existing weak ref to this node if any
                _observedNodes.RemoveAll(wr => wr.TryGetTarget(out var n) && ReferenceEquals(n, target));
                _observedNodes.Add(new WeakReference<Node>(target));
            }
        }

        /// <summary>
        /// Stops observing all targets.
        /// https://dom.spec.whatwg.org/#dom-mutationobserver-disconnect
        /// Thread-safe.
        /// </summary>
        public void Disconnect()
        {
            List<WeakReference<Node>> nodesToUnregister;

            // First, copy the nodes list under instance lock
            lock (_instanceLock)
            {
                nodesToUnregister = new List<WeakReference<Node>>(_observedNodes);
                _observedNodes.Clear();
                _recordQueue.Clear();
            }

            // Unregister from nodes outside the lock to avoid deadlocks
            foreach (var weakRef in nodesToUnregister)
            {
                if (weakRef.TryGetTarget(out var node) && node is ContainerNode container)
                    container.UnregisterObserver(this);
            }

            // Remove from pending observers (static lock)
            lock (_staticLock)
            {
                _pendingObservers.Remove(this);
            }
        }

        /// <summary>
        /// Returns and empties the record queue.
        /// https://dom.spec.whatwg.org/#dom-mutationobserver-takerecords
        /// Thread-safe.
        /// </summary>
        public IReadOnlyList<MutationRecord> TakeRecords()
        {
            MutationRecord[] records;

            // Take records under instance lock
            lock (_instanceLock)
            {
                records = _recordQueue.ToArray();
                _recordQueue.Clear();
            }

            // Remove from pending (static lock)
            lock (_staticLock)
            {
                _pendingObservers.Remove(this);
            }

            return records;
        }

        /// <summary>
        /// Enqueues a mutation record for this observer.
        /// Thread-safe - can be called from multiple threads.
        /// </summary>
        internal void EnqueueRecord(MutationRecord record)
        {
            bool shouldSchedule = false;

            // Add record under instance lock
            lock (_instanceLock)
            {
                _recordQueue.Add(record);
            }

            // Check if we need to schedule (static lock)
            lock (_staticLock)
            {
                if (_pendingObservers.Add(this))
                    shouldSchedule = Interlocked.Exchange(ref _microtaskScheduled, 1) == 0;
            }

            if (shouldSchedule)
                ScheduleMicrotask();
        }

        private static void ScheduleMicrotask()
        {
            // Queue callback on thread pool to simulate microtask timing
            _ = Task.Run(async () =>
            {
                // Yield to simulate microtask timing
                await Task.Yield();
                ProcessPendingObservers();
            });
        }

        private static void ProcessPendingObservers()
        {
            List<MutationObserver> observers;
            lock (_staticLock)
            {
                Interlocked.Exchange(ref _microtaskScheduled, 0);
                observers = new List<MutationObserver>(_pendingObservers);
                _pendingObservers.Clear();
            }

            foreach (var observer in observers)
            {
                var records = observer.TakeRecords();
                if (records.Count > 0)
                {
                    try
                    {
                        observer._callback(records, observer);
                    }
                    catch (Exception ex)
                    {
                        // Log error but continue processing other observers
                        System.Diagnostics.Debug.WriteLine($"MutationObserver callback error: {ex}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Options for MutationObserver.observe().
    /// https://dom.spec.whatwg.org/#dictdef-mutationobserverinit
    /// </summary>
    public struct MutationObserverInit
    {
        /// <summary>
        /// Whether to observe child list changes.
        /// </summary>
        public bool ChildList;

        /// <summary>
        /// Whether to observe attribute changes.
        /// </summary>
        public bool Attributes;

        /// <summary>
        /// Whether to observe character data changes.
        /// </summary>
        public bool CharacterData;

        /// <summary>
        /// Whether to observe the entire subtree.
        /// </summary>
        public bool Subtree;

        /// <summary>
        /// Whether to record old attribute values.
        /// </summary>
        public bool AttributeOldValue;

        /// <summary>
        /// Whether to record old character data values.
        /// </summary>
        public bool CharacterDataOldValue;

        /// <summary>
        /// List of attribute names to observe (null = all).
        /// </summary>
        public string[] AttributeFilter;
    }

    /// <summary>
    /// Represents a single mutation.
    /// https://dom.spec.whatwg.org/#mutationrecord
    /// </summary>
    public sealed class MutationRecord
    {
        /// <summary>
        /// The type of mutation ("attributes", "characterData", "childList").
        /// </summary>
        public MutationRecordType Type { get; init; }

        /// <summary>
        /// The node that was mutated.
        /// </summary>
        public Node Target { get; init; }

        /// <summary>
        /// The nodes added (for childList mutations).
        /// </summary>
        public IReadOnlyList<Node> AddedNodes { get; init; } = Array.Empty<Node>();

        /// <summary>
        /// The nodes removed (for childList mutations).
        /// </summary>
        public IReadOnlyList<Node> RemovedNodes { get; init; } = Array.Empty<Node>();

        /// <summary>
        /// The previous sibling of added/removed nodes.
        /// </summary>
        public Node PreviousSibling { get; init; }

        /// <summary>
        /// The next sibling of added/removed nodes.
        /// </summary>
        public Node NextSibling { get; init; }

        /// <summary>
        /// The name of the changed attribute (for attribute mutations).
        /// </summary>
        public string AttributeName { get; init; }

        /// <summary>
        /// The namespace of the changed attribute.
        /// </summary>
        public string AttributeNamespace { get; init; }

        /// <summary>
        /// The old value (for attribute/characterData mutations).
        /// </summary>
        public string OldValue { get; init; }
    }

    /// <summary>
    /// Mutation record type enumeration.
    /// </summary>
    public enum MutationRecordType
    {
        /// <summary>Child list was modified.</summary>
        ChildList,
        /// <summary>An attribute was modified.</summary>
        Attributes,
        /// <summary>Character data was modified.</summary>
        CharacterData
    }

    /// <summary>
    /// Internal list of registered observers for a node.
    /// Uses WeakReference to prevent memory leaks.
    /// Thread-safe implementation.
    /// </summary>
    internal sealed class RegisteredObserverList
    {
        private readonly List<(WeakReference<MutationObserver> Observer, MutationObserverInit Options)> _list = new();
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

        /// <summary>
        /// Adds or updates an observer registration.
        /// Thread-safe.
        /// </summary>
        public void Add(MutationObserver observer, MutationObserverInit options)
        {
            _lock.EnterWriteLock();
            try
            {
                // Remove existing registration for this observer
                _list.RemoveAll(x => x.Observer.TryGetTarget(out var o) && ReferenceEquals(o, observer));
                _list.Add((new WeakReference<MutationObserver>(observer), options));
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Removes an observer registration.
        /// Thread-safe.
        /// </summary>
        public void Remove(MutationObserver observer)
        {
            _lock.EnterWriteLock();
            try
            {
                _list.RemoveAll(x => x.Observer.TryGetTarget(out var o) && ReferenceEquals(o, observer));
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Notifies observers of child list changes.
        /// Thread-safe.
        /// </summary>
        public void NotifyChildList(MutationRecord record)
        {
            var observersToNotify = GetObserversForNotification(
                (options, _) => options.ChildList);

            foreach (var observer in observersToNotify)
                observer.EnqueueRecord(record);
        }

        /// <summary>
        /// Notifies observers of attribute changes.
        /// Thread-safe.
        /// </summary>
        public void NotifyAttributes(MutationRecord record)
        {
            var observersToNotify = GetObserversForNotification(
                (options, attrName) =>
                {
                    if (!options.Attributes)
                        return false;

                    // Check attribute filter
                    if (options.AttributeFilter == null || options.AttributeFilter.Length == 0)
                        return true;

                    foreach (var filter in options.AttributeFilter)
                    {
                        if (string.Equals(filter, attrName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    return false;
                },
                record.AttributeName);

            foreach (var observer in observersToNotify)
                observer.EnqueueRecord(record);
        }

        /// <summary>
        /// Notifies observers of character data changes.
        /// Thread-safe.
        /// </summary>
        public void NotifyCharacterData(MutationRecord record)
        {
            var observersToNotify = GetObserversForNotification(
                (options, _) => options.CharacterData);

            foreach (var observer in observersToNotify)
                observer.EnqueueRecord(record);
        }

        /// <summary>
        /// Notifies observers with subtree option.
        /// Thread-safe.
        /// </summary>
        public void NotifySubtree(MutationRecord record)
        {
            var observersToNotify = GetObserversForNotification(
                (options, attrName) =>
                {
                    if (!options.Subtree)
                        return false;

                    return record.Type switch
                    {
                        MutationRecordType.ChildList => options.ChildList,
                        MutationRecordType.Attributes => options.Attributes && MatchesAttributeFilter(options, attrName),
                        MutationRecordType.CharacterData => options.CharacterData,
                        _ => false
                    };
                },
                record.AttributeName);

            foreach (var observer in observersToNotify)
                observer.EnqueueRecord(record);
        }

        /// <summary>
        /// Gets observers that match the filter, removing dead references.
        /// </summary>
        private List<MutationObserver> GetObserversForNotification(
            Func<MutationObserverInit, string, bool> filter,
            string attributeName = null)
        {
            var result = new List<MutationObserver>();
            var deadRefs = new List<int>();

            _lock.EnterReadLock();
            try
            {
                for (int i = 0; i < _list.Count; i++)
                {
                    var (weakRef, options) = _list[i];
                    if (!weakRef.TryGetTarget(out var observer))
                    {
                        deadRefs.Add(i);
                        continue;
                    }

                    if (filter(options, attributeName))
                        result.Add(observer);
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            // Clean up dead references if any found
            if (deadRefs.Count > 0)
            {
                _lock.EnterWriteLock();
                try
                {
                    // Re-check and remove (indices may have changed)
                    _list.RemoveAll(x => !x.Observer.TryGetTarget(out _));
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            return result;
        }

        private static bool MatchesAttributeFilter(MutationObserverInit options, string attributeName)
        {
            if (options.AttributeFilter == null || options.AttributeFilter.Length == 0)
                return true;

            foreach (var filter in options.AttributeFilter)
            {
                if (string.Equals(filter, attributeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            _lock?.Dispose();
        }
    }
}
