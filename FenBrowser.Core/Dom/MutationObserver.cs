using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Dom
{
    public class MutationObserver
    {
        private Action<List<MutationRecord>, MutationObserver> _callback;
        private List<MutationRecord> _recordQueue = new List<MutationRecord>();
        private List<Node> _nodes = new List<Node>(); // Weak references in a real engine, strong here for now

        // Spec: "microtask queue"
        // We will simulate this by having a static list of observers that have pending records
        private static HashSet<MutationObserver> _pendingObservers = new HashSet<MutationObserver>();
        private static object _lock = new object();
        private static bool _scheduled = false;

        public MutationObserver(Action<List<MutationRecord>, MutationObserver> callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public void Observe(Node target, MutationObserverInit options)
        {
            if (target == null) return;
            // Native: Register this observer on the node with specific options
            target.RegisterObserver(this, options);
            if (!_nodes.Contains(target)) _nodes.Add(target);
            FenLogger.Debug($"[MutationObserver] Observing {target.NodeName}", LogCategory.DOM);
        }

        public void Disconnect()
        {
            foreach (var node in _nodes)
            {
                node.UnregisterObserver(this);
            }
            _nodes.Clear();
            lock (_lock)
            {
                _recordQueue.Clear();
                _pendingObservers.Remove(this);
            }
            FenLogger.Debug("[MutationObserver] Disconnected", LogCategory.DOM);
        }

        public List<MutationRecord> TakeRecords()
        {
            lock (_lock)
            {
                var list = new List<MutationRecord>(_recordQueue);
                _recordQueue.Clear();
                _pendingObservers.Remove(this); // No longer pending
                return list;
            }
        }

        internal void EnqueueRecord(MutationRecord record)
        {
            lock (_lock)
            {
                _recordQueue.Add(record);
                if (!_pendingObservers.Contains(this))
                {
                    _pendingObservers.Add(this);
                    ScheduleMicrotask();
                }
            }
        }

        private static void ScheduleMicrotask()
        {
            if (_scheduled) return;
            _scheduled = true;
            
            // Simulate Microtask Checkpoint: Run asynchronously "soon"
            // In a real engine, this is triggered at end of script execution.
            Task.Run(async () => 
            {
                await Task.Yield(); // Hop to thread pool or next tick
                ProcessRecords();
            });
        }

        private static void ProcessRecords()
        {
            List<MutationObserver> toNotify;
            lock (_lock)
            {
                _scheduled = false;
                if (_pendingObservers.Count == 0) return;
                toNotify = new List<MutationObserver>(_pendingObservers);
                _pendingObservers.Clear();
            }

            foreach (var obs in toNotify)
            {
                List<MutationRecord> records;
                lock (_lock) // Each observer lock? Or global?
                {
                    // Taking records out of the observer
                    records = obs.TakeRecordsInternal(); 
                }

                if (records != null && records.Count > 0)
                {
                    try
                    {
                        obs._callback(records, obs);
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Error($"[MutationObserver] Callback error: {ex.Message}", LogCategory.DOM, ex);
                    }
                }
            }
        }

        private List<MutationRecord> TakeRecordsInternal()
        {
             // Assumes external lock (global _lock for now to simplify concurency)
             // In robust impl, _recordQueue should be locked by 'this'
             var list = new List<MutationRecord>(_recordQueue);
             _recordQueue.Clear();
             return list;
        }
    }
}
