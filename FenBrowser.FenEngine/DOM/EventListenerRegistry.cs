using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Represents a single event listener with its options
    /// </summary>
    public class EventListener
    {
        /// <summary>
        /// The callback function to invoke
        /// </summary>
        public FenValue Callback { get; set; }

        /// <summary>
        /// If true, listener is invoked during capture phase
        /// </summary>
        public bool Capture { get; set; }

        /// <summary>
        /// If true, listener is automatically removed after first invocation
        /// </summary>
        public bool Once { get; set; }

        /// <summary>
        /// If true, indicates that the listener will never call preventDefault()
        /// </summary>
        public bool Passive { get; set; }

        /// <summary>
        /// AbortSignal to remove listener (not implemented yet)
        /// </summary>
        public object Signal { get; set; }

        public EventListener(FenValue callback, bool capture = false, bool once = false, bool passive = false)
        {
            Callback = callback;
            Capture = capture;
            Once = once;
            Passive = passive;
        }

        /// <summary>
        /// Check if two listeners are the same (for removal purposes)
        /// Same callback + same capture flag = same listener
        /// </summary>
        public bool Matches(FenValue callback, bool capture)
        {
            // Per spec: listeners are matched by callback reference and capture flag
            return Capture == capture && Callback.Equals(callback);
        }
    }

    /// <summary>
    /// Centralized registry for all DOM event listeners.
    /// Manages addEventListener/removeEventListener operations.
    /// </summary>
    public class EventListenerRegistry
    {
        // Element → EventType → List<EventListener>
        private readonly Dictionary<Element, Dictionary<string, List<EventListener>>> _listeners
            = new Dictionary<Element, Dictionary<string, List<EventListener>>>();

        private readonly object _lock = new object();

        /// <summary>
        /// Add an event listener to an element
        /// </summary>
        public void Add(Element element, string type, FenValue callback, bool capture = false, bool once = false, bool passive = false)
        {
            if (element  == null || string.IsNullOrEmpty(type) || callback  == null)
                return;

            lock (_lock)
            {
                if (!_listeners.ContainsKey(element))
                    _listeners[element] = new Dictionary<string, List<EventListener>>(StringComparer.OrdinalIgnoreCase);

                var byType = _listeners[element];
                if (!byType.ContainsKey(type))
                    byType[type] = new List<EventListener>();

                var list = byType[type];

                // Check for duplicate (same callback + same capture)
                foreach (var existing in list)
                {
                    if (existing.Matches(callback, capture))
                    {
                        FenLogger.Debug($"[EventListenerRegistry] Duplicate listener ignored for '{type}'", LogCategory.Events);
                        return; // Already registered
                    }
                }

                list.Add(new EventListener(callback, capture, once, passive));
                FenLogger.Debug($"[EventListenerRegistry] Added listener for '{type}' on <{element.TagName}> (capture={capture}, once={once})", LogCategory.Events);
            }
        }

        /// <summary>
        /// Remove an event listener from an element
        /// </summary>
        public void Remove(Element element, string type, FenValue callback, bool capture = false)
        {
            if (element  == null || string.IsNullOrEmpty(type) || callback  == null)
                return;

            lock (_lock)
            {
                if (!_listeners.ContainsKey(element))
                    return;

                var byType = _listeners[element];
                if (!byType.ContainsKey(type))
                    return;

                var list = byType[type];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Matches(callback, capture))
                    {
                        list.RemoveAt(i);
                        FenLogger.Debug($"[EventListenerRegistry] Removed listener for '{type}' on <{element.TagName}>", LogCategory.Events);
                        break;
                    }
                }

                // Cleanup empty collections
                if (list.Count == 0)
                    byType.Remove(type);
                if (byType.Count == 0)
                    _listeners.Remove(element);
            }
        }

        /// <summary>
        /// Get all listeners for an element and event type
        /// </summary>
        /// <param name="element">Target element</param>
        /// <param name="type">Event type</param>
        /// <param name="capture">If true, return capture-phase listeners; if false, bubble-phase</param>
        /// <returns>List of matching listeners (copy to allow modification during iteration)</returns>
        public List<EventListener> Get(Element element, string type, bool capture)
        {
            var result = new List<EventListener>();

            if (element  == null || string.IsNullOrEmpty(type))
                return result;

            lock (_lock)
            {
                if (!_listeners.ContainsKey(element))
                    return result;

                var byType = _listeners[element];
                if (!byType.ContainsKey(type))
                    return result;

                foreach (var listener in byType[type])
                {
                    if (listener.Capture == capture)
                        result.Add(listener);
                }
            }

            return result;
        }

        /// <summary>
        /// Get all listeners for an element and event type (both phases)
        /// </summary>
        public List<EventListener> GetAll(Element element, string type)
        {
            var result = new List<EventListener>();

            if (element  == null || string.IsNullOrEmpty(type))
                return result;

            lock (_lock)
            {
                if (!_listeners.ContainsKey(element))
                    return result;

                var byType = _listeners[element];
                if (!byType.ContainsKey(type))
                    return result;

                result.AddRange(byType[type]);
            }

            return result;
        }

        /// <summary>
        /// Remove a listener that was marked with 'once' option
        /// </summary>
        public void RemoveOnce(Element element, string type, EventListener listener)
        {
            if (element  == null || string.IsNullOrEmpty(type) || listener  == null)
                return;

            lock (_lock)
            {
                if (!_listeners.ContainsKey(element))
                    return;

                var byType = _listeners[element];
                if (!byType.ContainsKey(type))
                    return;

                byType[type].Remove(listener);
                FenLogger.Debug($"[EventListenerRegistry] Removed 'once' listener for '{type}' on <{element.TagName}>", LogCategory.Events);
            }
        }

        /// <summary>
        /// Clear all listeners for an element (e.g., when element is removed from DOM)
        /// </summary>
        public void ClearElement(Element element)
        {
            if (element  == null) return;

            lock (_lock)
            {
                _listeners.Remove(element);
            }
        }

        /// <summary>
        /// Clear all listeners (e.g., on page navigation)
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _listeners.Clear();
            }
        }
    }
}

