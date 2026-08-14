// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections.Generic;
using System.Threading;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: EventTarget interface.
    /// https://dom.spec.whatwg.org/#interface-eventtarget
    ///
    /// Base class for all objects that can receive events.
    /// Uses lazy initialization and compact storage for memory efficiency.
    /// Thread-safe implementation.
    /// </summary>
    public abstract class EventTarget
    {
        private EventListenerStorage _listeners;
        private readonly object _listenerLock = new object();

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

        internal List<EventListenerEntry> GetEventListeners(string type)
        {
            return TryGetEventListeners(type, out var listeners)
                ? listeners
                : new List<EventListenerEntry>();
        }

        internal bool TryGetEventListeners(string type, out List<EventListenerEntry> listeners)
        {
            lock (_listenerLock)
            {
                if (_listeners is not null)
                    return _listeners.TryGetCopy(type, out listeners);

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
        private Dictionary<string, List<EventListenerEntry>> _listeners;
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

            _listeners ??= new Dictionary<string, List<EventListenerEntry>>(StringComparer.Ordinal);

            if (!_listeners.TryGetValue(type, out var list))
            {
                list = new List<EventListenerEntry>(2);
                _listeners[type] = list;
            }

            foreach (var entry in list)
            {
                if (ReferenceEquals(entry.Callback, callback) && entry.Capture == options.Capture)
                    return false;
            }

            var newEntry = new EventListenerEntry
            {
                Callback = callback,
                Capture = options.Capture,
                Once = options.Once,
                Passive = options.Passive,
                Signal = options.Signal
            };

            list.Add(newEntry);
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
            if (_listeners == null || !_listeners.TryGetValue(type, out var list))
                return false;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var entry = list[i];
                if (ReferenceEquals(entry.Callback, callback) && entry.Capture == capture)
                {
                    entry.Removed = true;
                    list.RemoveAt(i);
                    _totalCount--;

                    if (entry.Signal != null && entry.AbortHandler != null)
                        entry.Signal.OnAbort -= entry.AbortHandler;

                    if (list.Count == 0)
                        _listeners.Remove(type);
                    return true;
                }
            }

            return false;
        }

        public bool TryGetCopy(string type, out List<EventListenerEntry> copy)
        {
            if (_listeners != null && _listeners.TryGetValue(type, out var list))
            {
                copy = new List<EventListenerEntry>(list.Count);
                foreach (var entry in list)
                {
                    if (!entry.Removed)
                        copy.Add(entry);
                }
                return copy.Count > 0;
            }

            copy = null;
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
        private readonly object _lock = new object();
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

            var result = new List<EventTarget>();
            var currentTarget = CurrentTarget;
            foreach (var entry in Path)
            {
                if (entry.RootOfClosedTree && entry.InvocationTarget != currentTarget)
                    continue;

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
        public bool RootOfClosedTree;
        public bool SlotInClosedTree;
    }

    internal static class EventDispatcher
    {
        public static bool Dispatch(Event evt, EventTarget target)
        {
            evt.DispatchFlag = true;
            evt.Target = target;

            var path = BuildEventPath(target, evt.ComposedFlag);
            evt.Path = path;

            try
            {
                evt.EventPhase = EventPhase.Capturing;
                for (int i = path.Count - 1; i > 0; i--)
                {
                    if (evt.StopPropagationFlag) break;

                    var entry = path[i];
                    evt.CurrentTarget = entry.InvocationTarget;
                    InvokeEventListeners(evt, entry.InvocationTarget, EventPhase.Capturing);
                }

                if (!evt.StopPropagationFlag && path.Count > 0)
                {
                    evt.EventPhase = EventPhase.AtTarget;
                    var targetEntry = path[0];
                    evt.CurrentTarget = targetEntry.InvocationTarget;
                    InvokeEventListeners(evt, targetEntry.InvocationTarget, EventPhase.AtTarget);
                }

                if (evt.Bubbles && !evt.StopPropagationFlag)
                {
                    evt.EventPhase = EventPhase.Bubbling;
                    for (int i = 1; i < path.Count; i++)
                    {
                        if (evt.StopPropagationFlag) break;

                        var entry = path[i];
                        evt.CurrentTarget = entry.InvocationTarget;
                        InvokeEventListeners(evt, entry.InvocationTarget, EventPhase.Bubbling);
                    }
                }

                return !evt.CanceledFlag;
            }
            finally
            {
                evt.StopPropagationFlag = false;
                evt.StopImmediatePropagationFlag = false;
                evt.InPassiveListenerFlag = false;
                evt.DispatchFlag = false;
                evt.EventPhase = EventPhase.None;
                evt.CurrentTarget = null;
                evt.Path = null;
            }
        }

        private static List<EventPathEntry> BuildEventPath(EventTarget target, bool composed)
        {
            var path = new List<EventPathEntry>
            {
                new EventPathEntry
                {
                    InvocationTarget = target,
                    ShadowAdjustedTarget = target
                }
            };

            var current = target;
            while (true)
            {
                var parent = current.GetParentForEventDispatch();
                if (parent == null)
                    break;

                if (current is ShadowRoot shadowRoot)
                {
                    // The `composed` flag controls whether an event crosses a shadow
                    // boundary at all. Open/closed mode controls encapsulation and
                    // composedPath/retargeting, not propagation for composed:false.
                    if (!composed)
                        break;

                    parent = shadowRoot.Host;
                    path.Add(new EventPathEntry
                    {
                        InvocationTarget = parent,
                        ShadowAdjustedTarget = parent,
                        RootOfClosedTree = shadowRoot.Mode == ShadowRootMode.Closed
                    });
                }
                else
                {
                    path.Add(new EventPathEntry
                    {
                        InvocationTarget = parent,
                        ShadowAdjustedTarget = parent
                    });
                }

                current = parent;
            }

            return path;
        }

        private static void InvokeEventListeners(Event evt, EventTarget target, EventPhase phase)
        {
            if (!target.TryGetEventListeners(evt.Type, out var listeners))
                return;

            for (var listenerIndex = 0; listenerIndex < listeners.Count; listenerIndex++)
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
            EngineLogCompat.Error(
                $"[EventDispatcher] Error in listener for '{evt.Type}' on {target}: {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.Events);
        }
    }
}
