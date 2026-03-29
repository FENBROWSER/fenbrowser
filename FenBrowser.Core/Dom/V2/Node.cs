// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: Node interface.
    /// https://dom.spec.whatwg.org/#interface-node
    ///
    /// Abstract base for all DOM nodes.
    /// IMPORTANT: This class does NOT have a children list.
    /// Only ContainerNode subclasses can have children.
    /// </summary>
    public abstract class Node : EventTarget
    {
        // --- Static Mutation Event (for DevTools) ---
        // delegate void MutationHandler(Node target, string type, string? attrName, string? attrNamespace, List<Node>? addedNodes, List<Node>? removedNodes);
        public static event Action<Node, string, string?, string?, List<Node>?, List<Node>?>? OnMutation;

        internal static void NotifyMutation(Node target, string type, string? attrName, string? attrNamespace, List<Node>? addedNodes, List<Node>? removedNodes)
        {
            OnMutation?.Invoke(target, type, attrName, attrNamespace, addedNodes, removedNodes);
        }
        // --- Internal State ---
        internal NodeFlags _flags;
        internal Node _parentNode;
        internal Document _ownerDocument;

        // Sibling pointers for efficient traversal
        internal Node _nextSibling;
        internal Node _previousSibling;

        // Tree scope for shadow DOM isolation
        internal TreeScope _treeScope;

        // --- Abstract Properties ---

        /// <summary>
        /// Returns the type of this node.
        /// https://dom.spec.whatwg.org/#dom-node-nodetype
        /// </summary>
        public abstract NodeType NodeType { get; }

        /// <summary>
        /// Returns the name of this node.
        /// https://dom.spec.whatwg.org/#dom-node-nodename
        /// </summary>
        public abstract string NodeName { get; }

        // --- Virtual Properties ---

        /// <summary>
        /// Gets or sets the value of this node.
        /// https://dom.spec.whatwg.org/#dom-node-nodevalue
        /// </summary>
        public virtual string NodeValue
        {
            get => null;
            set { }
        }

        /// <summary>
        /// Gets or sets the text content of this node.
        /// https://dom.spec.whatwg.org/#dom-node-textcontent
        /// </summary>
        public virtual string TextContent
        {
            get => null;
            set { }
        }

        /// <summary>
        /// Serializes the node to HTML.
        /// </summary>
        public virtual string ToHtml() => "";

        [Obsolete("Use TextContent")]
        public string Text
        {
            get => TextContent;
            set => TextContent = value;
        }

        // --- Style Access (DEPRECATED - Use StyleCache extension methods) ---

        /// <summary>
        /// Gets or sets the computed style for this node.
        /// DEPRECATED: Use node.GetComputedStyle() and node.SetComputedStyle() extension methods instead.
        /// This property exists only for backward compatibility during migration.
        /// </summary>
        [Obsolete("Use StyleCache extension methods: node.GetComputedStyle() and node.SetComputedStyle(). " +
                  "Storing style on nodes violates separation of concerns.")]
        public FenBrowser.Core.Css.CssComputed ComputedStyle
        {
            get => Css.NodeStyleExtensions.GetComputedStyle(this);
            set => Css.NodeStyleExtensions.SetComputedStyle(this, value);
        }

        // --- Navigation Properties ---

        /// <summary>
        /// Returns the parent node (null if none).
        /// https://dom.spec.whatwg.org/#dom-node-parentnode
        /// </summary>
        public Node ParentNode => _parentNode;

        internal override EventTarget GetParentForEventDispatch() => _parentNode;

        /// <summary>
        /// Returns the parent element (null if parent is not an Element).
        /// https://dom.spec.whatwg.org/#dom-node-parentelement
        /// </summary>
        public Element ParentElement => _parentNode as Element;

        /// <summary>
        /// Returns the next sibling (null if none).
        /// https://dom.spec.whatwg.org/#dom-node-nextsibling
        /// </summary>
        public Node NextSibling => _nextSibling;

        /// <summary>
        /// Returns the previous sibling (null if none).
        /// https://dom.spec.whatwg.org/#dom-node-previoussibling
        /// </summary>
        public Node PreviousSibling => _previousSibling;

        /// <summary>
        /// Returns the first child (null for non-container nodes).
        /// https://dom.spec.whatwg.org/#dom-node-firstchild
        /// </summary>
        public virtual Node FirstChild => null;

        /// <summary>
        /// Returns the last child (null for non-container nodes).
        /// https://dom.spec.whatwg.org/#dom-node-lastchild
        /// </summary>
        public virtual Node LastChild => null;

        /// <summary>
        /// Returns the child nodes (empty for non-container nodes).
        /// https://dom.spec.whatwg.org/#dom-node-childnodes
        /// </summary>
        public virtual NodeList ChildNodes => EmptyNodeList.Instance;

        /// <summary>
        /// Returns whether this node has children.
        /// https://dom.spec.whatwg.org/#dom-node-haschildnodes
        /// </summary>
        public virtual bool HasChildNodes => false;

        /// <summary>
        /// Returns the owning document.
        /// https://dom.spec.whatwg.org/#dom-node-ownerdocument
        /// </summary>
        public Document OwnerDocument => _ownerDocument;

        /// <summary>
        /// Returns whether this node is connected to a document.
        /// https://dom.spec.whatwg.org/#dom-node-isconnected
        /// </summary>
        public bool IsConnected => (_flags & NodeFlags.IsConnected) != 0;


        // --- Backward Compatibility ---
        [Obsolete("Use ParentNode")]
        public Node Parent => ParentNode;

        [Obsolete("Use ChildNodes")]
        public virtual System.Collections.Generic.List<Node> Children
        {
            get
            {
                var list = new System.Collections.Generic.List<Node>();
                if (ChildNodes != null)
                {
                    for (int i = 0; i < ChildNodes.Length; i++)
                        list.Add(ChildNodes[i]);
                }
                return list;
            }
        }

        // --- Fast Type Checks (via flags) ---

        /// <summary>Returns true if this is an Element node.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsElement() => (_flags & NodeFlags.IsElement) != 0;

        /// <summary>Returns true if this is a Text node.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsText() => (_flags & NodeFlags.IsText) != 0;

        /// <summary>Returns true if this is a Comment node.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsComment() => (_flags & NodeFlags.IsComment) != 0;

        /// <summary>Returns true if this is a Document node.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsDocument() => (_flags & NodeFlags.IsDocument) != 0;

        /// <summary>Returns true if this is a DocumentType node.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsDocumentType() => (_flags & NodeFlags.IsDocumentType) != 0;

        /// <summary>Returns true if this is a DocumentFragment node.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsDocumentFragment() => (_flags & NodeFlags.IsDocumentFragment) != 0;

        /// <summary>Returns true if this node can have children.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool IsContainer() => (_flags & NodeFlags.IsContainer) != 0;

        // --- Dirty Flags ---

        /// <summary>Returns true if this node's style is dirty.</summary>
        public bool StyleDirty => (_flags & NodeFlags.StyleDirty) != 0;

        /// <summary>Returns true if any descendant's style is dirty.</summary>
        public bool ChildStyleDirty => (_flags & NodeFlags.ChildStyleDirty) != 0;

        /// <summary>Returns true if this node's layout is dirty.</summary>
        public bool LayoutDirty => (_flags & NodeFlags.LayoutDirty) != 0;

        /// <summary>Returns true if any descendant's layout is dirty.</summary>
        public bool ChildLayoutDirty => (_flags & NodeFlags.ChildLayoutDirty) != 0;

        /// <summary>Returns true if this node needs repainting.</summary>
        public bool PaintDirty => (_flags & NodeFlags.PaintDirty) != 0;

        /// <summary>Returns true if any descendant needs repainting.</summary>
        public bool ChildPaintDirty => (_flags & NodeFlags.ChildPaintDirty) != 0;

        /// <summary>
        /// Marks this node as dirty and propagates child dirty flags up.
        /// </summary>
        public void MarkDirty(InvalidationKind kind)
        {
            bool propagateStyle = false;
            bool propagateLayout = false;
            bool propagatePaint = false;

            if ((kind & InvalidationKind.Style) != 0 && !StyleDirty)
            {
                _flags |= NodeFlags.StyleDirty;
                propagateStyle = true;
            }
            if ((kind & InvalidationKind.Layout) != 0 && !LayoutDirty)
            {
                _flags |= NodeFlags.LayoutDirty;
                propagateLayout = true;
            }
            if ((kind & InvalidationKind.Paint) != 0 && !PaintDirty)
            {
                _flags |= NodeFlags.PaintDirty;
                propagatePaint = true;
            }

            // Propagate "ChildDirty" up to root
            if (propagateStyle || propagateLayout || propagatePaint)
            {
                PropagateChildDirtyUp(propagateStyle, propagateLayout, propagatePaint);
                _ownerDocument?.NotifyTreeDirty();
            }
        }

        private void PropagateChildDirtyUp(bool style, bool layout, bool paint)
        {
            var parent = _parentNode;
            while (parent != null)
            {
                bool changed = false;

                if (style && (parent._flags & NodeFlags.ChildStyleDirty) == 0)
                {
                    parent._flags |= NodeFlags.ChildStyleDirty;
                    changed = true;
                }
                if (layout && (parent._flags & NodeFlags.ChildLayoutDirty) == 0)
                {
                    parent._flags |= NodeFlags.ChildLayoutDirty;
                    changed = true;
                }
                if (paint && (parent._flags & NodeFlags.ChildPaintDirty) == 0)
                {
                    parent._flags |= NodeFlags.ChildPaintDirty;
                    changed = true;
                }

                if (!changed) break; // Path already marked
                parent = parent._parentNode;
            }
        }

        /// <summary>
        /// Clears only the StyleDirty flag on this node (preserves ChildStyleDirty).
        /// Used by incremental recascade after recomputing this node's style.
        /// </summary>
        public void ClearStyleDirty()
        {
            _flags &= ~NodeFlags.StyleDirty;
        }

        /// <summary>
        /// Clears dirty flags after processing.
        /// </summary>
        public void ClearDirty(InvalidationKind kind)
        {
            if ((kind & InvalidationKind.Style) != 0)
                _flags &= ~(NodeFlags.StyleDirty | NodeFlags.ChildStyleDirty);
            if ((kind & InvalidationKind.Layout) != 0)
                _flags &= ~(NodeFlags.LayoutDirty | NodeFlags.ChildLayoutDirty);
            if ((kind & InvalidationKind.Paint) != 0)
                _flags &= ~(NodeFlags.PaintDirty | NodeFlags.ChildPaintDirty);
        }

        // --- DOM Methods ---

        /// <summary>
        /// Returns a duplicate of this node.
        /// https://dom.spec.whatwg.org/#dom-node-clonenode
        /// </summary>
        public abstract Node CloneNode(bool deep = false);

        /// <summary>
        /// Returns true if this node is the same as other.
        /// https://dom.spec.whatwg.org/#dom-node-issamenode
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSameNode(Node other) => ReferenceEquals(this, other);

        /// <summary>
        /// Returns true if this node is equal to other (deep equality).
        /// https://dom.spec.whatwg.org/#dom-node-isequalnode
        /// </summary>
        public virtual bool IsEqualNode(Node other)
        {
            if (other == null) return false;
            if (NodeType != other.NodeType) return false;
            if (NodeName != other.NodeName) return false;
            if (NodeValue != other.NodeValue) return false;

            // Compare children
            var thisChildren = ChildNodes;
            var otherChildren = other.ChildNodes;
            if (thisChildren.Length != otherChildren.Length) return false;

            for (int i = 0; i < thisChildren.Length; i++)
            {
                if (!thisChildren[i].IsEqualNode(otherChildren[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Returns a bitmask of the relationship between this node and other.
        /// https://dom.spec.whatwg.org/#dom-node-comparedocumentposition
        /// </summary>
        public DocumentPosition CompareDocumentPosition(Node other)
        {
            if (other == null) return 0;
            if (ReferenceEquals(this, other)) return 0;

            // Different documents
            if (GetRootNode() != other.GetRootNode())
            {
                return DocumentPosition.Disconnected |
                       DocumentPosition.ImplementationSpecific |
                       (GetHashCode() < other.GetHashCode()
                           ? DocumentPosition.Preceding
                           : DocumentPosition.Following);
            }

            // Check ancestor/descendant relationship
            if (Contains(other))
                return DocumentPosition.ContainedBy | DocumentPosition.Following;
            if (other.Contains(this))
                return DocumentPosition.Contains | DocumentPosition.Preceding;

            // Find common ancestor and compare positions
            var thisAncestors = CollectAncestors(this);
            var otherAncestors = CollectAncestors(other);

            // Find divergence point
            Node commonAncestor = null;
            int thisIdx = thisAncestors.Count - 1;
            int otherIdx = otherAncestors.Count - 1;

            while (thisIdx >= 0 && otherIdx >= 0 &&
                   ReferenceEquals(thisAncestors[thisIdx], otherAncestors[otherIdx]))
            {
                commonAncestor = thisAncestors[thisIdx];
                thisIdx--;
                otherIdx--;
            }

            if (commonAncestor == null)
                return DocumentPosition.Disconnected | DocumentPosition.ImplementationSpecific;

            // Compare position within common ancestor's children
            var thisChild = thisIdx >= 0 ? thisAncestors[thisIdx] : this;
            var otherChild = otherIdx >= 0 ? otherAncestors[otherIdx] : other;

            for (var child = commonAncestor.FirstChild; child != null; child = child._nextSibling)
            {
                if (ReferenceEquals(child, thisChild))
                    return DocumentPosition.Following;
                if (ReferenceEquals(child, otherChild))
                    return DocumentPosition.Preceding;
            }

            return DocumentPosition.ImplementationSpecific;
        }

        /// <summary>
        /// Returns true if this node contains other.
        /// https://dom.spec.whatwg.org/#dom-node-contains
        /// </summary>
        public bool Contains(Node other)
        {
            if (other == null) return false;

            for (var node = other; node != null; node = node._parentNode)
            {
                if (ReferenceEquals(node, this))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the root of this node's tree.
        /// https://dom.spec.whatwg.org/#dom-node-getrootnode
        /// </summary>
        public Node GetRootNode(GetRootNodeOptions options = default)
        {
            var root = this;
            while (root._parentNode != null)
            {
                root = root._parentNode;
            }

            // If composed, continue through shadow host
            if (options.Composed && root is ShadowRoot shadowRoot)
            {
                return shadowRoot.Host.GetRootNode(options);
            }

            return root;
        }

        /// <summary>
        /// Normalizes this node by merging adjacent Text nodes.
        /// https://dom.spec.whatwg.org/#dom-node-normalize
        /// </summary>
        public virtual void Normalize()
        {
            // Default implementation does nothing.
            // ContainerNode overrides this.
        }

        // --- Internal Methods ---

        internal void SetParent(Node newParent)
        {
            var oldParent = _parentNode;
            if (ReferenceEquals(oldParent, newParent))
                return;

            _parentNode = newParent;
            OnParentChanged(oldParent, newParent);

            // Update connected flag
            UpdateConnectedFlag();
        }

        protected virtual void OnParentChanged(Node oldParent, Node newParent)
        {
            var newScope = ResolveTreeScope(newParent);
            if (!ReferenceEquals(_treeScope, newScope))
            {
                UpdateTreeScopeRecursive(newScope);
            }
        }

        private static TreeScope ResolveTreeScope(Node node)
        {
            return node switch
            {
                null => null,
                ShadowRoot shadowRoot => shadowRoot._treeScope,
                _ => node._treeScope
            };
        }

        private void UpdateTreeScopeRecursive(TreeScope newScope)
        {
            _treeScope = newScope;
            for (var child = FirstChild; child != null; child = child._nextSibling)
            {
                child.UpdateTreeScopeRecursive(newScope);
            }
        }

        private void UpdateConnectedFlag()
        {
            bool wasConnected = IsConnected;
            bool isNowConnected = _parentNode != null && _parentNode.IsConnected;

            // Special case: Document is always connected to itself
            if (this is Document)
                isNowConnected = true;

            if (wasConnected && !isNowConnected)
            {
                _flags &= ~NodeFlags.IsConnected;
                OnDisconnected();
                PropagateDisconnected();
            }
            else if (!wasConnected && isNowConnected)
            {
                _flags |= NodeFlags.IsConnected;
                OnConnected();
                PropagateConnected();
            }
        }

        private void PropagateConnected()
        {
            for (var child = FirstChild; child != null; child = child._nextSibling)
            {
                child._flags |= NodeFlags.IsConnected;
                child.OnConnected();
                child.PropagateConnected();
            }
        }

        private void PropagateDisconnected()
        {
            for (var child = FirstChild; child != null; child = child._nextSibling)
            {
                child._flags &= ~NodeFlags.IsConnected;
                child.OnDisconnected();
                child.PropagateDisconnected();
            }
        }

        protected virtual void OnConnected() { }
        protected virtual void OnDisconnected() { }

        private static List<Node> CollectAncestors(Node node)
        {
            var list = new List<Node>();
            for (var n = node; n != null; n = n._parentNode)
                list.Add(n);
            return list;
        }

        // --- Traversal Helpers ---

        /// <summary>
        /// Enumerates all ancestors (parent, grandparent, etc.)
        /// </summary>
        public IEnumerable<Node> Ancestors()
        {
            for (var p = _parentNode; p != null; p = p._parentNode)
                yield return p;
        }

        /// <summary>
        /// Enumerates all descendants in tree order (depth-first).
        /// </summary>
        public IEnumerable<Node> Descendants()
        {
            var child = FirstChild;
            while (child != null)
            {
                yield return child;

                // Depth first: go to first child if exists
                if (child.FirstChild != null)
                {
                    child = child.FirstChild;
                }
                else
                {
                    // No children, go to next sibling or backtrack
                    while (child != null && child._nextSibling == null && !ReferenceEquals(child, this))
                    {
                        child = child._parentNode;
                    }
                    if (child == null || ReferenceEquals(child, this))
                        break;
                    child = child._nextSibling;
                }
            }
        }

        /// <summary>
        /// Enumerates this node and all descendants.
        /// </summary>
        public IEnumerable<Node> SelfAndDescendants()
        {
            yield return this;
            foreach (var d in Descendants())
                yield return d;
        }
    }

    /// <summary>
    /// Options for GetRootNode.
    /// </summary>
    public struct GetRootNodeOptions
    {
        public bool Composed;
    }
}
