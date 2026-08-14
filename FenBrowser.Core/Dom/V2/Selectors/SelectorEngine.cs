// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2.Selectors - Selector Engine

using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2.Selectors
{
    /// <summary>
    /// High-performance selector matching engine.
    /// Uses compiled selectors with a thread-local bounded LRU cache.
    /// </summary>
    public static class SelectorEngine
    {
        private const int MaxCacheSize = 256;

        [ThreadStatic]
        private static SelectorCacheState _cache;

        private sealed class SelectorCacheState
        {
            public readonly Dictionary<string, LinkedListNode<SelectorCacheEntry>> Entries =
                new(StringComparer.Ordinal);
            public readonly LinkedList<SelectorCacheEntry> Recency = new();
        }

        private sealed class SelectorCacheEntry
        {
            public SelectorCacheEntry(string key, CompiledSelector selector)
            {
                Key = key;
                Selector = selector;
            }

            public string Key { get; }
            public CompiledSelector Selector { get; }
        }

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
        /// Compiles a selector string with a bounded per-thread LRU cache.
        /// </summary>
        public static CompiledSelector Compile(string selectors)
        {
            ArgumentNullException.ThrowIfNull(selectors);
            var cache = _cache ??= new SelectorCacheState();

            if (cache.Entries.TryGetValue(selectors, out var cachedNode))
            {
                if (!ReferenceEquals(cache.Recency.First, cachedNode))
                {
                    cache.Recency.Remove(cachedNode);
                    cache.Recency.AddFirst(cachedNode);
                }
                return cachedNode.Value.Selector;
            }

            // Parse outside any cache mutation. The cache is thread-local, so there is
            // no synchronization to gain by interleaving parse and bookkeeping.
            var compiled = SelectorParser.Parse(selectors);

            // Evict exactly one least-recently-used selector when full. The previous
            // implementation cleared all 256 entries at once, creating periodic parse
            // storms on selector-heavy applications.
            if (cache.Entries.Count >= MaxCacheSize)
            {
                var leastRecent = cache.Recency.Last;
                if (leastRecent != null)
                {
                    cache.Recency.RemoveLast();
                    cache.Entries.Remove(leastRecent.Value.Key);
                }
            }

            var newNode = cache.Recency.AddFirst(new SelectorCacheEntry(selectors, compiled));
            cache.Entries[selectors] = newNode;
            return compiled;
        }

        /// <summary>
        /// Clears the selector cache for this thread.
        /// </summary>
        public static void ClearCache()
        {
            var cache = _cache;
            if (cache == null) return;
            cache.Entries.Clear();
            cache.Recency.Clear();
        }

        private static Element QueryFirstInternal(Node root, CompiledSelector selector)
        {
            var stack = new Stack<Node>();
            PushChildrenReverse(root, stack);

            while (stack.Count > 0)
            {
                var node = stack.Pop();

                if (node is Element el && selector.Matches(el))
                {
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

            for (var child = container.LastChild; child != null; child = child.PreviousSibling)
                stack.Push(child);
        }
    }

    /// <summary>
    /// Extension methods for easy selector API on elements.
    /// </summary>
    public static class SelectorExtensions
    {
        public static Element QuerySelector(this ContainerNode node, string selectors)
        {
            return SelectorEngine.QueryFirst(node, selectors);
        }

        public static NodeList QuerySelectorAll(this ContainerNode node, string selectors)
        {
            return SelectorEngine.QueryAll(node, selectors);
        }

        public static bool Matches(this Element element, string selectors)
        {
            return SelectorEngine.Matches(element, selectors);
        }

        public static Element Closest(this Element element, string selectors)
        {
            return SelectorEngine.Closest(element, selectors);
        }
    }
}
