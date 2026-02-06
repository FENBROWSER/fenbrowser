// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: Attr interface.
    /// https://dom.spec.whatwg.org/#interface-attr
    ///
    /// IMPORTANT: In modern DOM (DOM4+), Attr is NOT a Node.
    /// It's a separate type representing an element attribute.
    /// </summary>
    public sealed class Attr
    {
        /// <summary>
        /// The namespace URI of this attribute (null for HTML attributes).
        /// https://dom.spec.whatwg.org/#dom-attr-namespaceuri
        /// </summary>
        public string NamespaceUri { get; }

        /// <summary>
        /// The namespace prefix (e.g., "xml" in "xml:lang").
        /// https://dom.spec.whatwg.org/#dom-attr-prefix
        /// </summary>
        public string Prefix { get; }

        /// <summary>
        /// The local name of the attribute.
        /// https://dom.spec.whatwg.org/#dom-attr-localname
        /// </summary>
        public string LocalName { get; }

        /// <summary>
        /// The qualified name (prefix:localName or just localName).
        /// https://dom.spec.whatwg.org/#dom-attr-name
        /// </summary>
        public string Name { get; }

        private string _value;

        /// <summary>
        /// The attribute value.
        /// https://dom.spec.whatwg.org/#dom-attr-value
        /// </summary>
        public string Value
        {
            get => _value;
            set
            {
                var oldValue = _value;
                _value = value ?? "";

                if (oldValue != _value && OwnerElement != null)
                {
                    OwnerElement.OnAttributeValueChanged(this, oldValue);
                }
            }
        }

        /// <summary>
        /// The element this attribute belongs to.
        /// https://dom.spec.whatwg.org/#dom-attr-ownerelement
        /// </summary>
        public Element OwnerElement { get; internal set; }

        /// <summary>
        /// Whether this attribute was explicitly specified (always true).
        /// https://dom.spec.whatwg.org/#dom-attr-specified
        /// </summary>
        public bool Specified => true;

        /// <summary>
        /// Creates a new Attr with the given qualified name and value.
        /// </summary>
        public Attr(string qualifiedName, string value, Element owner = null)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                throw new ArgumentException("Attribute name cannot be null or empty", nameof(qualifiedName));

            Name = qualifiedName;
            LocalName = ExtractLocalName(qualifiedName);
            Prefix = ExtractPrefix(qualifiedName);
            _value = value ?? "";
            OwnerElement = owner;
        }

        /// <summary>
        /// Creates a new Attr with namespace information.
        /// </summary>
        public Attr(string namespaceUri, string qualifiedName, string value, Element owner = null)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                throw new ArgumentException("Attribute name cannot be null or empty", nameof(qualifiedName));

            NamespaceUri = namespaceUri;
            Name = qualifiedName;
            LocalName = ExtractLocalName(qualifiedName);
            Prefix = ExtractPrefix(qualifiedName);
            _value = value ?? "";
            OwnerElement = owner;
        }

        private static string ExtractLocalName(string qualifiedName)
        {
            int colon = qualifiedName.IndexOf(':');
            return colon >= 0 ? qualifiedName.Substring(colon + 1) : qualifiedName;
        }

        private static string ExtractPrefix(string qualifiedName)
        {
            int colon = qualifiedName.IndexOf(':');
            return colon >= 0 ? qualifiedName.Substring(0, colon) : null;
        }

        /// <summary>
        /// Creates a clone of this attribute (detached from any element).
        /// </summary>
        public Attr Clone()
        {
            return new Attr(NamespaceUri, Name, _value, null);
        }

        public override string ToString() => $"{Name}=\"{_value}\"";

        public override bool Equals(object obj)
        {
            if (obj is not Attr other)
                return false;
            return NamespaceUri == other.NamespaceUri &&
                   Name == other.Name &&
                   _value == other._value;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(NamespaceUri, Name, _value);
        }
    }
}
