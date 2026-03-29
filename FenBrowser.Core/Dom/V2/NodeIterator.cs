// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: NodeIterator interface.
    /// https://dom.spec.whatwg.org/#interface-nodeiterator
    ///
    /// Used for iterating through nodes in document order.
    /// </summary>
    public sealed class NodeIterator
    {
        private Document _owningDocument;

        /// <summary>
        /// The root of the iteration.
        /// </summary>
        public Node Root { get; }

        /// <summary>
        /// The current reference node.
        /// </summary>
        public Node ReferenceNode { get; private set; }

        /// <summary>
        /// Whether the pointer is before the reference node.
        /// </summary>
        public bool PointerBeforeReferenceNode { get; private set; }

        /// <summary>
        /// Bitmask of node types to include.
        /// </summary>
        public uint WhatToShow { get; }

        /// <summary>
        /// Optional filter callback.
        /// </summary>
        public NodeFilter Filter { get; }

        public NodeIterator(Node root, uint whatToShow = 0xFFFFFFFF, NodeFilter filter = null)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            WhatToShow = whatToShow;
            Filter = filter;
            ReferenceNode = root;
            PointerBeforeReferenceNode = true;
        }

        /// <summary>
        /// Returns the next node.
        /// </summary>
        public Node NextNode()
        {
            return Traverse(true);
        }

        /// <summary>
        /// Returns the previous node.
        /// </summary>
        public Node PreviousNode()
        {
            return Traverse(false);
        }

        /// <summary>
        /// Detaches the iterator (no-op in modern browsers).
        /// </summary>
        public void Detach()
        {
            _owningDocument?.UnregisterNodeIterator(this);
            _owningDocument = null;
        }

        private Node Traverse(bool next)
        {
            var node = ReferenceNode;
            var beforeNode = PointerBeforeReferenceNode;

            while (true)
            {
                if (next)
                {
                    if (!beforeNode)
                    {
                        node = NextNodeInTree(node);
                        if (node == null)
                            return null;
                    }
                    beforeNode = false;
                }
                else
                {
                    if (beforeNode)
                    {
                        node = PreviousNodeInTree(node);
                        if (node == null)
                            return null;
                    }
                    beforeNode = true;
                }

                var result = AcceptNode(node);
                if (result == NodeFilterResult.Accept)
                {
                    ReferenceNode = node;
                    PointerBeforeReferenceNode = beforeNode;
                    return node;
                }
            }
        }

        private Node NextNodeInTree(Node node)
        {
            // First try children
            if (node.HasChildNodes)
                return node.FirstChild;

            // Then try siblings
            while (node != null && node != Root)
            {
                if (node.NextSibling != null)
                    return node.NextSibling;
                node = node.ParentNode;
            }

            return null;
        }

        private Node PreviousNodeInTree(Node node)
        {
            // If at root, can't go back
            if (node == Root)
                return null;

            // Try previous sibling
            if (node.PreviousSibling != null)
            {
                // Go to last descendant of previous sibling
                node = node.PreviousSibling;
                while (node.HasChildNodes)
                    node = node.LastChild;
                return node;
            }

            // Go to parent
            return node.ParentNode == Root ? null : node.ParentNode;
        }

        private NodeFilterResult AcceptNode(Node node)
        {
            // Check whatToShow
            uint flag = 1u << ((int)node.NodeType - 1);
            if ((WhatToShow & flag) == 0)
                return NodeFilterResult.Skip;

            // Check filter
            if (Filter != null)
                return Filter(node);

            return NodeFilterResult.Accept;
        }

        /// <summary>
        /// Called when a node is removed from the document.
        /// Updates the iterator state appropriately.
        /// </summary>
        internal void OnNodeRemoved(Node node)
        {
            // Check if removed node is an ancestor of or is the reference node
            if (!IsAncestorOrSelf(node, ReferenceNode))
                return;

            if (PointerBeforeReferenceNode)
            {
                // Find next node not in removed subtree
                var next = NextNodeInTree(node);
                while (next != null && IsAncestorOrSelf(node, next))
                    next = NextNodeInTree(next);

                if (next != null)
                {
                    ReferenceNode = next;
                }
                else
                {
                    // Go to previous
                    var prev = PreviousNodeInTree(node);
                    while (prev != null && IsAncestorOrSelf(node, prev))
                        prev = PreviousNodeInTree(prev);

                    ReferenceNode = prev ?? Root;
                    PointerBeforeReferenceNode = false;
                }
            }
            else
            {
                // Find previous node not in removed subtree
                var prev = PreviousNodeInTree(node);
                while (prev != null && IsAncestorOrSelf(node, prev))
                    prev = PreviousNodeInTree(prev);

                if (prev != null)
                {
                    ReferenceNode = prev;
                }
                else
                {
                    // Go to next
                    var next = NextNodeInTree(node);
                    while (next != null && IsAncestorOrSelf(node, next))
                        next = NextNodeInTree(next);

                    ReferenceNode = next ?? Root;
                    PointerBeforeReferenceNode = true;
                }
            }
        }

        private static bool IsAncestorOrSelf(Node ancestor, Node descendant)
        {
            for (var n = descendant; n != null; n = n.ParentNode)
            {
                if (n == ancestor)
                    return true;
            }
            return false;
        }

        internal void Attach(Document ownerDocument)
        {
            _owningDocument = ownerDocument;
        }
    }
}
