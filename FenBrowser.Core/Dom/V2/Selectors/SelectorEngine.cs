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
            // Breadth-first traversal with bloom filter optimization
            var stack = new Stack<Node>();

            // Push children in reverse order
            if (root is ContainerNode container)
            {
                var children = new List<Node>();
                for (var child = container.FirstChild; child != null; child = child.NextSibling)
                    children.Add(child);

                for (int i = children.Count - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }

            while (stack.Count > 0)
            {
                var node = stack.Pop();

                if (node is Element el)
                {
                    // Fast-path: bloom filter rejection
                    if (!selector.MayMatch(el.AncestorFilter))
                    {
                        // Skip this subtree only if combinator requires ancestors
                        // For now, still check children
                    }

                    if (selector.Matches(el))
                        return el;
                }

                // Push children
                if (node is ContainerNode containerNode)
                {
                    var children = new List<Node>();
                    for (var child = containerNode.FirstChild; child != null; child = child.NextSibling)
                        children.Add(child);

                    for (int i = children.Count - 1; i >= 0; i--)
                        stack.Push(children[i]);
                }
            }

            return null;
        }

        private static void QueryAllInternal(Node root, CompiledSelector selector, List<Node> results)
        {
            // Depth-first traversal
            var stack = new Stack<Node>();

            if (root is ContainerNode container)
            {
                var children = new List<Node>();
                for (var child = container.FirstChild; child != null; child = child.NextSibling)
                    children.Add(child);

                for (int i = children.Count - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }

            while (stack.Count > 0)
            {
                var node = stack.Pop();

                if (node is Element el)
                {
                    if (selector.Matches(el))
                        results.Add(el);
                }

                // Push children
                if (node is ContainerNode containerNode)
                {
                    var children = new List<Node>();
                    for (var child = containerNode.FirstChild; child != null; child = child.NextSibling)
                        children.Add(child);

                    for (int i = children.Count - 1; i >= 0; i--)
                        stack.Push(children[i]);
                }
            }
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
