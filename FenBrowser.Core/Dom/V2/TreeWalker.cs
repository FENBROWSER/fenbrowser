// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: TreeWalker interface.
    /// https://dom.spec.whatwg.org/#interface-treewalker
    ///
    /// Used for traversing the DOM tree.
    /// </summary>
    public sealed class TreeWalker
    {
        /// <summary>
        /// The root of the traversal.
        /// </summary>
        public Node Root { get; }

        /// <summary>
        /// Bitmask of node types to include.
        /// </summary>
        public uint WhatToShow { get; }

        /// <summary>
        /// Optional filter callback.
        /// </summary>
        public NodeFilter Filter { get; }

        /// <summary>
        /// The current node.
        /// </summary>
        private Node _currentNode;

        public Node CurrentNode
        {
            get => _currentNode;
            set
            {
                if (value == null)
                    throw new ArgumentNullException(nameof(value));
                if (!IsInclusiveDescendantOfRoot(value))
                    throw new DomException(DomExceptionNames.NotFoundError,
                        "CurrentNode must stay within the TreeWalker root.");
                _currentNode = value;
            }
        }

        public TreeWalker(Node root, uint whatToShow = 0xFFFFFFFF, NodeFilter filter = null)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            WhatToShow = whatToShow;
            Filter = filter;
            _currentNode = root;
        }

        /// <summary>
        /// Moves to the parent node.
        /// </summary>
        public Node ParentNode()
        {
            var node = CurrentNode;
            while (node != null && node != Root)
            {
                node = node.ParentNode;
                if (node != null && AcceptNode(node) == NodeFilterResult.Accept)
                {
                    CurrentNode = node;
                    return node;
                }
            }
            return null;
        }

        /// <summary>
        /// Moves to the first child.
        /// </summary>
        public Node FirstChild() => TraverseChildren(true);

        /// <summary>
        /// Moves to the last child.
        /// </summary>
        public Node LastChild() => TraverseChildren(false);

        /// <summary>
        /// Moves to the previous sibling.
        /// </summary>
        public Node PreviousSibling() => TraverseSiblings(false);

        /// <summary>
        /// Moves to the next sibling.
        /// </summary>
        public Node NextSibling() => TraverseSiblings(true);

        /// <summary>
        /// Moves to the previous node in document order.
        /// </summary>
        public Node PreviousNode()
        {
            var node = CurrentNode;
            while (node != Root)
            {
                var sibling = node.PreviousSibling;
                while (sibling != null)
                {
                    node = sibling;
                    var result = AcceptNode(node);

                    while (result != NodeFilterResult.Reject && node.HasChildNodes)
                    {
                        node = node.LastChild;
                        result = AcceptNode(node);
                    }

                    if (result == NodeFilterResult.Accept)
                    {
                        CurrentNode = node;
                        return node;
                    }

                    sibling = node.PreviousSibling;
                }

                if (node == Root || node.ParentNode == null)
                    return null;

                node = node.ParentNode;
                if (AcceptNode(node) == NodeFilterResult.Accept)
                {
                    CurrentNode = node;
                    return node;
                }
            }
            return null;
        }

        /// <summary>
        /// Moves to the next node in document order.
        /// </summary>
        public Node NextNode()
        {
            var node = CurrentNode;
            var result = NodeFilterResult.Accept;

            while (true)
            {
                while (result != NodeFilterResult.Reject && node.HasChildNodes)
                {
                    node = node.FirstChild;
                    result = AcceptNode(node);
                    if (result == NodeFilterResult.Accept)
                    {
                        CurrentNode = node;
                        return node;
                    }
                }

                var sibling = node;
                Node temp = null;

                while (sibling != null)
                {
                    if (sibling == Root)
                        return null;

                    temp = sibling.NextSibling;
                    if (temp != null)
                    {
                        node = temp;
                        break;
                    }

                    sibling = sibling.ParentNode;
                }

                if (temp == null)
                    return null;

                result = AcceptNode(node);
                if (result == NodeFilterResult.Accept)
                {
                    CurrentNode = node;
                    return node;
                }
            }
        }

        private Node TraverseChildren(bool first)
        {
            var node = first ? CurrentNode.FirstChild : CurrentNode.LastChild;

            while (node != null)
            {
                var result = AcceptNode(node);
                if (result == NodeFilterResult.Accept)
                {
                    CurrentNode = node;
                    return node;
                }

                if (result == NodeFilterResult.Skip)
                {
                    var child = first ? node.FirstChild : node.LastChild;
                    if (child != null)
                    {
                        node = child;
                        continue;
                    }
                }

                while (node != null)
                {
                    var sibling = first ? node.NextSibling : node.PreviousSibling;
                    if (sibling != null)
                    {
                        node = sibling;
                        break;
                    }

                    var parent = node.ParentNode;
                    if (parent == null || parent == Root || parent == CurrentNode)
                    {
                        return null;
                    }

                    node = parent;
                }
            }

            return null;
        }

        private Node TraverseSiblings(bool next)
        {
            var node = CurrentNode;
            if (node == Root)
                return null;

            while (true)
            {
                var sibling = next ? node.NextSibling : node.PreviousSibling;

                while (sibling != null)
                {
                    node = sibling;
                    var result = AcceptNode(node);
                    if (result == NodeFilterResult.Accept)
                    {
                        CurrentNode = node;
                        return node;
                    }

                    sibling = next ? node.FirstChild : node.LastChild;
                    if (result == NodeFilterResult.Reject || sibling == null)
                    {
                        sibling = next ? node.NextSibling : node.PreviousSibling;
                    }
                }

                node = node.ParentNode;
                if (node == null || node == Root)
                    return null;

                if (AcceptNode(node) == NodeFilterResult.Accept)
                    return null;
            }
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

        private bool IsInclusiveDescendantOfRoot(Node node)
        {
            for (var current = node; current != null; current = current.ParentNode)
            {
                if (ReferenceEquals(current, Root))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Node filter callback delegate.
    /// </summary>
    public delegate NodeFilterResult NodeFilter(Node node);

    /// <summary>
    /// Node filter result.
    /// </summary>
    public enum NodeFilterResult : ushort
    {
        Accept = 1,
        Reject = 2,
        Skip = 3
    }

    /// <summary>
    /// Constants for whatToShow.
    /// </summary>
    public static class NodeFilterShow
    {
        public const uint All = 0xFFFFFFFF;
        public const uint Element = 0x1;
        public const uint Attribute = 0x2;
        public const uint Text = 0x4;
        public const uint CDataSection = 0x8;
        public const uint EntityReference = 0x10;
        public const uint Entity = 0x20;
        public const uint ProcessingInstruction = 0x40;
        public const uint Comment = 0x80;
        public const uint Document = 0x100;
        public const uint DocumentType = 0x200;
        public const uint DocumentFragment = 0x400;
        public const uint Notation = 0x800;
    }
}
