// WHATWG DOM Living Standard implementation
// FenBrowser.Core.Dom.V2

using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard EventTarget base.
    /// Listener mutation is copy-on-write so high-frequency dispatch can consume
    /// immutable snapshots without allocating a List for every target/event.
    /// </summary>
    public abstract class EventTarget
    {
        private EventListenerStorage _listeners;
        private readonly object _listenerLock = new();

        public void AddEventListener(string type, EventListener callback, bool capture = false) =>
            AddEventListener(type, callback, new AddEventListenerOptions { Capture = capture });

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

            bool removed;
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
            ArgumentNullException.ThrowIfNull(evt);
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
                if (_listeners != null)
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
            if (options.Signal?.Aborted == true)
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

            var next = new EventListenerEntry[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = newEntry;

            // Publish before subscribing to AbortSignal. If abort happened just before
            // subscription, the signal's event adder invokes immediately and removal
            // can now see this entry. If abort happens just after publication, the
            // callback likewise removes an already-visible entry. This closes the
            // publish/abort lost-removal window.
            _listeners[type] = next;
            _totalCount++;

            if (options.Signal != null)
            {
                Action abortHandler = () => owner.RemoveEventListener(type, callback, options.Capture);
                newEntry.AbortHandler = abortHandler;
                options.Signal.OnAbort += abortHandler;
            }

            return !newEntry.Removed;
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
                if (_aborted)
                    return;

                _aborted = true;
                handlers = _onAbort;
                _onAbort = null;
            }

            handlers?.Invoke();
        }
    }

    public sealed class AbortController
    {
        public AbortSignal Signal { get; } = new();
        public void Abort() => Signal.Abort();
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

        public void StopPropagation() => StopPropagationFlag = true;

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

            var result = new List<EventTarget>(Path.Count);
            for (var i = 0; i < Path.Count; i++)
            {
                var entry = Path[i];
                if (entry.ClosedTreeRoot != null &&
                    !IsInsideShadowTree(CurrentTarget, entry.ClosedTreeRoot))
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

        private static bool IsInsideShadowTree(EventTarget target, ShadowRoot shadowRoot) =>
            target is Node node && ReferenceEquals(node.GetRootNode(), shadowRoot);
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
                for (var i = path.Count - 1; i > 0; i--)
                {
                    if (evt.StopPropagationFlag)
                        break;
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
                    for (var i = 1; i < path.Count; i++)
                    {
                        if (evt.StopPropagationFlag)
                            break;
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
                new()
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

                    parent = shadowRoot.Host;
                    adjustedTarget = parent;

                    if (shadowRoot.Mode == ShadowRootMode.Closed)
                    {
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

            for (var i = 0; i < listeners.Length; i++)
            {
                var listener = listeners[i];
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

                var wasInPassive = evt.InPassiveListenerFlag;
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
            EngineLogCompat.Error(
                $"[EventDispatcher] Error in listener for '{NormalizeLogField(evt.Type)}' on {target}: {NormalizeLogField(ex.Message)}",
                FenBrowser.Core.Logging.LogCategory.Events);
        }

        private static string NormalizeLogField(string value) =>
            (value ?? string.Empty)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\0", "\\0", StringComparison.Ordinal);
    }
}
