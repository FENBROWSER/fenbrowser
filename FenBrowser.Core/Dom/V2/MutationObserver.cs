// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections.Generic;
using System.Threading;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: MutationObserver.
    /// https://dom.spec.whatwg.org/#mutationobserver
    /// </summary>
    public sealed class MutationObserver
    {
        private readonly Action<IReadOnlyList<MutationRecord>, MutationObserver> _callback;
        private readonly Action<Action> _deliveryScheduler;
        private readonly List<MutationRecord> _recordQueue = new();
        private readonly List<WeakReference<Node>> _observedNodes = new();
        private readonly object _instanceLock = new();
        private int _deliveryScheduled;
        private int _isDelivering;

        /// <summary>
        /// Creates an observer with immediate delivery scheduling. Browser scripting
        /// should use the scheduler overload so callbacks run at a mutation-observer
        /// microtask checkpoint rather than during the mutating DOM operation.
        /// </summary>
        public MutationObserver(Action<IReadOnlyList<MutationRecord>, MutationObserver> callback)
            : this(callback, null)
        {
        }

        /// <summary>
        /// Creates an observer whose delivery callback is enqueued by the supplied
        /// scheduler. The scheduler must enqueue the action; it should not execute the
        /// action inline when browser-spec microtask ordering is required.
        /// </summary>
        public MutationObserver(
            Action<IReadOnlyList<MutationRecord>, MutationObserver> callback,
            Action<Action> deliveryScheduler)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            _deliveryScheduler = deliveryScheduler;
        }

        public void Observe(Node target, MutationObserverInit options)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (!options.ChildList && !options.Attributes && !options.CharacterData)
            {
                throw new DomException("TypeError",
                    "At least one of childList, attributes, or characterData must be true");
            }

            if (options.AttributeOldValue && !options.Attributes)
                throw new DomException("TypeError", "attributeOldValue requires attributes to be true");

            if (options.AttributeFilter != null && options.AttributeFilter.Length > 0 && !options.Attributes)
                throw new DomException("TypeError", "attributeFilter requires attributes to be true");

            if (options.CharacterDataOldValue && !options.CharacterData)
                throw new DomException("TypeError", "characterDataOldValue requires characterData to be true");

            // Current DOM registration storage lives on ContainerNode. CharacterData
            // targets still require the planned node-level registration refactor.
            if (target is ContainerNode container)
                container.RegisterObserver(this, options);

            lock (_instanceLock)
            {
                _observedNodes.RemoveAll(wr => wr.TryGetTarget(out var n) && ReferenceEquals(n, target));
                _observedNodes.Add(new WeakReference<Node>(target));
            }
        }

        public void Disconnect()
        {
            List<WeakReference<Node>> nodesToUnregister;
            lock (_instanceLock)
            {
                nodesToUnregister = new List<WeakReference<Node>>(_observedNodes);
                _observedNodes.Clear();
                _recordQueue.Clear();
            }

            foreach (var weakRef in nodesToUnregister)
            {
                if (weakRef.TryGetTarget(out var node) && node is ContainerNode container)
                    container.UnregisterObserver(this);
            }
        }

        public IReadOnlyList<MutationRecord> TakeRecords()
        {
            lock (_instanceLock)
            {
                if (_recordQueue.Count == 0)
                    return Array.Empty<MutationRecord>();

                var records = _recordQueue.ToArray();
                _recordQueue.Clear();
                return records;
            }
        }

        internal void EnqueueRecord(MutationRecord record)
        {
            if (record == null)
                return;

            bool shouldSchedule = false;
            lock (_instanceLock)
            {
                _recordQueue.Add(record);
                if (_deliveryScheduled == 0)
                {
                    _deliveryScheduled = 1;
                    shouldSchedule = true;
                }
            }

            if (shouldSchedule)
                ScheduleDelivery();
        }

        private void ScheduleDelivery()
        {
            if (_deliveryScheduler != null)
            {
                _deliveryScheduler(DeliverPendingRecords);
            }
            else
            {
                DeliverPendingRecords();
            }
        }

        private void DeliverPendingRecords()
        {
            if (Interlocked.Exchange(ref _isDelivering, 1) != 0)
                return;

            bool reschedule = false;
            try
            {
                MutationRecord[] records;
                lock (_instanceLock)
                {
                    if (_recordQueue.Count == 0)
                    {
                        _deliveryScheduled = 0;
                        return;
                    }

                    records = _recordQueue.ToArray();
                    _recordQueue.Clear();
                }

                try
                {
                    _callback(records, this);
                }
                catch (Exception ex)
                {
                    // Observer exceptions are reported without aborting delivery state.
                    System.Diagnostics.Debug.WriteLine($"MutationObserver callback error: {ex}");
                }
            }
            finally
            {
                lock (_instanceLock)
                {
                    _deliveryScheduled = 0;
                    if (_recordQueue.Count > 0)
                    {
                        _deliveryScheduled = 1;
                        reschedule = true;
                    }
                }

                Interlocked.Exchange(ref _isDelivering, 0);
            }

            if (reschedule)
                ScheduleDelivery();
        }
    }

    public struct MutationObserverInit
    {
        public bool ChildList;
        public bool Attributes;
        public bool CharacterData;
        public bool Subtree;
        public bool AttributeOldValue;
        public bool CharacterDataOldValue;
        public string[] AttributeFilter;
    }

    public sealed class MutationRecord
    {
        public MutationRecordType Type { get; init; }
        public Node Target { get; init; }
        public IReadOnlyList<Node> AddedNodes { get; init; } = Array.Empty<Node>();
        public IReadOnlyList<Node> RemovedNodes { get; init; } = Array.Empty<Node>();
        public Node PreviousSibling { get; init; }
        public Node NextSibling { get; init; }
        public string AttributeName { get; init; }
        public string AttributeNamespace { get; init; }
        public string OldValue { get; init; }
    }

    public enum MutationRecordType
    {
        ChildList,
        Attributes,
        CharacterData
    }

    internal sealed class RegisteredObserverList
    {
        private readonly List<(WeakReference<MutationObserver> Observer, MutationObserverInit Options)> _list = new();
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

        public void Add(MutationObserver observer, MutationObserverInit options)
        {
            _lock.EnterWriteLock();
            try
            {
                _list.RemoveAll(x => x.Observer.TryGetTarget(out var o) && ReferenceEquals(o, observer));
                _list.Add((new WeakReference<MutationObserver>(observer), options));
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

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

        public void NotifyChildList(MutationRecord record)
        {
            var registrations = GetObserversForNotification((options, _) => options.ChildList);
            foreach (var registration in registrations)
                registration.Observer.EnqueueRecord(record);
        }

        public void NotifyAttributes(MutationRecord record)
        {
            var registrations = GetObserversForNotification(
                (options, attrName) => options.Attributes && MatchesAttributeFilter(options, attrName),
                record.AttributeName);

            foreach (var registration in registrations)
            {
                registration.Observer.EnqueueRecord(
                    registration.Options.AttributeOldValue
                        ? record
                        : CopyRecord(record, oldValue: null));
            }
        }

        public void NotifyCharacterData(MutationRecord record)
        {
            var registrations = GetObserversForNotification((options, _) => options.CharacterData);
            foreach (var registration in registrations)
            {
                registration.Observer.EnqueueRecord(
                    registration.Options.CharacterDataOldValue
                        ? record
                        : CopyRecord(record, oldValue: null));
            }
        }

        public void NotifySubtree(MutationRecord record)
        {
            var registrations = GetObserversForNotification(
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

            foreach (var registration in registrations)
            {
                string oldValue = record.Type switch
                {
                    MutationRecordType.Attributes when !registration.Options.AttributeOldValue => null,
                    MutationRecordType.CharacterData when !registration.Options.CharacterDataOldValue => null,
                    _ => record.OldValue
                };

                registration.Observer.EnqueueRecord(
                    ReferenceEquals(oldValue, record.OldValue)
                        ? record
                        : CopyRecord(record, oldValue));
            }
        }

        private List<(MutationObserver Observer, MutationObserverInit Options)> GetObserversForNotification(
            Func<MutationObserverInit, string, bool> filter,
            string attributeName = null)
        {
            var result = new List<(MutationObserver Observer, MutationObserverInit Options)>();
            bool foundDeadReference = false;

            _lock.EnterReadLock();
            try
            {
                for (int i = 0; i < _list.Count; i++)
                {
                    var (weakRef, options) = _list[i];
                    if (!weakRef.TryGetTarget(out var observer))
                    {
                        foundDeadReference = true;
                        continue;
                    }

                    if (filter(options, attributeName))
                        result.Add((observer, options));
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            if (foundDeadReference)
            {
                _lock.EnterWriteLock();
                try
                {
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

        private static MutationRecord CopyRecord(MutationRecord record, string oldValue)
        {
            return new MutationRecord
            {
                Type = record.Type,
                Target = record.Target,
                AddedNodes = record.AddedNodes,
                RemovedNodes = record.RemovedNodes,
                PreviousSibling = record.PreviousSibling,
                NextSibling = record.NextSibling,
                AttributeName = record.AttributeName,
                AttributeNamespace = record.AttributeNamespace,
                OldValue = oldValue
            };
        }

        public void Dispose()
        {
            _lock.Dispose();
        }
    }
}
