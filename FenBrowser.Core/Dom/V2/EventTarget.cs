// WHATWG DOM Living Standard implementation
// FenBrowser.Core.Dom.V2

using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard EventTarget base.
    /// Listener mutation is copy-on-write so the high-frequency dispatch path can
    /// consume immutable snapshots without allocating one List per target/event.
    /// </summary>
    public abstract class EventTarget
    {
        private EventListenerStorage _listeners;
        private readonly object _listenerLock = new();

        public void AddEventListener(string type, EventListener callback, bool capture = false)
        {
            AddEventListener(type, callback, new AddEventListenerOptions { Capture = capture });
        }

        public void AddEventListener(string type, EventListener callback, AddEventListenerOptions options)
        {
            if (string.IsNullOrEmpty(type) || callback == null)
                return;

            bool added;
            lock (_listenerLock)
            {
                var storage = _listeners ??= new EventListenerStorage();
                added = storage.Add(type, callback, options, this);
                if (storage.IsEmpty && ReferenceEquals(_listeners, storage))
                    _listeners = null;
            }

            if (added)
                OnEventListenersChanged();
        }

        public void RemoveEventListener(string type, EventListener callback, bool capture = false)
        {
            if (string.IsNullOrEmpty(type) || callback == null)
                return;

            bool removed = false;
            lock (_listenerLock)
            {
                if (_listeners == null)
                    return;

                removed = _listeners.Remove(type, callback, capture);
                if (_listeners.IsEmpty)
                    _listeners = null;
            }

            if (removed)
                OnEventListenersChanged();
        }

        public bool DispatchEvent(Event evt)
        {
            if (evt == null)
                throw new ArgumentNullException(nameof(evt));
            if (evt.DispatchFlag)
                throw new DomException("InvalidStateError", "Event is already being dispatched");
            if (!evt.Initialized)
                throw new DomException("InvalidStateError", "Event has not been initialized");

            evt.IsTrusted = false;
            return EventDispatcher.Dispatch(evt, this);
        }

        internal bool TryGetEventListeners(string type, out EventListenerEntry[] listeners)
        {
            lock (_listenerLock)
            {
                if (_listeners is not null)
                    return _listeners.TryGetSnapshot(type, out listeners);

                listeners = null;
                return false;
            }
        }

        internal bool HasEventListeners
        {
            get
            {
                lock (_listenerLock)
                    return _listeners != null && !_listeners.IsEmpty;
            }
        }

        protected virtual void OnEventListenersChanged() { }

        internal virtual EventTarget GetParentForEventDispatch() => null;
    }

    public delegate void EventListener(Event evt);

    public struct AddEventListenerOptions
    {
        public bool Capture;
        public bool Once;
        public bool Passive;
        public AbortSignal Signal;
    }

    internal sealed class EventListenerStorage
    {
        private Dictionary<string, EventListenerEntry[]> _listeners;
        private int _totalCount;

        public bool IsEmpty => _totalCount == 0;

        public bool Add(
            string type,
            EventListener callback,
            AddEventListenerOptions options,
            EventTarget owner)
        {
            if (options.Signal != null && options.Signal.Aborted)
                return false;

            _listeners ??= new Dictionary<string, EventListenerEntry[]>(StringComparer.Ordinal);
            _listeners.TryGetValue(type, out var current);
            current ??= Array.Empty<EventListenerEntry>();

            for (var i = 0; i < current.Length; i++)
            {
                var entry = current[i];
                if (!entry.Removed &&
                    ReferenceEquals(entry.Callback, callback) &&
                    entry.Capture == options.Capture)
                {
                    return false;
                }
            }

            var newEntry = new EventListenerEntry
            {
                Callback = callback,
                Capture = options.Capture,
                Once = options.Once,
                Passive = options.Passive,
                Signal = options.Signal
            };

            if (options.Signal != null)
            {
                Action abortHandler = () => owner.RemoveEventListener(type, callback, options.Capture);
                newEntry.AbortHandler = abortHandler;
                options.Signal.OnAbort += abortHandler;

                // AbortSignal invokes a just-added listener immediately if it raced an
                // abort. Do not publish an entry that was removed by that callback.
                if (options.Signal.Aborted)
                {
                    options.Signal.OnAbort -= abortHandler;
                    return false;
                }
            }

            var next = new EventListenerEntry[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = newEntry;
            _listeners[type] = next;
            _totalCount++;
            return true;
        }

        public bool Remove(string type, EventListener callback, bool capture)
        {
            if (_listeners == null || !_listeners.TryGetValue(type, out var current))
                return false;

            var removeIndex = -1;
            for (var i = 0; i < current.Length; i++)
            {
                var entry = current[i];
                if (!entry.Removed &&
                    ReferenceEquals(entry.Callback, callback) &&
                    entry.Capture == capture)
                {
                    removeIndex = i;
                    break;
                }
            }

            if (removeIndex < 0)
                return false;

            var removed = current[removeIndex];
            removed.Removed = true;
            if (removed.Signal != null && removed.AbortHandler != null)
                removed.Signal.OnAbort -= removed.AbortHandler;

            if (current.Length == 1)
            {
                _listeners.Remove(type);
            }
            else
            {
                var next = new EventListenerEntry[current.Length - 1];
                if (removeIndex > 0)
                    Array.Copy(current, 0, next, 0, removeIndex);
                if (removeIndex + 1 < current.Length)
                {
                    Array.Copy(
                        current,
                        removeIndex + 1,
                        next,
                        removeIndex,
                        current.Length - removeIndex - 1);
                }
                _listeners[type] = next;
            }

            _totalCount--;
            return true;
        }

        public bool TryGetSnapshot(string type, out EventListenerEntry[] snapshot)
        {
            if (_listeners != null &&
                _listeners.TryGetValue(type, out snapshot) &&
                snapshot.Length > 0)
            {
                return true;
            }

            snapshot = null;
            return false;
        }
    }

    internal sealed class EventListenerEntry
    {
        public EventListener Callback;
        public bool Capture;
        public bool Once;
        public bool Passive;
        public AbortSignal Signal;
        public Action AbortHandler;
        public volatile bool Removed;
    }

    public sealed class AbortSignal
    {
        private volatile bool _aborted;
        private readonly object _lock = new();
        private event Action _onAbort;

        public bool Aborted => _aborted;

        public event Action OnAbort
        {
            add
            {
                bool invokeImmediately;
                lock (_lock)
                {
                    invokeImmediately = _aborted;
                    if (!invokeImmediately)
                        _onAbort += value;
                }

                if (invokeImmediately)
                    value?.Invoke();
            }
            remove
            {
                lock (_lock)
                    _onAbort -= value;
            }
        }

        internal void Abort()
        {
            Action handlers;
            lock (_lock)
            {
                if (_aborted) return;
                _aborted = true;
                handlers = _onAbort;
                _onAbort = null;
            }

            handlers?.Invoke();
        }
    }

    public sealed class AbortController
    {
        public AbortSignal Signal { get; } = new AbortSignal();

        public void Abort()
        {
            Signal.Abort();
        }
    }

    public class Event
    {
        internal bool StopPropagationFlag;
        internal bool StopImmediatePropagationFlag;
        internal bool CanceledFlag;
        internal bool InPassiveListenerFlag;
        internal bool ComposedFlag;
        internal bool Initialized = true;
        internal bool DispatchFlag;

        internal List<EventPathEntry> Path;

        public string Type { get; }
        public EventTarget Target { get; internal set; }
        public EventTarget CurrentTarget { get; internal set; }
        public EventPhase EventPhase { get; internal set; }
        public bool Bubbles { get; }
        public bool Cancelable { get; }
        public bool Composed => ComposedFlag;
        public bool DefaultPrevented => CanceledFlag;
        public bool IsTrusted { get; internal set; }
        public double TimeStamp { get; }

        public Event(string type, EventInit init = default)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Bubbles = init.Bubbles;
            Cancelable = init.Cancelable;
            ComposedFlag = init.Composed;
            TimeStamp = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
        }

        public void StopPropagation()
        {
            StopPropagationFlag = true;
        }

        public void StopImmediatePropagation()
        {
            StopPropagationFlag = true;
            StopImmediatePropagationFlag = true;
        }

        public void PreventDefault()
        {
            if (Cancelable && !InPassiveListenerFlag)
                CanceledFlag = true;
        }

        public EventTarget[] ComposedPath()
        {
            if (Path == null || Path.Count == 0)
                return Array.Empty<EventTarget>();

            // Closed-tree filtering remains intentionally conservative: entries that
            // mark the crossing host are visible, while the closed root's internals
            // are hidden from listeners whose current target is outside that root.
            var result = new List<EventTarget>(Path.Count);
            var currentTarget = CurrentTarget;
            for (var i = 0; i < Path.Count; i++)
            {
                var entry = Path[i];
                if (entry.ClosedTreeRoot != null &&
                    !IsInsideShadowTree(currentTarget, entry.ClosedTreeRoot))
                {
                    continue;
                }

                result.Add(entry.InvocationTarget);
            }

            return result.ToArray();
        }

        public bool ReturnValue
        {
            get => !CanceledFlag;
            set { if (!value) PreventDefault(); }
        }

        public EventTarget SrcElement => Target;

        private static bool IsInsideShadowTree(EventTarget target, ShadowRoot shadowRoot)
        {
            return target is Node node && ReferenceEquals(node.GetRootNode(), shadowRoot);
        }
    }

    public struct EventInit
    {
        public bool Bubbles;
        public bool Cancelable;
        public bool Composed;
    }

    public enum EventPhase : ushort
    {
        None = 0,
        Capturing = 1,
        AtTarget = 2,
        Bubbling = 3
    }

    internal sealed class EventPathEntry
    {
        public EventTarget InvocationTarget;
        public EventTarget ShadowAdjustedTarget;
        public Node RelatedTarget;
        public ShadowRoot ClosedTreeRoot;
    }

    internal static class EventDispatcher
    {
        public static bool Dispatch(Event evt, EventTarget target)
        {
            evt.DispatchFlag = true;
            var originalTarget = target;
            evt.Target = target;

            var path = BuildEventPath(target, evt.ComposedFlag);
            evt.Path = path;

            try
            {
                evt.EventPhase = EventPhase.Capturing;
                for (int i = path.Count - 1; i > 0; i--)
                {
                    if (evt.StopPropagationFlag) break;
                    InvokePathEntry(evt, path[i], EventPhase.Capturing);
                }

                if (!evt.StopPropagationFlag && path.Count > 0)
                {
                    evt.EventPhase = EventPhase.AtTarget;
                    InvokePathEntry(evt, path[0], EventPhase.AtTarget);
                }

                if (evt.Bubbles && !evt.StopPropagationFlag)
                {
                    evt.EventPhase = EventPhase.Bubbling;
                    for (int i = 1; i < path.Count; i++)
                    {
                        if (evt.StopPropagationFlag) break;
                        InvokePathEntry(evt, path[i], EventPhase.Bubbling);
                    }
                }

                return !evt.CanceledFlag;
            }
            finally
            {
                evt.Target = originalTarget;
                evt.StopPropagationFlag = false;
                evt.StopImmediatePropagationFlag = false;
                evt.InPassiveListenerFlag = false;
                evt.DispatchFlag = false;
                evt.EventPhase = EventPhase.None;
                evt.CurrentTarget = null;
                evt.Path = null;
            }
        }

        private static void InvokePathEntry(Event evt, EventPathEntry entry, EventPhase phase)
        {
            evt.Target = entry.ShadowAdjustedTarget;
            evt.CurrentTarget = entry.InvocationTarget;
            InvokeEventListeners(evt, entry.InvocationTarget, phase);
        }

        private static List<EventPathEntry> BuildEventPath(EventTarget target, bool composed)
        {
            var path = new List<EventPathEntry>(8)
            {
                new EventPathEntry
                {
                    InvocationTarget = target,
                    ShadowAdjustedTarget = target
                }
            };

            var current = target;
            var adjustedTarget = target;
            while (true)
            {
                var parent = current.GetParentForEventDispatch();
                if (parent == null)
                    break;

                if (current is ShadowRoot shadowRoot)
                {
                    if (!composed)
                        break;

                    // Retargeting: once traversal leaves a shadow tree, listeners on
                    // the host and its ancestors observe the host as event.target. If
                    // another outer shadow boundary is crossed later, the target is
                    // retargeted again to that outer host.
                    parent = shadowRoot.Host;
                    adjustedTarget = parent;

                    if (shadowRoot.Mode == ShadowRootMode.Closed)
                    {
                        // Entries accumulated so far are inside this closed root. Mark
                        // them so composedPath() can hide them from outside listeners.
                        for (var i = 0; i < path.Count; i++)
                        {
                            if (path[i].ClosedTreeRoot == null)
                                path[i].ClosedTreeRoot = shadowRoot;
                        }
                    }
                }

                path.Add(new EventPathEntry
                {
                    InvocationTarget = parent,
                    ShadowAdjustedTarget = adjustedTarget
                });
                current = parent;
            }

            return path;
        }

        private static void InvokeEventListeners(Event evt, EventTarget target, EventPhase phase)
        {
            if (!target.TryGetEventListeners(evt.Type, out var listeners))
                return;

            for (var listenerIndex = 0; listenerIndex < listeners.Length; listenerIndex++)
            {
                var listener = listeners[listenerIndex];
                if (listener.Removed)
                    continue;

                if (phase == EventPhase.Capturing && !listener.Capture)
                    continue;
                if (phase == EventPhase.Bubbling && listener.Capture)
                    continue;
                if (evt.StopImmediatePropagationFlag)
                    break;

                if (listener.Once)
                    target.RemoveEventListener(evt.Type, listener.Callback, listener.Capture);

                bool wasInPassive = evt.InPassiveListenerFlag;
                if (listener.Passive)
                    evt.InPassiveListenerFlag = true;

                try
                {
                    listener.Callback(evt);
                }
                catch (Exception ex)
                {
                    ReportError(ex, target, evt);
                }
                finally
                {
                    evt.InPassiveListenerFlag = wasInPassive;
                }
            }
        }

        private static void ReportError(Exception ex, EventTarget target, Event evt)
        {
            var type = NormalizeLogField(evt.Type);
            var message = NormalizeLogField(ex.Message);
            EngineLogCompat.Error(
                $"[EventDispatcher] Error in listener for '{type}' on {target}: {message}",
                FenBrowser.Core.Logging.LogCategory.Events);
        }

        private static string NormalizeLogField(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\0", "\\0", StringComparison.Ordinal);
        }
    }
}
