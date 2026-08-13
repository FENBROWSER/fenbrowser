// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2.Selectors - Selector Engine

using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2.Selectors
{
    /// <summary>
    /// High-performance selector matching engine.
    /// Uses compiled selectors with bloom filter optimization.
    /// </summary>
    public static class SelectorEngine
    {
        // Thread-local selector cache (LRU-like)
        [ThreadStatic]
        private static Dictionary<string, CompiledSelector> _cache;
        private const int MaxCacheSize = 256;

        /// <summary>
        /// Returns the first element matching the selector.
        /// </summary>
        public static Element QueryFirst(Node root, string selectors)
        {
            if (root == null) return null;

            var compiled = Compile(selectors);
            return QueryFirstInternal(root, compiled);
        }

        /// <summary>
        /// Returns all elements matching the selector.
        /// </summary>
        public static NodeList QueryAll(Node root, string selectors)
        {
            if (root == null) return EmptyNodeList.Instance;

            var compiled = Compile(selectors);
            var results = new List<Node>();
            QueryAllInternal(root, compiled, results);
            return new StaticNodeList(results);
        }

        /// <summary>
        /// Returns true if the element matches the selector.
        /// </summary>
        public static bool Matches(Element element, string selectors)
        {
            if (element == null) return false;

            var compiled = Compile(selectors);
            return compiled.Matches(element);
        }

        /// <summary>
        /// Returns the closest ancestor (or self) matching the selector.
        /// </summary>
        public static Element Closest(Element element, string selectors)
        {
            if (element == null) return null;

            var compiled = Compile(selectors);
            for (var el = element; el != null; el = el.ParentElement)
            {
                if (compiled.Matches(el))
                    return el;
            }
            return null;
        }

        /// <summary>
        /// Compiles a selector string (with caching).
        /// </summary>
        public static CompiledSelector Compile(string selectors)
        {
            ArgumentNullException.ThrowIfNull(selectors);
            _cache ??= new Dictionary<string, CompiledSelector>(StringComparer.Ordinal);

            if (_cache.TryGetValue(selectors, out var cached))
                return cached;

            var compiled = SelectorParser.Parse(selectors);

            // LRU-like eviction
            if (_cache.Count >= MaxCacheSize)
                _cache.Clear();

            _cache[selectors] = compiled;
            return compiled;
        }

        /// <summary>
        /// Clears the selector cache for this thread.
        /// </summary>
        public static void ClearCache()
        {
            _cache?.Clear();
        }

        // --- Internal Query Implementation ---

        private static Element QueryFirstInternal(Node root, CompiledSelector selector)
        {
            // Pre-order depth-first traversal in document tree order. Push siblings
            // from last to first so the stack pops FirstChild first. Using the DOM's
            // existing sibling links avoids allocating a temporary List<Node> for
            // every container visited by querySelector.
            var stack = new Stack<Node>();
            PushChildrenReverse(root, stack);

            while (stack.Count > 0)
            {
                var node = stack.Pop();

                if (node is Element el)
                {
                    // Fast-path bloom filter information is intentionally not used to
                    // prune descendants here: a selector that cannot match this element
                    // can still match one of its descendants.
                    if (selector.Matches(el))
                        return el;
                }

                PushChildrenReverse(node, stack);
            }

            return null;
        }

        private static void QueryAllInternal(Node root, CompiledSelector selector, List<Node> results)
        {
            var stack = new Stack<Node>();
            PushChildrenReverse(root, stack);

            while (stack.Count > 0)
            {
                var node = stack.Pop();

                if (node is Element el && selector.Matches(el))
                    results.Add(el);

                PushChildrenReverse(node, stack);
            }
        }

        private static void PushChildrenReverse(Node node, Stack<Node> stack)
        {
            if (node is not ContainerNode container)
                return;

            // Last -> first push yields first -> last processing with LIFO stack.
            for (var child = container.LastChild; child != null; child = child.PreviousSibling)
                stack.Push(child);
        }
    }

    /// <summary>
    /// Extension methods for easy selector API on elements.
    /// </summary>
    public static class SelectorExtensions
    {
        /// <summary>
        /// Returns the first descendant element matching the selector.
        /// </summary>
        public static Element QuerySelector(this ContainerNode node, string selectors)
        {
            return SelectorEngine.QueryFirst(node, selectors);
        }

        /// <summary>
        /// Returns all descendant elements matching the selector.
        /// </summary>
        public static NodeList QuerySelectorAll(this ContainerNode node, string selectors)
        {
            return SelectorEngine.QueryAll(node, selectors);
        }

        /// <summary>
        /// Returns true if this element matches the selector.
        /// </summary>
        public static bool Matches(this Element element, string selectors)
        {
            return SelectorEngine.Matches(element, selectors);
        }

        /// <summary>
        /// Returns the closest ancestor (or self) matching the selector.
        /// </summary>
        public static Element Closest(this Element element, string selectors)
        {
            return SelectorEngine.Closest(element, selectors);
        }
    }
}