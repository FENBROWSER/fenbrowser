// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Dom.V2.Selectors;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// Base class for nodes that can have children.
    /// Implements ParentNode mixin from WHATWG DOM.
    /// https://dom.spec.whatwg.org/#interface-parentnode
    ///
    /// Uses slot-based child storage (Chromium pattern):
    /// - 4 inline slots for 0-4 children (no heap allocation)
    /// - Overflow list for 5+ children
    /// Performance optimizations:
    /// - Cached ChildElementCount (invalidated on child mutations)
    /// - Version number for live collection invalidation
    /// </summary>
    public abstract class ContainerNode : Node, IParentNode
    {
        // --- Slot-Based Child Storage ---
        private ChildNodeStorage _children;

        // --- Performance: Cached counts (invalidated on mutation) ---
        private int _cachedChildElementCount = -1; // -1 means not cached
        private uint _childListVersion; // Incremented on any child mutation

        // --- Node Overrides ---

        public override Node FirstChild => _children.First;
        public override Node LastChild => _children.Last;
        public override bool HasChildNodes => _children.Count > 0;
        public override NodeList ChildNodes => new LiveChildNodeList(this);

        /// <summary>
        /// Gets or sets the text content of this node.
        /// https://dom.spec.whatwg.org/#dom-node-textcontent
        /// </summary>
        public override string TextContent
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                foreach (var child in Descendants())
                {
                    if (child.NodeType == NodeType.Text && child is CharacterData cd)
                        sb.Append(cd.Data);
                }
                return sb.ToString();
            }
            set
            {
                RemoveAllChildren();
                if (!string.IsNullOrEmpty(value))
                    AppendChild(new Text(value));
            }
        }

        // --- IParentNode Implementation ---

        /// <summary>
        /// Returns the number of child elements.
        /// https://dom.spec.whatwg.org/#dom-parentnode-childelementcount
        /// Cached for performance - invalidated on child mutations.
        /// </summary>
        public int ChildElementCount
        {
            get
            {
                if (_cachedChildElementCount < 0)
                {
                    int count = 0;
                    for (var child = FirstChild; child != null; child = child._nextSibling)
                    {
                        if (child.IsElement())
                            count++;
                    }
                    _cachedChildElementCount = count;
                }
                return _cachedChildElementCount;
            }
        }

        /// <summary>
        /// Returns the version number of the child list.
        /// Used by live collections to detect invalidation.
        /// </summary>
        internal uint ChildListVersion => _childListVersion;

        /// <summary>
        /// Invalidates cached child data.
        /// Called after any child mutation.
        /// </summary>
        private void InvalidateChildCache()
        {
            _cachedChildElementCount = -1;
            unchecked { _childListVersion++; }
        }

        /// <summary>
        /// Returns the first child element.
        /// https://dom.spec.whatwg.org/#dom-parentnode-firstelementchild
        /// </summary>
        public Element FirstElementChild
        {
            get
            {
                for (var child = FirstChild; child != null; child = child._nextSibling)
                {
                    if (child is Element el)
                        return el;
                }
                return null;
            }
        }

        /// <summary>
        /// Returns the last child element.
        /// https://dom.spec.whatwg.org/#dom-parentnode-lastelementchild
        /// </summary>
        public Element LastElementChild
        {
            get
            {
                for (var child = LastChild; child != null; child = child._previousSibling)
                {
                    if (child is Element el)
                        return el;
                }
                return null;
            }
        }

        /// <summary>
        /// Returns a live HTMLCollection of child elements.
        /// https://dom.spec.whatwg.org/#dom-parentnode-children
        /// </summary>
        /// <summary>
        /// Returns a live HTMLCollection of child elements.
        /// https://dom.spec.whatwg.org/#dom-parentnode-children
        /// </summary>
        public NodeList Children => new LiveElementChildList(this);

        // --- Child Mutation Methods ---

        /// <summary>
        /// Appends a node as the last child.
        /// https://dom.spec.whatwg.org/#dom-node-appendchild
        /// </summary>
        public Node AppendChild(Node node)
        {
            AssertNotInRestrictedPhase();
            ValidateForInsertion(node);
            return AppendChildInternal(node);
        }

        /// <summary>
        /// Inserts a node before the reference node.
        /// https://dom.spec.whatwg.org/#dom-node-insertbefore
        /// </summary>
        public Node InsertBefore(Node node, Node child)
        {
            AssertNotInRestrictedPhase();
            ValidateForInsertion(node);

            if (child == null)
                return AppendChildInternal(node);

            if (!ReferenceEquals(child._parentNode, this))
                throw new DomException("NotFoundError", "Child is not a child of this node");

            return InsertBeforeInternal(node, child);
        }

        /// <summary>
        /// Replaces a child node with another.
        /// https://dom.spec.whatwg.org/#dom-node-replacechild
        /// </summary>
        public Node ReplaceChild(Node node, Node child)
        {
            AssertNotInRestrictedPhase();
            ValidateForInsertion(node);

            if (!ReferenceEquals(child._parentNode, this))
                throw new DomException("NotFoundError", "Child is not a child of this node");

            return ReplaceChildInternal(node, child);
        }

        /// <summary>
        /// Removes a child node.
        /// https://dom.spec.whatwg.org/#dom-node-removechild
        /// </summary>
        public Node RemoveChild(Node child)
        {
            AssertNotInRestrictedPhase();

            if (child == null)
                throw new ArgumentNullException(nameof(child));
            if (!ReferenceEquals(child._parentNode, this))
                throw new DomException("NotFoundError", "Child is not a child of this node");

            return RemoveChildInternal(child);
        }

        // --- ParentNode Convenience Methods ---

        /// <summary>
        /// Prepends nodes before the first child.
        /// https://dom.spec.whatwg.org/#dom-parentnode-prepend
        /// </summary>
        public void Prepend(params Node[] nodes)
        {
            var firstChild = FirstChild;
            foreach (var node in nodes)
            {
                if (firstChild != null)
                    InsertBefore(node, firstChild);
                else
                    AppendChild(node);
            }
        }

        /// <summary>
        /// Appends nodes after the last child.
        /// https://dom.spec.whatwg.org/#dom-parentnode-append
        /// </summary>
        public void Append(params Node[] nodes)
        {
            foreach (var node in nodes)
                AppendChild(node);
        }

        /// <summary>
        /// Replaces all children with the given nodes.
        /// https://dom.spec.whatwg.org/#dom-parentnode-replacechildren
        /// </summary>
        public void ReplaceChildren(params Node[] nodes)
        {
            // Remove all existing children
            while (FirstChild != null)
                RemoveChildInternal(FirstChild);

            // Append new children
            foreach (var node in nodes)
                AppendChildInternal(node);
        }

        /// <summary>
        /// Removes all child nodes.
        /// </summary>
        public void RemoveAllChildren()
        {
            while (FirstChild != null)
                RemoveChild(FirstChild);
        }

        // --- Query Selectors ---

        /// <summary>
        /// Returns the first element matching the selector.
        /// https://dom.spec.whatwg.org/#dom-parentnode-queryselector
        /// Uses compiled SelectorEngine with bloom filter optimization.
        /// </summary>
        /// <param name="selectors">CSS selector string</param>
        /// <returns>First matching element or null</returns>
        /// <exception cref="DomException">SyntaxError if selector is invalid</exception>
        public Element QuerySelector(string selectors)
        {
            if (string.IsNullOrWhiteSpace(selectors))
                throw new DomException("SyntaxError", "Selector cannot be empty");

            try
            {
                return SelectorEngine.QueryFirst(this, selectors);
            }
            catch (DomException)
            {
                throw; // Re-throw DOMExceptions (invalid selectors)
            }
            catch (Exception ex)
            {
                throw new DomException("SyntaxError", $"Invalid selector: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns all elements matching the selector as a static NodeList.
        /// https://dom.spec.whatwg.org/#dom-parentnode-queryselectorall
        /// Uses compiled SelectorEngine with bloom filter optimization.
        /// </summary>
        /// <param name="selectors">CSS selector string</param>
        /// <returns>Static NodeList of matching elements</returns>
        /// <exception cref="DomException">SyntaxError if selector is invalid</exception>
        public NodeList QuerySelectorAll(string selectors)
        {
            if (string.IsNullOrWhiteSpace(selectors))
                throw new DomException("SyntaxError", "Selector cannot be empty");

            try
            {
                return SelectorEngine.QueryAll(this, selectors);
            }
            catch (DomException)
            {
                throw; // Re-throw DOMExceptions (invalid selectors)
            }
            catch (Exception ex)
            {
                throw new DomException("SyntaxError", $"Invalid selector: {ex.Message}");
            }
        }

        // --- Normalization ---

        public override void Normalize()
        {
            var child = FirstChild;
            while (child != null)
            {
                var next = child._nextSibling;

                // Recursively normalize children first
                child.Normalize();

                if (child is Text textNode)
                {
                    // Remove empty text nodes
                    if (string.IsNullOrEmpty(textNode.Data))
                    {
                        RemoveChildInternal(textNode);
                    }
                    // Merge adjacent text nodes
                    else if (next is Text nextText)
                    {
                        textNode.Data += nextText.Data;
                        RemoveChildInternal(nextText);
                        next = textNode._nextSibling; // Continue from merged position
                    }
                }

                child = next;
            }
        }




        // --- Internal Child Operations ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AssertNotInRestrictedPhase()
        {
            EngineContext.Current.AssertNotInPhase(
                EnginePhase.Measure,
                EnginePhase.Layout,
                EnginePhase.Paint);
        }

        private void ValidateForInsertion(Node node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            // Can't insert self as child
            if (ReferenceEquals(node, this))
                throw new DomException("HierarchyRequestError", "Cannot insert node as child of itself");

            // Check for cycles: node can't be ancestor of this
            for (Node p = this; p != null; p = p._parentNode)
            {
                if (ReferenceEquals(p, node))
                    throw new DomException("HierarchyRequestError", "Node cannot be an ancestor of its new parent");
            }

            // Check node type validity
            ValidateChildType(node);
        }

        protected virtual void ValidateChildType(Node node)
        {
            // By default, accept Element, Text, Comment, ProcessingInstruction, DocumentFragment
            // Document and DocumentType have additional restrictions
            if (node is Document)
                throw new DomException("HierarchyRequestError", "Cannot insert a Document as a child");
        }

        private Node AppendChildInternal(Node node)
        {
            // Handle DocumentFragment: insert all children instead
            if (node is DocumentFragment fragment)
            {
                while (fragment.FirstChild != null)
                    AppendChildInternal(fragment.FirstChild);
                return fragment;
            }

            // Remove from old parent
            ((ContainerNode)node._parentNode)?.RemoveChildInternal(node);

            // Adopt node
            AdoptNode(node);

            // Insert at end
            _children.Append(node);

            // Set parent and update siblings
            node.SetParent(this);

            // Invalidate cache
            InvalidateChildCache();

            // Notify observers
            NotifyChildListMutation(null, node);

            return node;
        }

        internal Node AppendChildUnsafe(Node node)
        {
             return AppendChildInternal(node);
        }

        private Node InsertBeforeInternal(Node node, Node child)
        {
            // Handle DocumentFragment
            if (node is DocumentFragment fragment)
            {
                while (fragment.FirstChild != null)
                    InsertBeforeInternal(fragment.FirstChild, child);
                return fragment;
            }

            // Remove from old parent
            ((ContainerNode)node._parentNode)?.RemoveChildInternal(node);

            // Adopt node
            AdoptNode(node);

            // Insert before child
            _children.InsertBefore(node, child);

            // Set parent
            node.SetParent(this);

            // Invalidate cache
            InvalidateChildCache();

            // Notify observers
            NotifyChildListMutation(null, node);

            return node;
        }

        private Node ReplaceChildInternal(Node node, Node child)
        {
            var nextSibling = child._nextSibling;

            // Remove old child
            RemoveChildInternal(child);

            // Insert new node at old position
            if (nextSibling != null)
                InsertBeforeInternal(node, nextSibling);
            else
                AppendChildInternal(node);

            return child;
        }

        internal Node RemoveChildUnsafe(Node child)
        {
             return RemoveChildInternal(child);
        }

        private Node RemoveChildInternal(Node child)
        {
            _children.Remove(child);
            child.SetParent(null);

            // Invalidate cache
            InvalidateChildCache();

            // Notify observers
            NotifyChildListMutation(child, null);

            return child;
        }

        private void AdoptNode(Node node)
        {
            // Set owner document
            if (node._ownerDocument != _ownerDocument)
            {
                AdoptNodeRecursive(node, _ownerDocument);
            }
        }

        private static void AdoptNodeRecursive(Node node, Document newOwner)
        {
            node._ownerDocument = newOwner;
            for (var child = node.FirstChild; child != null; child = child._nextSibling)
            {
                AdoptNodeRecursive(child, newOwner);
            }
        }

        // --- Mutation Observer Integration ---

        private RegisteredObserverList _registeredObservers;

        internal void RegisterObserver(MutationObserver observer, MutationObserverInit options)
        {
            _registeredObservers ??= new RegisteredObserverList();
            _registeredObservers.Add(observer, options);
        }

        internal void UnregisterObserver(MutationObserver observer)
        {
            _registeredObservers?.Remove(observer);
        }

        private void NotifyChildListMutation(Node removed, Node added)
        {
            var record = new MutationRecord
            {
                Type = MutationRecordType.ChildList,
                Target = this,
                RemovedNodes = removed != null ? new[] { removed } : null,
                AddedNodes = added != null ? new[] { added } : null
            };

            _registeredObservers?.NotifyChildList(record);

            // Notify static event for DevTools
            Node.NotifyMutation(this, "childList", null, null, 
                added != null ? new List<Node> { added } : null, 
                removed != null ? new List<Node> { removed } : null);

            // Propagate to ancestors with subtree observation
            PropagateToAncestors(record);
        }

        private void PropagateToAncestors(MutationRecord record)
        {
            for (var parent = _parentNode; parent != null; parent = parent._parentNode)
            {
                if (parent is ContainerNode container)
                    container._registeredObservers?.NotifySubtree(record);
            }
        }

        // --- Element Lookup Helpers ---

        /// <summary>
        /// Finds an element by ID using linear search.
        /// Document should override this to use the ID index for O(1) lookup.
        /// </summary>
        public virtual Element FindById(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            foreach (var node in Descendants())
            {
                if (node is Element el && string.Equals(el.Id, id, StringComparison.Ordinal))
                    return el;
            }
            return null;
        }

        /// <summary>
        /// Returns all elements with the given tag name.
        /// </summary>
        public virtual HTMLCollection GetElementsByTagName(string tagName)
        {
            if (string.IsNullOrEmpty(tagName))
                return HTMLCollection.Empty;

            bool isWildcard = tagName == "*";
            var results = new List<Element>();

            foreach (var node in Descendants())
            {
                if (node is Element el)
                {
                    if (isWildcard || string.Equals(el.LocalName, tagName, StringComparison.OrdinalIgnoreCase))
                        results.Add(el);
                }
            }

            return new StaticHTMLCollection(results);
        }

        /// <summary>
        /// Returns all elements with the given class name(s).
        /// Multiple class names can be space-separated.
        /// </summary>
        public virtual HTMLCollection GetElementsByClassName(string classNames)
        {
            if (string.IsNullOrWhiteSpace(classNames))
                return HTMLCollection.Empty;

            var classes = classNames.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (classes.Length == 0)
                return HTMLCollection.Empty;

            var results = new List<Element>();

            foreach (var node in Descendants())
            {
                if (node is Element el)
                {
                    bool hasAllClasses = true;
                    foreach (var cls in classes)
                    {
                        if (!el.ClassList.Contains(cls))
                        {
                            hasAllClasses = false;
                            break;
                        }
                    }
                    if (hasAllClasses)
                        results.Add(el);
                }
            }

            return new StaticHTMLCollection(results);
        }

        // --- Internal Child Enumeration ---

        internal int ChildCount => _children.Count;

        internal IEnumerable<Node> ChildrenEnumerable()
        {
            for (var child = FirstChild; child != null; child = child._nextSibling)
                yield return child;
        }

        // --- CharacterData Mutation Notification ---

        /// <summary>
        /// Called by CharacterData to notify observers of character data changes.
        /// </summary>
        internal void NotifyCharacterDataChange(MutationRecord record, bool isDirect)
        {
            if (_registeredObservers == null)
                return;

            if (isDirect)
            {
                // Direct parent - notify with characterData option
                _registeredObservers.NotifyCharacterData(record);
            }
            else
            {
                // Ancestor - notify with subtree option
                _registeredObservers.NotifySubtree(record);
            }
        }
    }

    /// <summary>
    /// Slot-based child node storage.
    /// Chromium pattern: 4 inline slots before heap allocation.
    /// </summary>
    internal struct ChildNodeStorage
    {
        // Inline slots (no heap for ≤4 children)
        private Node _slot0, _slot1, _slot2, _slot3;
        private List<Node> _overflow;
        private int _count;

        public int Count => _count;

        public Node First
        {
            get
            {
                if (_count == 0) return null;
                return _slot0;
            }
        }

        public Node Last
        {
            get
            {
                if (_count == 0) return null;
                if (_count <= 4)
                {
                    return _count switch
                    {
                        1 => _slot0,
                        2 => _slot1,
                        3 => _slot2,
                        4 => _slot3,
                        _ => null
                    };
                }
                return _overflow[_overflow.Count - 1];
            }
        }

        public void Append(Node node)
        {
            if (_count < 4)
            {
                SetSlot(_count, node);
                UpdateSiblings(node, _count);
            }
            else
            {
                EnsureOverflow();
                _overflow.Add(node);
                UpdateSiblings(node, _count);
            }
            _count++;
        }

        public void InsertBefore(Node node, Node before)
        {
            int index = IndexOf(before);
            if (index < 0)
                throw new InvalidOperationException("Reference node not found");

            // Shift everything after index
            if (_count < 4)
            {
                for (int i = _count; i > index; i--)
                    SetSlot(i, GetSlot(i - 1));
                SetSlot(index, node);
            }
            else
            {
                EnsureOverflow();
                if (index < 4)
                {
                    // Need to shift inline slots and push last to overflow
                    var last = _slot3;
                    for (int i = 3; i > index; i--)
                        SetSlot(i, GetSlot(i - 1));
                    SetSlot(index, node);
                    _overflow.Insert(0, last);
                }
                else
                {
                    _overflow.Insert(index - 4, node);
                }
            }
            _count++;
            RebuildSiblingLinks();
        }

        public void Remove(Node node)
        {
            int index = IndexOf(node);
            if (index < 0) return;

            // Clear sibling links
            node._previousSibling = null;
            node._nextSibling = null;

            // Shift elements
            if (_count <= 4)
            {
                for (int i = index; i < _count - 1; i++)
                    SetSlot(i, GetSlot(i + 1));
                SetSlot(_count - 1, null);
            }
            else if (index < 4)
            {
                // Shift inline slots
                for (int i = index; i < 3; i++)
                    SetSlot(i, GetSlot(i + 1));

                // Pull from overflow
                if (_overflow.Count > 0)
                {
                    _slot3 = _overflow[0];
                    _overflow.RemoveAt(0);
                }
                else
                {
                    _slot3 = null;
                }
            }
            else
            {
                _overflow.RemoveAt(index - 4);
            }

            _count--;
            RebuildSiblingLinks();
        }

        public int IndexOf(Node node)
        {
            if (_count <= 4)
            {
                if (ReferenceEquals(_slot0, node)) return 0;
                if (ReferenceEquals(_slot1, node)) return 1;
                if (ReferenceEquals(_slot2, node)) return 2;
                if (ReferenceEquals(_slot3, node)) return 3;
                return -1;
            }

            if (ReferenceEquals(_slot0, node)) return 0;
            if (ReferenceEquals(_slot1, node)) return 1;
            if (ReferenceEquals(_slot2, node)) return 2;
            if (ReferenceEquals(_slot3, node)) return 3;

            for (int i = 0; i < _overflow.Count; i++)
            {
                if (ReferenceEquals(_overflow[i], node))
                    return i + 4;
            }
            return -1;
        }

        public Node GetAt(int index)
        {
            if (index < 0 || index >= _count)
                return null;
            return GetSlot(index);
        }

        private Node GetSlot(int index)
        {
            if (index < 4)
            {
                return index switch
                {
                    0 => _slot0,
                    1 => _slot1,
                    2 => _slot2,
                    3 => _slot3,
                    _ => null
                };
            }
            return _overflow?[index - 4];
        }

        private void SetSlot(int index, Node node)
        {
            if (index < 4)
            {
                switch (index)
                {
                    case 0: _slot0 = node; break;
                    case 1: _slot1 = node; break;
                    case 2: _slot2 = node; break;
                    case 3: _slot3 = node; break;
                }
            }
            else
            {
                EnsureOverflow();
                var overflowIndex = index - 4;
                while (_overflow.Count <= overflowIndex)
                    _overflow.Add(null);
                _overflow[overflowIndex] = node;
            }
        }

        private void EnsureOverflow()
        {
            _overflow ??= new List<Node>(4);
        }

        private void UpdateSiblings(Node node, int index)
        {
            // Set previous sibling
            if (index > 0)
            {
                var prev = GetSlot(index - 1);
                node._previousSibling = prev;
                if (prev != null)
                    prev._nextSibling = node;
            }
            else
            {
                node._previousSibling = null;
            }

            // No next sibling (appending)
            node._nextSibling = null;
        }

        private void RebuildSiblingLinks()
        {
            Node prev = null;
            for (int i = 0; i < _count; i++)
            {
                var curr = GetSlot(i);
                if (curr == null) continue;

                curr._previousSibling = prev;
                if (prev != null)
                    prev._nextSibling = curr;
                curr._nextSibling = null;
                prev = curr;
            }
        }
    }
}
