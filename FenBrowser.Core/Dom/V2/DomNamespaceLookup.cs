// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM §4.4 "locate a namespace" and "locate a namespace prefix", backing
    /// Node.lookupNamespaceURI, Node.lookupPrefix and Node.isDefaultNamespace.
    /// </summary>
    public static class DomNamespaceLookup
    {
        private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

        /// <summary>https://dom.spec.whatwg.org/#locate-a-namespace</summary>
        public static string LookupNamespaceUri(object node, string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                prefix = null;
            }

            switch (node)
            {
                case Element element:
                    if (element.NamespaceUri != null && string.Equals(element.Prefix, prefix, StringComparison.Ordinal))
                    {
                        return element.NamespaceUri;
                    }

                    var attributes = element.Attributes;
                    for (var i = 0; i < attributes.Length; i++)
                    {
                        var attr = attributes[i];
                        if (!string.Equals(attr.NamespaceUri, XmlnsNamespace, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var declaresPrefix = string.Equals(attr.Prefix, "xmlns", StringComparison.Ordinal) &&
                                             string.Equals(attr.LocalName, prefix, StringComparison.Ordinal);
                        var declaresDefault = prefix == null && attr.Prefix == null &&
                                              string.Equals(attr.LocalName, "xmlns", StringComparison.Ordinal);
                        if (declaresPrefix || declaresDefault)
                        {
                            return string.IsNullOrEmpty(attr.Value) ? null : attr.Value;
                        }
                    }

                    return element.ParentElement == null ? null : LookupNamespaceUri(element.ParentElement, prefix);
                case Document document:
                    return document.DocumentElement == null ? null : LookupNamespaceUri(document.DocumentElement, prefix);
                case DocumentType:
                case DocumentFragment:
                    return null;
                case Attr attribute:
                    return attribute.OwnerElement == null ? null : LookupNamespaceUri(attribute.OwnerElement, prefix);
                case Node other:
                    return other.ParentElement == null ? null : LookupNamespaceUri(other.ParentElement, prefix);
                default:
                    return null;
            }
        }

        /// <summary>https://dom.spec.whatwg.org/#dom-node-lookupprefix</summary>
        public static string LookupPrefix(object node, string namespaceUri)
        {
            if (string.IsNullOrEmpty(namespaceUri))
            {
                return null;
            }

            switch (node)
            {
                case Element element:
                    return LocatePrefix(element, namespaceUri);
                case Document document:
                    return document.DocumentElement == null ? null : LocatePrefix(document.DocumentElement, namespaceUri);
                case DocumentType:
                case DocumentFragment:
                    return null;
                case Attr attribute:
                    return attribute.OwnerElement == null ? null : LocatePrefix(attribute.OwnerElement, namespaceUri);
                case Node other:
                    return other.ParentElement == null ? null : LocatePrefix(other.ParentElement, namespaceUri);
                default:
                    return null;
            }
        }

        /// <summary>https://dom.spec.whatwg.org/#locate-a-namespace-prefix</summary>
        private static string LocatePrefix(Element element, string namespaceUri)
        {
            if (string.Equals(element.NamespaceUri, namespaceUri, StringComparison.Ordinal) && element.Prefix != null)
            {
                return element.Prefix;
            }

            var attributes = element.Attributes;
            for (var i = 0; i < attributes.Length; i++)
            {
                var attr = attributes[i];
                if (string.Equals(attr.Prefix, "xmlns", StringComparison.Ordinal) &&
                    string.Equals(attr.Value, namespaceUri, StringComparison.Ordinal))
                {
                    return attr.LocalName;
                }
            }

            return element.ParentElement == null ? null : LocatePrefix(element.ParentElement, namespaceUri);
        }
    }
}
