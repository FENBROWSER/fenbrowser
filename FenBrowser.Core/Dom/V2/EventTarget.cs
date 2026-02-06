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
        // Lazy-initialized event listener storage with lock for thread safety
        private EventListenerStorage _listeners;
        private readonly object _listenerLock = new object();

        /// <summary>
        /// Registers an event handler of a specific event type.
        /// https://dom.spec.whatwg.org/#dom-eventtarget-addeventlistener
        /// </summary>
        public void AddEventListener(string type, EventListener callback, bool capture = false)
        {
            AddEventListener(type, callback, new AddEventListenerOptions { Capture = capture });
        }

        /// <summary>
        /// Registers an event handler with options.
        /// Thread-safe implementation.
        /// </summary>
        public void AddEventListener(string type, EventListener callback, AddEventListenerOptions options)
        {
            if (string.IsNullOrEmpty(type) || callback == null)
                return;

            lock (_listenerLock)
            {
                _listeners ??= new EventListenerStorage();
                _listeners.Add(type, callback, options);
            }
            OnEventListenersChanged();
        }

        /// <summary>
        /// Removes an event listener.
        /// https://dom.spec.whatwg.org/#dom-eventtarget-removeeventlistener
        /// Thread-safe implementation.
        /// </summary>
        public void RemoveEventListener(string type, EventListener callback, bool capture = false)
        {
            if (string.IsNullOrEmpty(type) || callback == null)
                return;

            lock (_listenerLock)
            {
                if (_listeners == null)
                    return;

                _listeners.Remove(type, callback, capture);

                if (_listeners.IsEmpty)
                    _listeners = null;
            }
            OnEventListenersChanged();
        }

        /// <summary>
        /// Dispatches an event to this target.
        /// https://dom.spec.whatwg.org/#dom-eventtarget-dispatchevent
        /// Implements full WHATWG event dispatch algorithm with capture/bubble phases.
        /// </summary>
        public bool DispatchEvent(Event evt)
        {
            if (evt == null)
                throw new ArgumentNullException(nameof(evt));
            if (evt.DispatchFlag)
                throw new DomException("InvalidStateError", "Event is already being dispatched");
            if (!evt.Initialized)
                throw new DomException("InvalidStateError", "Event has not been initialized");

            evt.IsTrusted = false; // User-dispatched events are not trusted
            return EventDispatcher.Dispatch(evt, this);
        }

        /// <summary>
        /// Gets event listeners for a specific type (internal use).
        /// Thread-safe copy returned.
        /// </summary>
        internal List<EventListenerEntry> GetEventListeners(string type)
        {
            lock (_listenerLock)
            {
                return _listeners?.GetCopy(type) ?? new List<EventListenerEntry>();
            }
        }

        /// <summary>
        /// Checks if this target has any event listeners.
        /// </summary>
        internal bool HasEventListeners
        {
            get
            {
                lock (_listenerLock)
                {
                    return _listeners != null && !_listeners.IsEmpty;
                }
            }
        }

        /// <summary>
        /// Called when event listeners are added or removed.
        /// Override to update HasEventListeners flag in derived classes.
        /// </summary>
        protected virtual void OnEventListenersChanged() { }

        /// <summary>
        /// Gets the parent for event dispatch (for building event path).
        /// Override in Node to return ParentNode.
        /// </summary>
        internal virtual EventTarget GetParentForEventDispatch() => null;
    }

    /// <summary>
    /// Event listener callback delegate.
    /// </summary>
    public delegate void EventListener(Event evt);

    /// <summary>
    /// Options for addEventListener.
    /// https://dom.spec.whatwg.org/#dictdef-addeventlisteneroptions
    /// </summary>
    public struct AddEventListenerOptions
    {
        public bool Capture;
        public bool Once;
        public bool Passive;
        public AbortSignal Signal;
    }

    /// <summary>
    /// Internal storage for event listeners.
    /// Thread-safe operations via external lock in EventTarget.
    /// </summary>
    internal sealed class EventListenerStorage
    {
        private Dictionary<string, List<EventListenerEntry>> _listeners;
        private int _totalCount;

        public bool IsEmpty => _totalCount == 0;

        public void Add(string type, EventListener callback, AddEventListenerOptions options)
        {
            _listeners ??= new Dictionary<string, List<EventListenerEntry>>(StringComparer.Ordinal);

            if (!_listeners.TryGetValue(type, out var list))
            {
                list = new List<EventListenerEntry>(2);
                _listeners[type] = list;
            }

            // Check for duplicate (same callback + same capture phase)
            foreach (var entry in list)
            {
                if (ReferenceEquals(entry.Callback, callback) && entry.Capture == options.Capture)
                    return; // Already registered, per spec
            }

            var newEntry = new EventListenerEntry
            {
                Callback = callback,
                Capture = options.Capture,
                Once = options.Once,
                Passive = options.Passive,
                Signal = options.Signal
            };

            // Register abort signal listener if provided
            if (options.Signal != null && !options.Signal.Aborted)
            {
                options.Signal.OnAbort += () => Remove(type, callback, options.Capture);
            }

            list.Add(newEntry);
            _totalCount++;
        }

        public void Remove(string type, EventListener callback, bool capture)
        {
            if (_listeners == null || !_listeners.TryGetValue(type, out var list))
                return;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var entry = list[i];
                if (ReferenceEquals(entry.Callback, callback) && entry.Capture == capture)
                {
                    // Mark as removed for iteration safety, then remove
                    entry.Removed = true;
                    list.RemoveAt(i);
                    _totalCount--;

                    if (list.Count == 0)
                        _listeners.Remove(type);
                    return;
                }
            }
        }

        /// <summary>
        /// Returns a copy of listeners for thread-safe iteration during dispatch.
        /// </summary>
        public List<EventListenerEntry> GetCopy(string type)
        {
            if (_listeners != null && _listeners.TryGetValue(type, out var list))
            {
                // Return copy to allow safe iteration even if listeners modified
                var copy = new List<EventListenerEntry>(list.Count);
                foreach (var entry in list)
                {
                    if (!entry.Removed)
                        copy.Add(entry);
                }
                return copy;
            }
            return new List<EventListenerEntry>();
        }
    }

    /// <summary>
    /// Internal event listener entry.
    /// </summary>
    internal sealed class EventListenerEntry
    {
        public EventListener Callback;
        public bool Capture;
        public bool Once;
        public bool Passive;
        public AbortSignal Signal;
        public volatile bool Removed; // Volatile for thread visibility
    }

    /// <summary>
    /// Abort signal for cancellable operations.
    /// https://dom.spec.whatwg.org/#interface-abortsignal
    /// Thread-safe implementation.
    /// </summary>
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
                lock (_lock)
                {
                    if (_aborted)
                    {
                        // Already aborted, invoke immediately
                        value?.Invoke();
                    }
                    else
                    {
                        _onAbort += value;
                    }
                }
            }
            remove
            {
                lock (_lock)
                {
                    _onAbort -= value;
                }
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

            // Invoke handlers outside lock to prevent deadlocks
            handlers?.Invoke();
        }
    }

    /// <summary>
    /// AbortController for creating abort signals.
    /// https://dom.spec.whatwg.org/#interface-abortcontroller
    /// </summary>
    public sealed class AbortController
    {
        public AbortSignal Signal { get; } = new AbortSignal();

        public void Abort()
        {
            Signal.Abort();
        }
    }

    /// <summary>
    /// DOM Event class with full WHATWG compliance.
    /// https://dom.spec.whatwg.org/#interface-event
    /// </summary>
    public class Event
    {
        // Internal state flags
        internal bool StopPropagationFlag;
        internal bool StopImmediatePropagationFlag;
        internal bool CanceledFlag;
        internal bool InPassiveListenerFlag;
        internal bool ComposedFlag;
        internal bool Initialized = true;
        internal bool DispatchFlag;

        // Event path for composedPath()
        internal List<EventPathEntry> Path;

        /// <summary>Event type name.</summary>
        public string Type { get; }

        /// <summary>Target of the event.</summary>
        public EventTarget Target { get; internal set; }

        /// <summary>Current target during dispatch.</summary>
        public EventTarget CurrentTarget { get; internal set; }

        /// <summary>Current event phase.</summary>
        public EventPhase EventPhase { get; internal set; }

        /// <summary>Whether event bubbles up through the DOM.</summary>
        public bool Bubbles { get; }

        /// <summary>Whether event can be canceled.</summary>
        public bool Cancelable { get; }

        /// <summary>Whether event can cross shadow DOM boundary.</summary>
        public bool Composed => ComposedFlag;

        /// <summary>Whether default was prevented.</summary>
        public bool DefaultPrevented => CanceledFlag;

        /// <summary>Whether event was dispatched by user agent.</summary>
        public bool IsTrusted { get; internal set; }

        /// <summary>Event creation timestamp.</summary>
        public double TimeStamp { get; }

        /// <summary>
        /// Creates a new Event.
        /// </summary>
        public Event(string type, EventInit init = default)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Bubbles = init.Bubbles;
            Cancelable = init.Cancelable;
            ComposedFlag = init.Composed;
            TimeStamp = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
        }

        /// <summary>
        /// Stops event from reaching other listeners.
        /// https://dom.spec.whatwg.org/#dom-event-stoppropagation
        /// </summary>
        public void StopPropagation()
        {
            StopPropagationFlag = true;
        }

        /// <summary>
        /// Stops event immediately, including other listeners on same target.
        /// https://dom.spec.whatwg.org/#dom-event-stopimmediatepropagation
        /// </summary>
        public void StopImmediatePropagation()
        {
            StopPropagationFlag = true;
            StopImmediatePropagationFlag = true;
        }

        /// <summary>
        /// Prevents default action if event is cancelable.
        /// https://dom.spec.whatwg.org/#dom-event-preventdefault
        /// </summary>
        public void PreventDefault()
        {
            if (Cancelable && !InPassiveListenerFlag)
                CanceledFlag = true;
        }

        /// <summary>
        /// Returns the event's path through the DOM.
        /// https://dom.spec.whatwg.org/#dom-event-composedpath
        /// </summary>
        public EventTarget[] ComposedPath()
        {
            if (Path == null || Path.Count == 0)
                return Array.Empty<EventTarget>();

            var result = new List<EventTarget>();
            var currentTarget = CurrentTarget;

            // Build composed path considering shadow DOM
            foreach (var entry in Path)
            {
                // Filter based on shadow DOM encapsulation
                if (entry.RootOfClosedTree && entry.InvocationTarget != currentTarget)
                    continue;

                result.Add(entry.InvocationTarget);
            }

            return result.ToArray();
        }

        // Legacy aliases
        public bool ReturnValue
        {
            get => !CanceledFlag;
            set { if (!value) PreventDefault(); }
        }

        public EventTarget SrcElement => Target;
    }

    /// <summary>
    /// Event initialization options.
    /// </summary>
    public struct EventInit
    {
        public bool Bubbles;
        public bool Cancelable;
        public bool Composed;
    }

    /// <summary>
    /// Event dispatch phase.
    /// </summary>
    public enum EventPhase : ushort
    {
        None = 0,
        Capturing = 1,
        AtTarget = 2,
        Bubbling = 3
    }

    /// <summary>
    /// Entry in event path for composed path tracking.
    /// </summary>
    internal sealed class EventPathEntry
    {
        public EventTarget InvocationTarget;
        public EventTarget ShadowAdjustedTarget;
        public Node RelatedTarget;
        public bool RootOfClosedTree;
        public bool SlotInClosedTree;
    }

    /// <summary>
    /// Full WHATWG-compliant event dispatcher.
    /// https://dom.spec.whatwg.org/#concept-event-dispatch
    /// </summary>
    internal static class EventDispatcher
    {
        /// <summary>
        /// Dispatches an event following the WHATWG DOM spec algorithm.
        /// Implements capture phase, target phase, and bubble phase.
        /// </summary>
        public static bool Dispatch(Event evt, EventTarget target)
        {
            evt.DispatchFlag = true;
            evt.Target = target;

            // Build event path
            var path = BuildEventPath(target, evt.ComposedFlag);
            evt.Path = path;

            try
            {
                // 1. CAPTURE PHASE - from window/document down to target's parent
                evt.EventPhase = EventPhase.Capturing;

                for (int i = path.Count - 1; i > 0; i--)
                {
                    if (evt.StopPropagationFlag)
                        break;

                    var entry = path[i];
                    evt.CurrentTarget = entry.InvocationTarget;
                    InvokeEventListeners(evt, entry.InvocationTarget, EventPhase.Capturing);
                }

                // 2. TARGET PHASE
                if (!evt.StopPropagationFlag && path.Count > 0)
                {
                    evt.EventPhase = EventPhase.AtTarget;
                    var targetEntry = path[0];
                    evt.CurrentTarget = targetEntry.InvocationTarget;

                    // At target, invoke both capture and bubble listeners
                    InvokeEventListeners(evt, targetEntry.InvocationTarget, EventPhase.AtTarget);
                }

                // 3. BUBBLE PHASE - from target's parent up to window/document
                if (evt.Bubbles && !evt.StopPropagationFlag)
                {
                    evt.EventPhase = EventPhase.Bubbling;

                    for (int i = 1; i < path.Count; i++)
                    {
                        if (evt.StopPropagationFlag)
                            break;

                        var entry = path[i];
                        evt.CurrentTarget = entry.InvocationTarget;
                        InvokeEventListeners(evt, entry.InvocationTarget, EventPhase.Bubbling);
                    }
                }

                return !evt.CanceledFlag;
            }
            finally
            {
                // Cleanup
                evt.DispatchFlag = false;
                evt.EventPhase = EventPhase.None;
                evt.CurrentTarget = null;
                evt.Path = null;
            }
        }

        /// <summary>
        /// Builds the event propagation path from target up through ancestors.
        /// </summary>
        private static List<EventPathEntry> BuildEventPath(EventTarget target, bool composed)
        {
            var path = new List<EventPathEntry>();

            // Start with target
            path.Add(new EventPathEntry
            {
                InvocationTarget = target,
                ShadowAdjustedTarget = target
            });

            // Walk up through ancestors
            var current = target;
            while (true)
            {
                var parent = current.GetParentForEventDispatch();
                if (parent == null)
                    break;

                // Handle shadow DOM boundary
                if (current is ShadowRoot shadowRoot)
                {
                    if (!composed && shadowRoot.Mode == ShadowRootMode.Closed)
                    {
                        // Stop at closed shadow root boundary
                        break;
                    }

                    // Shadow root's host becomes next in path
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

        /// <summary>
        /// Invokes event listeners on a target for the specified phase.
        /// </summary>
        private static void InvokeEventListeners(Event evt, EventTarget target, EventPhase phase)
        {
            var listeners = target.GetEventListeners(evt.Type);
            if (listeners.Count == 0)
                return;

            foreach (var listener in listeners)
            {
                if (listener.Removed)
                    continue;

                // Check phase matching
                if (phase == EventPhase.Capturing && !listener.Capture)
                    continue;
                if (phase == EventPhase.Bubbling && listener.Capture)
                    continue;
                // At target: invoke both capture and bubble listeners

                if (evt.StopImmediatePropagationFlag)
                    break;

                // Handle passive listeners
                bool wasInPassive = evt.InPassiveListenerFlag;
                if (listener.Passive)
                    evt.InPassiveListenerFlag = true;

                try
                {
                    listener.Callback(evt);
                }
                catch (Exception ex)
                {
                    // Per spec: report error but continue dispatch
                    ReportError(ex, target, evt);
                }
                finally
                {
                    evt.InPassiveListenerFlag = wasInPassive;
                }

                // Handle once option
                if (listener.Once)
                    listener.Removed = true;
            }
        }

        /// <summary>
        /// Reports an error that occurred during event dispatch.
        /// </summary>
        private static void ReportError(Exception ex, EventTarget target, Event evt)
        {
            // TODO: Integrate with window.onerror or console
            System.Diagnostics.Debug.WriteLine(
                $"[EventDispatcher] Error in listener for '{evt.Type}' on {target}: {ex.Message}");
        }
    }
}
