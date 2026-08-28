// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: CharacterData interface.
    /// Base class for Text, Comment, ProcessingInstruction.
    /// These nodes cannot have children.
    /// </summary>
    public abstract class CharacterData : Node, IChildNode, INonDocumentTypeChildNode
    {
        private string _data;

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

        public int Length => _data?.Length ?? 0;

        public override string NodeValue
        {
            get => Data;
            set => Data = value;
        }

        public override string TextContent
        {
            get => Data;
            set => Data = value;
        }

        public override string ToHtml()
        {
            if (NodeType == NodeType.Comment)
            {
                return $"<!--{Data}-->";
            }

            if (ParentNode is Element parent &&
                HtmlElementSemantics.IsRawTextSerializationParent(parent.LocalName, parent.NamespaceUri))
            {
                return Data;
            }

            return System.Net.WebUtility.HtmlEncode(Data);
        }

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

        public void AppendData(string data)
        {
            Data += data ?? "";
        }

        public void InsertData(int offset, string data)
        {
            var current = Data;
            if (offset < 0 || offset > current.Length)
                throw new DomException("IndexSizeError", "Offset is out of range");

            Data = current.Insert(offset, data ?? "");
        }

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

        public void Remove()
        {
            if (_parentNode is ContainerNode parent)
            {
                parent.RemoveChild(this);
            }
        }

        public void Before(params Node[] nodes)
        {
            if (_parentNode is not ContainerNode parent)
                return;

            foreach (var node in nodes)
            {
                parent.InsertBefore(node, this);
            }
        }

        public void After(params Node[] nodes)
        {
            if (_parentNode is not ContainerNode parent)
                return;

            var referenceNode = _nextSibling;
            foreach (var node in nodes)
            {
                if (referenceNode != null)
                {
                    parent.InsertBefore(node, referenceNode);
                }
                else
                {
                    parent.AppendChild(node);
                }
            }
        }

        public void ReplaceWith(params Node[] nodes)
        {
            if (_parentNode is not ContainerNode parent)
                return;

            var referenceNode = _nextSibling;
            parent.RemoveChild(this);

            foreach (var node in nodes)
            {
                if (referenceNode != null)
                {
                    parent.InsertBefore(node, referenceNode);
                }
                else
                {
                    parent.AppendChild(node);
                }
            }
        }

        public Element PreviousElementSibling
        {
            get
            {
                for (var node = _previousSibling;
                     node != null;
                     node = node._previousSibling)
                {
                    if (node is Element element)
                    {
                        return element;
                    }
                }

                return null;
            }
        }

        public Element NextElementSibling
        {
            get
            {
                for (var node = _nextSibling;
                     node != null;
                     node = node._nextSibling)
                {
                    if (node is Element element)
                    {
                        return element;
                    }
                }

                return null;
            }
        }

        protected CharacterData(string data, Document owner = null)
        {
            _data = data ?? "";
            _ownerDocument = owner;
        }

        protected virtual void OnDataChanged(string oldValue)
        {
            MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);
            NotifyCharacterDataMutation(oldValue);
        }

        private void NotifyCharacterDataMutation(string oldValue)
        {
            MutationRecord record =
                MutationObserver.NotifyCharacterDataTarget(this, oldValue);

            for (var ancestor = _parentNode;
                 ancestor != null;
                 ancestor = ancestor._parentNode)
            {
                if (ancestor is ContainerNode container)
                {
                    record = container.NotifyCharacterDataChange(
                        record,
                        this,
                        oldValue,
                        isDirect: false);
                }
            }
        }
    }
}
