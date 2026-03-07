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
        private readonly Dictionary<Node, Dictionary<string, List<EventListener>>> _listeners
            = new Dictionary<Node, Dictionary<string, List<EventListener>>>();

        private readonly object _lock = new object();

        /// <summary>
        /// Add an event listener to an element
        /// </summary>
        public void Add(Node node, string type, FenValue callback, bool capture = false, bool once = false, bool passive = false)
        {
            if (node == null || type == null || callback.IsUndefined || callback.IsNull)
                return;

            lock (_lock)
            {
                if (!_listeners.ContainsKey(node))
                    _listeners[node] = new Dictionary<string, List<EventListener>>(StringComparer.OrdinalIgnoreCase);

                var byType = _listeners[node];
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
                FenLogger.Debug($"[EventListenerRegistry] Added listener for '{type}' on {DescribeNode(node)} (capture={capture}, once={once})", LogCategory.Events);
            }
        }

        /// <summary>
        /// Remove an event listener from an element
        /// </summary>
        public void Remove(Node node, string type, FenValue callback, bool capture = false)
        {
            if (node == null || type == null || callback.IsUndefined || callback.IsNull)
                return;

            lock (_lock)
            {
                if (!_listeners.ContainsKey(node))
                    return;

                var byType = _listeners[node];
                if (!byType.ContainsKey(type))
                    return;

                var list = byType[type];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Matches(callback, capture))
                    {
                        list.RemoveAt(i);
                        FenLogger.Debug($"[EventListenerRegistry] Removed listener for '{type}' on {DescribeNode(node)}", LogCategory.Events);
                        break;
                    }
                }

                // Cleanup empty collections
                if (list.Count == 0)
                    byType.Remove(type);
                if (byType.Count == 0)
                    _listeners.Remove(node);
            }
        }

        /// <summary>
        /// Get all listeners for an element and event type
        /// </summary>
        /// <param name="element">Target element</param>
        /// <param name="type">Event type</param>
        /// <param name="capture">If true, return capture-phase listeners; if false, bubble-phase</param>
        /// <returns>List of matching listeners (copy to allow modification during iteration)</returns>
        public List<EventListener> Get(Node node, string type, bool capture)
        {
            var result = new List<EventListener>();

            if (node  == null || type == null)
                return result;

            lock (_lock)
            {
                if (!_listeners.ContainsKey(node))
                    return result;

                var byType = _listeners[node];
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
        public List<EventListener> GetAll(Node node, string type)
        {
            var result = new List<EventListener>();

            if (node  == null || type == null)
                return result;

            lock (_lock)
            {
                if (!_listeners.ContainsKey(node))
                    return result;

                var byType = _listeners[node];
                if (!byType.ContainsKey(type))
                    return result;

                result.AddRange(byType[type]);
            }

            return result;
        }

        /// <summary>
        /// Remove a listener that was marked with 'once' option
        /// </summary>
        public void RemoveOnce(Node node, string type, EventListener listener)
        {
            if (node  == null || type == null || listener  == null)
                return;

            lock (_lock)
            {
                if (!_listeners.ContainsKey(node))
                    return;

                var byType = _listeners[node];
                if (!byType.ContainsKey(type))
                    return;

                byType[type].Remove(listener);
                FenLogger.Debug($"[EventListenerRegistry] Removed 'once' listener for '{type}' on {DescribeNode(node)}", LogCategory.Events);
            }
        }

        /// <summary>
        /// Clear all listeners for an element (e.g., when element is removed from DOM)
        /// </summary>
        public void ClearElement(Node node)
        {
            if (node  == null) return;

            lock (_lock)
            {
                _listeners.Remove(node);
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

        private static string DescribeNode(Node node)
        {
            if (node is Element element)
            {
                return $"<{element.TagName}>";
            }

            return node.NodeName ?? node.GetType().Name;
        }
    }
}




