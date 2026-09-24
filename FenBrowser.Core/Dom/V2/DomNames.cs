// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// The name rules of DOM 1.4 "Namespaces" and the element/attribute/doctype
    /// name definitions. They replaced the XML Name production: a name need only
    /// avoid the characters that would break markup, so "f}oo" is a valid element
    /// name and a name may start with a combining mark.
    /// </summary>
    public static class DomNames
    {
        private static bool IsAsciiWhitespace(char c) =>
            c is '\t' or '\n' or '\f' or '\r' or ' ';

        private static bool IsAsciiAlpha(char c) =>
            c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

        private static bool IsAsciiAlphanumeric(char c) =>
            IsAsciiAlpha(c) || c is >= '0' and <= '9';

        /// <summary>DOM "valid element local name".</summary>
        public static bool IsValidElementLocalName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (IsAsciiAlpha(name[0]))
            {
                foreach (var c in name)
                {
                    if (IsAsciiWhitespace(c) || c is '\0' or '/' or '>')
                        return false;
                }

                return true;
            }

            // Otherwise it starts with ":", "_" or a non-ASCII code point, and carries
            // on with ASCII alphanumerics, "-", ".", ":", "_" or non-ASCII code points.
            // A surrogate half is a code unit >= 0x80, which is what the spec's
            // code point test comes to for UTF-16.
            if (!(name[0] is ':' or '_' || name[0] >= 0x80))
                return false;

            for (int i = 1; i < name.Length; i++)
            {
                var c = name[i];
                if (!(IsAsciiAlphanumeric(c) || c is '-' or '.' or ':' or '_' || c >= 0x80))
                    return false;
            }

            return true;
        }

        /// <summary>DOM "valid attribute local name".</summary>
        public static bool IsValidAttributeLocalName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            foreach (var c in name)
            {
                if (IsAsciiWhitespace(c) || c is '\0' or '/' or '=' or '>')
                    return false;
            }

            return true;
        }

        /// <summary>DOM "valid namespace prefix".</summary>
        public static bool IsValidNamespacePrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return false;

            foreach (var c in prefix)
            {
                if (IsAsciiWhitespace(c) || c is '\0' or '/' or '>')
                    return false;
            }

            return true;
        }

        /// <summary>DOM "valid doctype name": no ASCII whitespace, NULL or "&gt;". May be empty.</summary>
        public static bool IsValidDoctypeName(string name)
        {
            foreach (var c in name ?? string.Empty)
            {
                if (IsAsciiWhitespace(c) || c is '\0' or '>')
                    return false;
            }

            return true;
        }

        /// <summary>
        /// DOM "validate and extract" for createElementNS (<paramref name="forElement"/>)
        /// and the attribute methods: "" is the null namespace, the name splits at its
        /// first colon, and the prefix must agree with the namespace.
        /// </summary>
        public static (string NamespaceUri, string Prefix, string LocalName) ValidateAndExtract(
            string namespaceUri, string qualifiedName, bool forElement)
        {
            if (string.IsNullOrEmpty(namespaceUri))
                namespaceUri = null;
            qualifiedName ??= string.Empty;

            string prefix = null;
            var localName = qualifiedName;
            int colon = qualifiedName.IndexOf(':');
            if (colon >= 0)
            {
                prefix = qualifiedName.Substring(0, colon);
                localName = qualifiedName.Substring(colon + 1);
                if (!IsValidNamespacePrefix(prefix))
                    throw new DomException("InvalidCharacterError", $"'{prefix}' is not a valid namespace prefix");
            }

            if (forElement ? !IsValidElementLocalName(localName) : !IsValidAttributeLocalName(localName))
                throw new DomException("InvalidCharacterError", $"'{qualifiedName}' is not a valid qualified name");

            if (prefix != null && namespaceUri == null)
                throw new DomException("NamespaceError", "A prefixed name needs a namespace");
            if (prefix == "xml" && namespaceUri != Namespaces.Xml)
                throw new DomException("NamespaceError", "The xml prefix needs the XML namespace");
            if ((qualifiedName == "xmlns" || prefix == "xmlns") && namespaceUri != Namespaces.Xmlns)
                throw new DomException("NamespaceError", "xmlns names need the XMLNS namespace");
            if (namespaceUri == Namespaces.Xmlns && qualifiedName != "xmlns" && prefix != "xmlns")
                throw new DomException("NamespaceError", "The XMLNS namespace needs an xmlns name");

            return (namespaceUri, prefix, localName);
        }
    }
}
