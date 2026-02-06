// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: DocumentType interface.
    /// https://dom.spec.whatwg.org/#interface-documenttype
    ///
    /// Represents a DOCTYPE declaration.
    /// This node CANNOT have children.
    /// </summary>
    public sealed class DocumentType : Node, IChildNode
    {
        public override NodeType NodeType => NodeType.DocumentType;
        public override string NodeName => Name;

        /// <summary>
        /// The name of the doctype (e.g., "html").
        /// https://dom.spec.whatwg.org/#dom-documenttype-name
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The public identifier (e.g., "-//W3C//DTD HTML 4.01//EN").
        /// https://dom.spec.whatwg.org/#dom-documenttype-publicid
        /// </summary>
        public string PublicId { get; }

        /// <summary>
        /// The system identifier (e.g., "http://www.w3.org/TR/html4/strict.dtd").
        /// https://dom.spec.whatwg.org/#dom-documenttype-systemid
        /// </summary>
        public string SystemId { get; }

        /// <summary>
        /// Creates a new DocumentType node.
        /// </summary>
        public DocumentType(string name, string publicId = "", string systemId = "", Document owner = null)
        {
            Name = name ?? "html";
            PublicId = publicId ?? "";
            SystemId = systemId ?? "";
            _ownerDocument = owner;
            _flags |= NodeFlags.IsDocumentType;
        }

        // --- IChildNode Implementation ---

        /// <summary>
        /// Removes this node from its parent.
        /// </summary>
        public void Remove()
        {
            if (_parentNode is ContainerNode parent)
                parent.RemoveChild(this);
        }

        /// <summary>
        /// Inserts nodes before this node.
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
        /// </summary>
        public void ReplaceWith(params Node[] nodes)
        {
            if (_parentNode is not ContainerNode parent)
                return;

            var refNode = _nextSibling;
            parent.RemoveChild(this);

            foreach (var node in nodes)
            {
                if (refNode != null)
                    parent.InsertBefore(node, refNode);
                else
                    parent.AppendChild(node);
            }
        }

        // --- Cloning ---

        public override Node CloneNode(bool deep = false)
        {
            return new DocumentType(Name, PublicId, SystemId, _ownerDocument);
        }

        // --- Equality ---

        public override bool IsEqualNode(Node other)
        {
            if (other is not DocumentType otherDocType)
                return false;
            return Name == otherDocType.Name &&
                   PublicId == otherDocType.PublicId &&
                   SystemId == otherDocType.SystemId;
        }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(PublicId) && string.IsNullOrEmpty(SystemId))
                return $"<!DOCTYPE {Name}>";
            if (!string.IsNullOrEmpty(PublicId) && !string.IsNullOrEmpty(SystemId))
                return $"<!DOCTYPE {Name} PUBLIC \"{PublicId}\" \"{SystemId}\">";
            if (!string.IsNullOrEmpty(PublicId))
                return $"<!DOCTYPE {Name} PUBLIC \"{PublicId}\">";
            return $"<!DOCTYPE {Name} SYSTEM \"{SystemId}\">";
        }
    }
}
