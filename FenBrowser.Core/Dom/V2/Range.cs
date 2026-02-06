// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: Range interface.
    /// https://dom.spec.whatwg.org/#interface-range
    ///
    /// Represents a fragment of a document that can span multiple nodes.
    /// </summary>
    public sealed class Range
    {
        private Node _startContainer;
        private int _startOffset;
        private Node _endContainer;
        private int _endOffset;

        /// <summary>
        /// The node in which the range starts.
        /// </summary>
        public Node StartContainer => _startContainer;

        /// <summary>
        /// The offset within StartContainer.
        /// </summary>
        public int StartOffset => _startOffset;

        /// <summary>
        /// The node in which the range ends.
        /// </summary>
        public Node EndContainer => _endContainer;

        /// <summary>
        /// The offset within EndContainer.
        /// </summary>
        public int EndOffset => _endOffset;

        /// <summary>
        /// Whether the start and end are the same.
        /// </summary>
        public bool Collapsed => _startContainer == _endContainer && _startOffset == _endOffset;

        /// <summary>
        /// The deepest common ancestor of start and end containers.
        /// </summary>
        public Node CommonAncestorContainer
        {
            get
            {
                var startAncestors = new HashSet<Node>();
                for (var n = _startContainer; n != null; n = n.ParentNode)
                    startAncestors.Add(n);

                for (var n = _endContainer; n != null; n = n.ParentNode)
                {
                    if (startAncestors.Contains(n))
                        return n;
                }

                return null;
            }
        }

        public Range(Document owner)
        {
            _startContainer = owner;
            _startOffset = 0;
            _endContainer = owner;
            _endOffset = 0;
        }

        /// <summary>
        /// Sets the start boundary.
        /// </summary>
        public void SetStart(Node node, int offset)
        {
            ValidateBoundary(node, offset);
            _startContainer = node;
            _startOffset = offset;

            // Ensure end is not before start
            if (ComparePoints(_endContainer, _endOffset, node, offset) < 0)
            {
                _endContainer = node;
                _endOffset = offset;
            }
        }

        /// <summary>
        /// Sets the end boundary.
        /// </summary>
        public void SetEnd(Node node, int offset)
        {
            ValidateBoundary(node, offset);
            _endContainer = node;
            _endOffset = offset;

            // Ensure start is not after end
            if (ComparePoints(_startContainer, _startOffset, node, offset) > 0)
            {
                _startContainer = node;
                _startOffset = offset;
            }
        }

        /// <summary>
        /// Sets the start before the given node.
        /// </summary>
        public void SetStartBefore(Node node)
        {
            var parent = node.ParentNode;
            if (parent == null)
                throw new DomException("InvalidNodeTypeError", "Node has no parent");

            SetStart(parent, GetNodeIndex(node));
        }

        /// <summary>
        /// Sets the start after the given node.
        /// </summary>
        public void SetStartAfter(Node node)
        {
            var parent = node.ParentNode;
            if (parent == null)
                throw new DomException("InvalidNodeTypeError", "Node has no parent");

            SetStart(parent, GetNodeIndex(node) + 1);
        }

        /// <summary>
        /// Sets the end before the given node.
        /// </summary>
        public void SetEndBefore(Node node)
        {
            var parent = node.ParentNode;
            if (parent == null)
                throw new DomException("InvalidNodeTypeError", "Node has no parent");

            SetEnd(parent, GetNodeIndex(node));
        }

        /// <summary>
        /// Sets the end after the given node.
        /// </summary>
        public void SetEndAfter(Node node)
        {
            var parent = node.ParentNode;
            if (parent == null)
                throw new DomException("InvalidNodeTypeError", "Node has no parent");

            SetEnd(parent, GetNodeIndex(node) + 1);
        }

        /// <summary>
        /// Collapses the range to one of its boundary points.
        /// </summary>
        public void Collapse(bool toStart = false)
        {
            if (toStart)
            {
                _endContainer = _startContainer;
                _endOffset = _startOffset;
            }
            else
            {
                _startContainer = _endContainer;
                _startOffset = _endOffset;
            }
        }

        /// <summary>
        /// Selects a node and its contents.
        /// </summary>
        public void SelectNode(Node node)
        {
            var parent = node.ParentNode;
            if (parent == null)
                throw new DomException("InvalidNodeTypeError", "Node has no parent");

            int index = GetNodeIndex(node);
            _startContainer = parent;
            _startOffset = index;
            _endContainer = parent;
            _endOffset = index + 1;
        }

        /// <summary>
        /// Selects the contents of a node.
        /// </summary>
        public void SelectNodeContents(Node node)
        {
            _startContainer = node;
            _startOffset = 0;
            _endContainer = node;
            _endOffset = GetNodeLength(node);
        }

        /// <summary>
        /// Compares a boundary point with another.
        /// </summary>
        public short CompareBoundaryPoints(ushort how, Range sourceRange)
        {
            // how: START_TO_START = 0, START_TO_END = 1, END_TO_END = 2, END_TO_START = 3
            Node thisContainer, sourceContainer;
            int thisOffset, sourceOffset;

            switch (how)
            {
                case 0: // START_TO_START
                    thisContainer = _startContainer;
                    thisOffset = _startOffset;
                    sourceContainer = sourceRange._startContainer;
                    sourceOffset = sourceRange._startOffset;
                    break;
                case 1: // START_TO_END
                    thisContainer = _endContainer;
                    thisOffset = _endOffset;
                    sourceContainer = sourceRange._startContainer;
                    sourceOffset = sourceRange._startOffset;
                    break;
                case 2: // END_TO_END
                    thisContainer = _endContainer;
                    thisOffset = _endOffset;
                    sourceContainer = sourceRange._endContainer;
                    sourceOffset = sourceRange._endOffset;
                    break;
                case 3: // END_TO_START
                    thisContainer = _startContainer;
                    thisOffset = _startOffset;
                    sourceContainer = sourceRange._endContainer;
                    sourceOffset = sourceRange._endOffset;
                    break;
                default:
                    throw new DomException("NotSupportedError", "Invalid comparison type");
            }

            return (short)ComparePoints(thisContainer, thisOffset, sourceContainer, sourceOffset);
        }

        /// <summary>
        /// Deletes the contents of the range.
        /// https://dom.spec.whatwg.org/#dom-range-deletecontents
        /// </summary>
        public void DeleteContents()
        {
            if (Collapsed) return;

            // Same container optimization
            if (_startContainer == _endContainer)
            {
                DeleteFromSameContainer();
                return;
            }

            // Extract then discard (simplification of full spec algorithm)
            ExtractContentsInternal(deleteOnly: true);
        }

        /// <summary>
        /// Extracts the contents of the range.
        /// https://dom.spec.whatwg.org/#dom-range-extractcontents
        /// </summary>
        public DocumentFragment ExtractContents()
        {
            if (Collapsed)
                return CreateFragment();

            return ExtractContentsInternal(deleteOnly: false);
        }

        /// <summary>
        /// Clones the contents of the range.
        /// https://dom.spec.whatwg.org/#dom-range-clonecontents
        /// </summary>
        public DocumentFragment CloneContents()
        {
            if (Collapsed)
                return CreateFragment();

            return CloneContentsInternal();
        }

        /// <summary>
        /// Inserts a node at the start of the range.
        /// https://dom.spec.whatwg.org/#dom-range-insertnode
        /// </summary>
        public void InsertNode(Node node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            // Validate node type
            if (node is Document || node is DocumentType)
                throw new DomException("HierarchyRequestError", "Cannot insert a Document or DocumentType");

            Node referenceNode;
            ContainerNode parent;

            if (_startContainer is CharacterData charData)
            {
                // Split the text node
                referenceNode = charData;
                parent = charData.ParentNode as ContainerNode;

                if (parent == null)
                    throw new DomException("HierarchyRequestError", "CharacterData has no parent");

                if (_startOffset > 0 && _startOffset < charData.Length)
                {
                    // Split at offset
                    if (charData is Text textNode)
                    {
                        referenceNode = textNode.SplitText(_startOffset);
                    }
                }
            }
            else if (_startContainer is ContainerNode container)
            {
                parent = container;
                referenceNode = _startOffset < container.ChildCount
                    ? container.ChildNodes[_startOffset]
                    : null;
            }
            else
            {
                throw new DomException("HierarchyRequestError", "Invalid start container");
            }

            // Insert the node
            if (referenceNode != null)
                parent.InsertBefore(node, referenceNode);
            else
                parent.AppendChild(node);
        }

        /// <summary>
        /// Surrounds the contents with a node.
        /// https://dom.spec.whatwg.org/#dom-range-surroundcontents
        /// </summary>
        public void SurroundContents(Node newParent)
        {
            if (newParent == null)
                throw new ArgumentNullException(nameof(newParent));

            // Validate node type
            if (newParent is Document || newParent is DocumentType || newParent is DocumentFragment)
                throw new DomException("InvalidNodeTypeError", "Cannot surround with Document, DocumentType, or DocumentFragment");

            // Check if range partially contains any non-text nodes
            if (PartiallyContainsNonTextNode())
                throw new DomException("InvalidStateError", "Range partially contains a non-text node");

            // Extract contents
            var fragment = ExtractContents();

            // Clear newParent's children
            if (newParent is ContainerNode container)
            {
                while (container.FirstChild != null)
                    container.RemoveChild(container.FirstChild);
            }

            // Insert newParent at range start
            InsertNode(newParent);

            // Append extracted contents to newParent
            if (newParent is ContainerNode parentContainer)
                parentContainer.AppendChild(fragment);

            // Select newParent
            SelectNode(newParent);
        }

        // --- Internal Methods ---

        private DocumentFragment CreateFragment()
        {
            var doc = _startContainer._ownerDocument ?? (_startContainer as Document);
            return doc?.CreateDocumentFragment() ?? new DocumentFragment();
        }

        private void DeleteFromSameContainer()
        {
            if (_startContainer is CharacterData charData)
            {
                charData.DeleteData(_startOffset, _endOffset - _startOffset);
            }
            else if (_startContainer is ContainerNode container)
            {
                // Remove children in range
                var children = new List<Node>();
                int i = 0;
                for (var child = container.FirstChild; child != null; child = child.NextSibling)
                {
                    if (i >= _startOffset && i < _endOffset)
                        children.Add(child);
                    i++;
                    if (i >= _endOffset) break;
                }

                foreach (var child in children)
                    container.RemoveChild(child);
            }

            Collapse(true);
        }

        private DocumentFragment ExtractContentsInternal(bool deleteOnly)
        {
            var fragment = deleteOnly ? null : CreateFragment();

            // Simple case: same container
            if (_startContainer == _endContainer)
            {
                if (_startContainer is CharacterData charData)
                {
                    var extracted = charData.SubstringData(_startOffset, _endOffset - _startOffset);
                    charData.DeleteData(_startOffset, _endOffset - _startOffset);

                    if (!deleteOnly && fragment != null)
                    {
                        var text = new Text(extracted, _startContainer._ownerDocument);
                        fragment.AppendChild(text);
                    }
                }
                else if (_startContainer is ContainerNode container)
                {
                    var children = new List<Node>();
                    int i = 0;
                    for (var child = container.FirstChild; child != null; child = child.NextSibling)
                    {
                        if (i >= _startOffset && i < _endOffset)
                            children.Add(child);
                        i++;
                        if (i >= _endOffset) break;
                    }

                    foreach (var child in children)
                    {
                        container.RemoveChild(child);
                        if (!deleteOnly && fragment != null)
                            fragment.AppendChild(child);
                    }
                }

                Collapse(true);
                return fragment;
            }

            // Complex case: different containers
            // Collect nodes to extract
            var nodesToExtract = CollectNodesInRange();

            foreach (var node in nodesToExtract)
            {
                if (node.ParentNode is ContainerNode parent)
                {
                    parent.RemoveChild(node);
                    if (!deleteOnly && fragment != null)
                        fragment.AppendChild(node);
                }
            }

            Collapse(true);
            return fragment;
        }

        private DocumentFragment CloneContentsInternal()
        {
            var fragment = CreateFragment();

            // Simple case: same container
            if (_startContainer == _endContainer)
            {
                if (_startContainer is CharacterData charData)
                {
                    var cloned = charData.SubstringData(_startOffset, _endOffset - _startOffset);
                    var text = new Text(cloned, _startContainer._ownerDocument);
                    fragment.AppendChild(text);
                }
                else if (_startContainer is ContainerNode container)
                {
                    int i = 0;
                    for (var child = container.FirstChild; child != null; child = child.NextSibling)
                    {
                        if (i >= _startOffset && i < _endOffset)
                            fragment.AppendChild(child.CloneNode(true));
                        i++;
                        if (i >= _endOffset) break;
                    }
                }

                return fragment;
            }

            // Complex case: different containers
            var nodesToClone = CollectNodesInRange();
            foreach (var node in nodesToClone)
            {
                fragment.AppendChild(node.CloneNode(true));
            }

            return fragment;
        }

        private List<Node> CollectNodesInRange()
        {
            var result = new List<Node>();
            var commonAncestor = CommonAncestorContainer;
            if (commonAncestor == null) return result;

            // Walk through descendants and collect fully contained nodes
            foreach (var node in commonAncestor.Descendants())
            {
                if (IsNodeFullyContained(node))
                    result.Add(node);
            }

            return result;
        }

        private bool IsNodeFullyContained(Node node)
        {
            var parent = node.ParentNode;
            if (parent == null) return false;

            int index = GetNodeIndex(node);

            // Check if entirely within range
            return ComparePoints(parent, index, _startContainer, _startOffset) >= 0 &&
                   ComparePoints(parent, index + 1, _endContainer, _endOffset) <= 0;
        }

        private bool PartiallyContainsNonTextNode()
        {
            // Check if range start is inside a non-text node
            if (_startContainer.NodeType != NodeType.Text &&
                _startContainer.NodeType != NodeType.Comment &&
                _startOffset > 0 && _startOffset < GetNodeLength(_startContainer))
            {
                return true;
            }

            // Check if range end is inside a non-text node
            if (_endContainer.NodeType != NodeType.Text &&
                _endContainer.NodeType != NodeType.Comment &&
                _endOffset > 0 && _endOffset < GetNodeLength(_endContainer))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Clones the range.
        /// </summary>
        public Range CloneRange()
        {
            var clone = new Range(_startContainer._ownerDocument);
            clone._startContainer = _startContainer;
            clone._startOffset = _startOffset;
            clone._endContainer = _endContainer;
            clone._endOffset = _endOffset;
            return clone;
        }

        /// <summary>
        /// Detaches the range (no-op in modern browsers).
        /// </summary>
        public void Detach() { }

        /// <summary>
        /// Returns true if the point is in the range.
        /// </summary>
        public bool IsPointInRange(Node node, int offset)
        {
            if (node.GetRootNode() != _startContainer.GetRootNode())
                return false;

            ValidateBoundary(node, offset);

            if (ComparePoints(node, offset, _startContainer, _startOffset) < 0)
                return false;
            if (ComparePoints(node, offset, _endContainer, _endOffset) > 0)
                return false;

            return true;
        }

        /// <summary>
        /// Compares a point to the range.
        /// </summary>
        public short ComparePoint(Node node, int offset)
        {
            if (node.GetRootNode() != _startContainer.GetRootNode())
                throw new DomException("WrongDocumentError", "Node is in a different document");

            ValidateBoundary(node, offset);

            if (ComparePoints(node, offset, _startContainer, _startOffset) < 0)
                return -1;
            if (ComparePoints(node, offset, _endContainer, _endOffset) > 0)
                return 1;
            return 0;
        }

        /// <summary>
        /// Returns true if the node intersects the range.
        /// </summary>
        public bool IntersectsNode(Node node)
        {
            if (node.GetRootNode() != _startContainer.GetRootNode())
                return false;

            var parent = node.ParentNode;
            if (parent == null)
                return true;

            int index = GetNodeIndex(node);

            if (ComparePoints(parent, index, _endContainer, _endOffset) > 0)
                return false;
            if (ComparePoints(parent, index + 1, _startContainer, _startOffset) < 0)
                return false;

            return true;
        }

        /// <summary>
        /// Returns the text content within the range.
        /// https://dom.spec.whatwg.org/#dom-range-stringifier
        /// </summary>
        public override string ToString()
        {
            if (Collapsed) return "";

            var sb = new System.Text.StringBuilder();

            // Same container - simple case
            if (_startContainer == _endContainer)
            {
                if (_startContainer is CharacterData charData)
                {
                    return charData.SubstringData(_startOffset, _endOffset - _startOffset);
                }
                else if (_startContainer is ContainerNode container)
                {
                    int i = 0;
                    for (var child = container.FirstChild; child != null; child = child.NextSibling)
                    {
                        if (i >= _startOffset && i < _endOffset)
                            CollectTextContent(child, sb);
                        i++;
                        if (i >= _endOffset) break;
                    }
                }
                return sb.ToString();
            }

            // Different containers - walk the range
            CollectRangeTextContent(sb);
            return sb.ToString();
        }

        private void CollectTextContent(Node node, System.Text.StringBuilder sb)
        {
            if (node is Text text)
            {
                sb.Append(text.Data);
            }
            else if (node is ContainerNode container)
            {
                for (var child = container.FirstChild; child != null; child = child.NextSibling)
                {
                    CollectTextContent(child, sb);
                }
            }
        }

        private void CollectRangeTextContent(System.Text.StringBuilder sb)
        {
            var commonAncestor = CommonAncestorContainer;
            if (commonAncestor == null) return;

            // Walk through all text nodes in the range
            bool started = false;
            bool ended = false;

            WalkNodesInRange(commonAncestor, ref started, ref ended, sb);
        }

        private void WalkNodesInRange(Node node, ref bool started, ref bool ended, System.Text.StringBuilder sb)
        {
            if (ended) return;

            // Check if this is the start container
            if (node == _startContainer)
            {
                started = true;
                if (node is CharacterData charData)
                {
                    int startIdx = _startOffset;
                    int endIdx = (node == _endContainer) ? _endOffset : charData.Length;
                    sb.Append(charData.SubstringData(startIdx, endIdx - startIdx));
                    if (node == _endContainer)
                    {
                        ended = true;
                        return;
                    }
                }
            }
            else if (node == _endContainer)
            {
                if (node is CharacterData charData)
                {
                    sb.Append(charData.SubstringData(0, _endOffset));
                }
                ended = true;
                return;
            }
            else if (started && node is Text text)
            {
                sb.Append(text.Data);
            }

            // Walk children
            if (node is ContainerNode container)
            {
                for (var child = container.FirstChild; child != null && !ended; child = child.NextSibling)
                {
                    WalkNodesInRange(child, ref started, ref ended, sb);
                }
            }
        }

        // --- Helpers ---

        private static void ValidateBoundary(Node node, int offset)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));
            if (offset < 0 || offset > GetNodeLength(node))
                throw new DomException("IndexSizeError", "Offset is out of range");
        }

        private static int GetNodeLength(Node node)
        {
            if (node is CharacterData cd)
                return cd.Length;
            return node.ChildNodes.Length;
        }

        private static int GetNodeIndex(Node node)
        {
            int index = 0;
            for (var sibling = node.PreviousSibling; sibling != null; sibling = sibling.PreviousSibling)
                index++;
            return index;
        }

        private static int ComparePoints(Node node1, int offset1, Node node2, int offset2)
        {
            if (node1 == node2)
                return offset1.CompareTo(offset2);

            // Check if node1 is ancestor of node2
            for (var n = node2; n != null; n = n.ParentNode)
            {
                if (n == node1)
                    return offset1 <= GetNodeIndex(FindChildContaining(node1, node2)) ? -1 : 1;
            }

            // Check if node2 is ancestor of node1
            for (var n = node1; n != null; n = n.ParentNode)
            {
                if (n == node2)
                    return GetNodeIndex(FindChildContaining(node2, node1)) < offset2 ? -1 : 1;
            }

            // Different branches - use document order
            var pos = node1.CompareDocumentPosition(node2);
            if ((pos & DocumentPosition.Following) != 0)
                return -1;
            if ((pos & DocumentPosition.Preceding) != 0)
                return 1;

            return 0;
        }

        private static Node FindChildContaining(Node ancestor, Node descendant)
        {
            for (var n = descendant; n != null; n = n.ParentNode)
            {
                if (n.ParentNode == ancestor)
                    return n;
            }
            return null;
        }
    }
}
