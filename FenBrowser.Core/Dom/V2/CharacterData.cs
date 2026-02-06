// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: CharacterData interface.
    /// https://dom.spec.whatwg.org/#interface-characterdata
    ///
    /// Base class for Text, Comment, ProcessingInstruction.
    /// These nodes CANNOT have children per WHATWG spec.
    /// </summary>
    public abstract class CharacterData : Node, IChildNode, INonDocumentTypeChildNode
    {
        private string _data;

        /// <summary>
        /// Gets or sets the character data of this node.
        /// https://dom.spec.whatwg.org/#dom-characterdata-data
        /// </summary>
        public string Data
        {
            get => _data ?? "";
            set
            {
                var oldValue = _data;
                _data = value ?? "";

                if (oldValue != _data)
                {
                    OnDataChanged(oldValue);
                }
            }
        }

        /// <summary>
        /// Returns the number of code units in the data.
        /// https://dom.spec.whatwg.org/#dom-characterdata-length
        /// </summary>
        public int Length => _data?.Length ?? 0;

        /// <summary>
        /// NodeValue returns the character data.
        /// </summary>
        public override string NodeValue
        {
            get => Data;
            set => Data = value;
        }

        /// <summary>
        /// TextContent returns the character data.
        /// </summary>
        public override string TextContent
        {
            get => Data;
            set => Data = value;
        }

        public override string ToHtml()
        {
            if (NodeType == NodeType.Comment) return $"<!--{Data}-->";
            return System.Net.WebUtility.HtmlEncode(Data);
        }

        // --- CharacterData Methods ---

        /// <summary>
        /// Returns a substring of the data.
        /// https://dom.spec.whatwg.org/#dom-characterdata-substringdata
        /// </summary>
        public string SubstringData(int offset, int count)
        {
            var data = Data;
            if (offset < 0 || offset > data.Length)
                throw new DomException("IndexSizeError", "Offset is out of range");
            if (count < 0)
                throw new DomException("IndexSizeError", "Count cannot be negative");

            int actualCount = Math.Min(count, data.Length - offset);
            return data.Substring(offset, actualCount);
        }

        /// <summary>
        /// Appends data to the end.
        /// https://dom.spec.whatwg.org/#dom-characterdata-appenddata
        /// </summary>
        public void AppendData(string data)
        {
            Data += data ?? "";
        }

        /// <summary>
        /// Inserts data at the specified offset.
        /// https://dom.spec.whatwg.org/#dom-characterdata-insertdata
        /// </summary>
        public void InsertData(int offset, string data)
        {
            var current = Data;
            if (offset < 0 || offset > current.Length)
                throw new DomException("IndexSizeError", "Offset is out of range");

            Data = current.Insert(offset, data ?? "");
        }

        /// <summary>
        /// Deletes data at the specified range.
        /// https://dom.spec.whatwg.org/#dom-characterdata-deletedata
        /// </summary>
        public void DeleteData(int offset, int count)
        {
            var current = Data;
            if (offset < 0 || offset > current.Length)
                throw new DomException("IndexSizeError", "Offset is out of range");
            if (count < 0)
                throw new DomException("IndexSizeError", "Count cannot be negative");

            int actualCount = Math.Min(count, current.Length - offset);
            Data = current.Remove(offset, actualCount);
        }

        /// <summary>
        /// Replaces data at the specified range.
        /// https://dom.spec.whatwg.org/#dom-characterdata-replacedata
        /// </summary>
        public void ReplaceData(int offset, int count, string data)
        {
            var current = Data;
            if (offset < 0 || offset > current.Length)
                throw new DomException("IndexSizeError", "Offset is out of range");
            if (count < 0)
                throw new DomException("IndexSizeError", "Count cannot be negative");

            int actualCount = Math.Min(count, current.Length - offset);
            var before = current.Substring(0, offset);
            var after = current.Substring(offset + actualCount);
            Data = before + (data ?? "") + after;
        }

        // --- IChildNode Implementation ---

        /// <summary>
        /// Removes this node from its parent.
        /// https://dom.spec.whatwg.org/#dom-childnode-remove
        /// </summary>
        public void Remove()
        {
            if (_parentNode is ContainerNode parent)
                parent.RemoveChild(this);
        }

        /// <summary>
        /// Inserts nodes before this node.
        /// https://dom.spec.whatwg.org/#dom-childnode-before
        /// </summary>
        public void Before(params Node[] nodes)
        {
            if (_parentNode is not ContainerNode parent)
                return;

            foreach (var node in nodes)
            {
                parent.InsertBefore(node, this);
            }
        }

        /// <summary>
        /// Inserts nodes after this node.
        /// https://dom.spec.whatwg.org/#dom-childnode-after
        /// </summary>
        public void After(params Node[] nodes)
        {
            if (_parentNode is not ContainerNode parent)
                return;

            var refNode = _nextSibling;
            foreach (var node in nodes)
            {
                if (refNode != null)
                    parent.InsertBefore(node, refNode);
                else
                    parent.AppendChild(node);
            }
        }

        /// <summary>
        /// Replaces this node with other nodes.
        /// https://dom.spec.whatwg.org/#dom-childnode-replacewith
        /// </summary>
        public void ReplaceWith(params Node[] nodes)
        {
            if (_parentNode is not ContainerNode parent)
                return;

            var refNode = _nextSibling;

            // Remove self first
            parent.RemoveChild(this);

            // Insert new nodes at the old position
            foreach (var node in nodes)
            {
                if (refNode != null)
                    parent.InsertBefore(node, refNode);
                else
                    parent.AppendChild(node);
            }
        }

        // --- INonDocumentTypeChildNode Implementation ---

        /// <summary>
        /// Returns the previous sibling element.
        /// https://dom.spec.whatwg.org/#dom-nondocumenttypechildnode-previouselementsibling
        /// </summary>
        public Element PreviousElementSibling
        {
            get
            {
                for (var node = _previousSibling; node != null; node = node._previousSibling)
                {
                    if (node is Element el)
                        return el;
                }
                return null;
            }
        }

        /// <summary>
        /// Returns the next sibling element.
        /// https://dom.spec.whatwg.org/#dom-nondocumenttypechildnode-nextelementsibling
        /// </summary>
        public Element NextElementSibling
        {
            get
            {
                for (var node = _nextSibling; node != null; node = node._nextSibling)
                {
                    if (node is Element el)
                        return el;
                }
                return null;
            }
        }

        // --- Protected ---

        protected CharacterData(string data, Document owner = null)
        {
            _data = data ?? "";
            _ownerDocument = owner;
        }

        protected virtual void OnDataChanged(string oldValue)
        {
            // Mark the node as needing repaint
            MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);

            // Notify mutation observers
            NotifyCharacterDataMutation(oldValue);
        }

        /// <summary>
        /// Notifies mutation observers of character data changes.
        /// Propagates to parent's observers and ancestors with subtree option.
        /// </summary>
        private void NotifyCharacterDataMutation(string oldValue)
        {
            // Create the mutation record
            var record = new MutationRecord
            {
                Type = MutationRecordType.CharacterData,
                Target = this,
                OldValue = oldValue
            };

            // Notify observers registered on this node's ancestors
            // CharacterData nodes can't have their own observers, but their parent
            // ContainerNode can observe characterData on children
            for (var ancestor = _parentNode; ancestor != null; ancestor = ancestor._parentNode)
            {
                if (ancestor is ContainerNode container)
                {
                    // Get the registered observer list via reflection or internal accessor
                    // For first ancestor (direct parent), notify with characterData option
                    // For further ancestors, notify with subtree option
                    NotifyContainerObservers(container, record, ancestor == _parentNode);

                    // Only the first container needs direct notification,
                    // the rest get subtree notifications
                }
            }
        }

        /// <summary>
        /// Notifies a container node's observers about character data change.
        /// </summary>
        private static void NotifyContainerObservers(ContainerNode container, MutationRecord record, bool isDirect)
        {
            // Access the registered observers through the container's internal field
            // We need to use the internal method since _registeredObservers is private
            container.NotifyCharacterDataChange(record, isDirect);
        }
    }
}
