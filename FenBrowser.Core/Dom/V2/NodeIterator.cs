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
        private bool _isActive;

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
        /// Legacy API retained by the DOM standard. It intentionally does nothing.
        /// The iterator must remain registered for mutation-adjustment bookkeeping.
        /// </summary>
        public void Detach()
        {
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

                // The candidate is the traversal's in-flight position while the
                // filter runs: a filter that removes nodes has the pre-removing
                // steps retarget this position (DOM 6.1), not the last accepted
                // reference, so the iterator never rests on a detached node.
                _inFlightNode = node;
                _inFlightBefore = beforeNode;
                _inFlight = true;
                NodeFilterResult result;
                try
                {
                    result = AcceptNode(node);
                }
                finally
                {
                    _inFlight = false;
                }

                var retargeted = !ReferenceEquals(_inFlightNode, node) || _inFlightBefore != beforeNode;
                if (result == NodeFilterResult.Accept)
                {
                    ReferenceNode = _inFlightNode;
                    PointerBeforeReferenceNode = _inFlightBefore;
                    return node;
                }

                if (retargeted)
                {
                    node = _inFlightNode;
                    beforeNode = _inFlightBefore;
                }
            }
        }

        private Node _inFlightNode;
        private bool _inFlightBefore;
        private bool _inFlight;

        private Node NextNodeInTree(Node node)
        {
            if (node.HasChildNodes)
                return node.FirstChild;

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
            if (node == Root)
                return null;

            if (node.PreviousSibling != null)
            {
                node = node.PreviousSibling;
                while (node.HasChildNodes)
                    node = node.LastChild;
                return node;
            }

            return node.ParentNode;
        }

        private NodeFilterResult AcceptNode(Node node)
        {
            uint flag = 1u << ((int)node.NodeType - 1);
            if ((WhatToShow & flag) == 0)
                return NodeFilterResult.Skip;

            if (Filter == null)
                return NodeFilterResult.Accept;

            // DOM traversal filters are not reentrant. A callback that calls
            // nextNode()/previousNode() on this iterator while it is already being
            // evaluated must fail instead of mutating ReferenceNode mid-filter.
            if (_isActive)
                throw new DomException("InvalidStateError", "NodeIterator filter is already active");

            _isActive = true;
            try
            {
                return Filter(node);
            }
            finally
            {
                _isActive = false;
            }
        }

        /// <summary>
        /// DOM §6.1 NodeIterator pre-removing steps, run for every live iterator
        /// before a node is removed from its parent.
        /// https://dom.spec.whatwg.org/#nodeiterator-pre-removing-steps
        /// </summary>
        internal void OnNodeRemoved(Node node)
        {
            // While a filter runs, the position being retargeted is the
            // candidate under filtering, not the last accepted reference.
            var reference = _inFlight ? _inFlightNode : ReferenceNode;
            var pointerBefore = _inFlight ? _inFlightBefore : PointerBeforeReferenceNode;

            // Step 1: nothing to do unless the removed subtree contains the
            // reference, and never when it contains the root itself (the
            // iterator then just follows its root out of the tree).
            if (!IsAncestorOrSelf(node, reference) || IsAncestorOrSelf(node, Root))
                return;

            // Step 2: pointer before the reference - move to the first following
            // node inside root that is outside the removed subtree.
            if (pointerBefore)
            {
                var next = FollowingNodeOutside(node);
                if (next != null)
                {
                    SetPosition(next, true);
                    return;
                }

                pointerBefore = false;
            }

            // Step 3: the node just before the removed subtree in tree order -
            // the previous sibling's last inclusive descendant, else the parent.
            var previous = node.PreviousSibling;
            if (previous != null)
            {
                while (previous.HasChildNodes)
                    previous = previous.LastChild;
                SetPosition(previous, pointerBefore);
            }
            else
            {
                SetPosition(node.ParentNode ?? Root, pointerBefore);
            }
        }

        private void SetPosition(Node node, bool pointerBefore)
        {
            if (_inFlight)
            {
                _inFlightNode = node;
                _inFlightBefore = pointerBefore;
            }
            else
            {
                ReferenceNode = node;
                PointerBeforeReferenceNode = pointerBefore;
            }
        }

        /// <summary>
        /// The first node following <paramref name="node"/> in tree order that is
        /// an inclusive descendant of root but not of <paramref name="node"/>.
        /// </summary>
        private Node FollowingNodeOutside(Node node)
        {
            var current = node;
            while (current != null && current != Root)
            {
                if (current.NextSibling != null)
                    return current.NextSibling;
                current = current.ParentNode;
            }

            return null;
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
