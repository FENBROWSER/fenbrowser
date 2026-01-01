// =============================================================================
// Attr.cs
// DOM Attr Interface Implementation
// 
// SPEC REFERENCE: DOM Living Standard §4.9 - Attr
//                 https://dom.spec.whatwg.org/#attr
// 
// PURPOSE: Represents an attribute on an Element with proper DOM semantics.
// 
// STATUS: ✅ Implemented for Phase 2
// =============================================================================

using System;

namespace FenBrowser.Core.Dom
{
    /// <summary>
    /// Represents an attribute on an Element per DOM Living Standard §4.9.
    /// </summary>
    public class Attr : Node
    {
        /// <summary>
        /// The qualified name of the attribute.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The namespace URI of the attribute (null for non-namespaced attributes).
        /// </summary>
        public string NamespaceURI { get; }

        /// <summary>
        /// The namespace prefix of the attribute (null if not present).
        /// </summary>
        public string Prefix { get; }

        /// <summary>
        /// The local name of the attribute (without prefix).
        /// </summary>
        public string LocalName { get; }

        /// <summary>
        /// The element this attribute is attached to (null if orphaned).
        /// </summary>
        public Element OwnerElement { get; internal set; }

        /// <summary>
        /// The value of the attribute.
        /// </summary>
        public string Value { get; set; }

        /// <summary>
        /// Whether this attribute was specified in the source document.
        /// For DOM 3 compatibility.
        /// </summary>
        public bool Specified => true;

        // Node overrides
        public override NodeType NodeType => NodeType.Attribute;
        public override string NodeName => Name;
        public override string NodeValue
        {
            get => Value;
            set => Value = value;
        }

        /// <summary>
        /// Create a new attribute with the given name and value.
        /// </summary>
        public Attr(string name, string value = "")
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Attribute name cannot be null or empty", nameof(name));

            Name = name;
            Value = value ?? "";
            LocalName = ExtractLocalName(name);
            Prefix = ExtractPrefix(name);
            NamespaceURI = null; // Non-namespaced by default
        }

        /// <summary>
        /// Create a namespaced attribute.
        /// </summary>
        public Attr(string namespaceUri, string qualifiedName, string value = "")
        {
            if (string.IsNullOrEmpty(qualifiedName))
                throw new ArgumentException("Qualified name cannot be null or empty", nameof(qualifiedName));

            Name = qualifiedName;
            Value = value ?? "";
            NamespaceURI = namespaceUri;
            LocalName = ExtractLocalName(qualifiedName);
            Prefix = ExtractPrefix(qualifiedName);
        }

        private static string ExtractLocalName(string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName)) return "";
            int colonIndex = qualifiedName.IndexOf(':');
            return colonIndex >= 0 ? qualifiedName.Substring(colonIndex + 1) : qualifiedName;
        }

        private static string ExtractPrefix(string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName)) return null;
            int colonIndex = qualifiedName.IndexOf(':');
            return colonIndex >= 0 ? qualifiedName.Substring(0, colonIndex) : null;
        }

        public override string ToString()
        {
            return $"{Name}=\"{Value}\"";
        }

        public override Node CloneNode(bool deep)
        {
            if (NamespaceURI != null)
                return new Attr(NamespaceURI, Name, Value);
            return new Attr(Name, Value);
        }
    }
}
