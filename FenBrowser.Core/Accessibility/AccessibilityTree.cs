// FenBrowser.Core.Accessibility — document-scoped accessibility tree service

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Core.Accessibility
{
    /// <summary>
    /// Document-scoped accessibility tree. One instance per Document (GetOrCreate pattern).
    /// The tree is built lazily and invalidated automatically when DOM mutations occur.
    /// </summary>
    public sealed class AccessibilityTree
    {
        // ---- Static GetOrCreate cache ----

        private static readonly ConditionalWeakTable<Document, AccessibilityTree> Cache =
            new ConditionalWeakTable<Document, AccessibilityTree>();

        /// <summary>
        /// Returns the AccessibilityTree for <paramref name="doc"/>, creating it if necessary.
        /// </summary>
        public static AccessibilityTree For(Document doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return Cache.GetValue(doc, d => new AccessibilityTree(d));
        }

        // ---- Instance ----

        private readonly Document _doc;
        private AccessibilityNode _root;
        private bool _dirty = true;

        // Element → node lookup built alongside the tree
        private readonly Dictionary<Element, AccessibilityNode> _nodeIndex =
            new Dictionary<Element, AccessibilityNode>(new RefEqComparer());

        private AccessibilityTree(Document doc)
        {
            _doc = doc;
            // Use a WeakReference so the static event does not root this instance.
            // The ConditionalWeakTable keeps us alive only as long as `doc` is alive.
            // Once `doc` is collected the table drops us, the WeakReference goes dead,
            // and the lambda below becomes a harmless no-op — no memory leak.
            var weakSelf = new WeakReference<AccessibilityTree>(this);
            Node.OnMutation += (target, type, attrName, ns, added, removed) =>
            {
                if (!weakSelf.TryGetTarget(out var self)) return;
                if (target?.OwnerDocument == self._doc || ReferenceEquals(target, self._doc))
                    self.Invalidate();
            };
        }

        // ---- Public API ----

        /// <summary>
        /// Returns the root accessibility node (builds the tree on first access after invalidation).
        /// </summary>
        public AccessibilityNode Root
        {
            get
            {
                EnsureBuilt();
                return _root;
            }
        }

        /// <summary>
        /// Returns the accessibility node for the given element, or null if not in the tree.
        /// </summary>
        public AccessibilityNode NodeFor(Element el)
        {
            if (el == null) return null;
            EnsureBuilt();
            _nodeIndex.TryGetValue(el, out var node);
            return node;
        }

        /// <summary>Marks the entire tree as dirty so it will be rebuilt on next access.</summary>
        public void Invalidate()
        {
            _dirty = true;
            _root = null;
            _nodeIndex.Clear();
        }

        /// <summary>
        /// Marks the subtree rooted at <paramref name="root"/> as dirty.
        /// Currently triggers a full rebuild (partial rebuild optimization is future work).
        /// </summary>
        public void InvalidateSubtree(Element root)
        {
            // Simple implementation: full rebuild
            Invalidate();
        }

        // ---- Private ----

        private void EnsureBuilt()
        {
            if (!_dirty) return;
            _dirty = false;
            _nodeIndex.Clear();

            try
            {
                _root = AccessibilityTreeBuilder.Build(_doc);
                if (_root != null)
                    IndexNode(_root);
            }
            catch
            {
                // Accessibility tree build must never crash the page
                _root = null;
            }
        }

        private void IndexNode(AccessibilityNode node)
        {
            if (node?.SourceElement != null)
                _nodeIndex[node.SourceElement] = node;

            if (node?.Children != null)
            {
                foreach (var child in node.Children)
                    IndexNode(child);
            }
        }

        private sealed class RefEqComparer : IEqualityComparer<Element>
        {
            public bool Equals(Element x, Element y) => ReferenceEquals(x, y);
            public int GetHashCode(Element obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
