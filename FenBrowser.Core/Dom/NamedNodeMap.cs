// =============================================================================
// NamedNodeMap.cs
// DOM NamedNodeMap Interface Implementation
// 
// SPEC REFERENCE: DOM Living Standard §4.10 - NamedNodeMap
//                 https://dom.spec.whatwg.org/#namednodemap
// 
// PURPOSE: Represents a collection of Attr objects as required by the DOM spec.
// 
// STATUS: ✅ Implemented for Phase 2.1
// =============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.Core.Dom
{
    /// <summary>
    /// Represents a collection of Attr objects per DOM Living Standard §4.10.
    /// Provides both indexed and named access to attributes.
    /// </summary>
    public class NamedNodeMap : IEnumerable<Attr>
    {
        private readonly List<Attr> _attributes = new List<Attr>();
        private readonly Element _ownerElement;

        public NamedNodeMap(Element owner)
        {
            _ownerElement = owner;
        }

        /// <summary>
        /// Number of attributes in the map.
        /// </summary>
        public int Length => _attributes.Count;

        /// <summary>
        /// Get attribute by index.
        /// </summary>
        public Attr this[int index]
        {
            get => (index >= 0 && index < _attributes.Count) ? _attributes[index] : null;
        }

        /// <summary>
        /// Get attribute by name (case-insensitive for HTML).
        /// </summary>
        public Attr this[string name]
        {
            get => GetNamedItem(name);
        }

        /// <summary>
        /// Get attribute by qualified name.
        /// </summary>
        public Attr GetNamedItem(string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName)) return null;
            return _attributes.FirstOrDefault(a => 
                string.Equals(a.Name, qualifiedName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get attribute by namespace and local name.
        /// </summary>
        public Attr GetNamedItemNS(string namespaceUri, string localName)
        {
            if (string.IsNullOrEmpty(localName)) return null;
            return _attributes.FirstOrDefault(a =>
                string.Equals(a.NamespaceURI, namespaceUri, StringComparison.Ordinal) &&
                string.Equals(a.LocalName, localName, StringComparison.Ordinal));
        }

        /// <summary>
        /// Set named attribute. Returns the old attribute if replaced, null otherwise.
        /// </summary>
        public Attr SetNamedItem(Attr attr)
        {
            if (attr == null) throw new ArgumentNullException(nameof(attr));

            var existing = GetNamedItem(attr.Name);
            if (existing != null)
            {
                int idx = _attributes.IndexOf(existing);
                _attributes[idx] = attr;
                existing.OwnerElement = null;
            }
            else
            {
                _attributes.Add(attr);
            }
            attr.OwnerElement = _ownerElement;
            return existing;
        }

        /// <summary>
        /// Set namespaced attribute. Returns the old attribute if replaced.
        /// </summary>
        public Attr SetNamedItemNS(Attr attr)
        {
            if (attr == null) throw new ArgumentNullException(nameof(attr));

            var existing = GetNamedItemNS(attr.NamespaceURI, attr.LocalName);
            if (existing != null)
            {
                int idx = _attributes.IndexOf(existing);
                _attributes[idx] = attr;
                existing.OwnerElement = null;
            }
            else
            {
                _attributes.Add(attr);
            }
            attr.OwnerElement = _ownerElement;
            return existing;
        }

        /// <summary>
        /// Remove attribute by name. Returns the removed attribute.
        /// </summary>
        public Attr RemoveNamedItem(string qualifiedName)
        {
            var attr = GetNamedItem(qualifiedName);
            if (attr == null)
                throw new DomException("NotFoundError", $"Attribute '{qualifiedName}' not found");
            _attributes.Remove(attr);
            attr.OwnerElement = null;
            return attr;
        }

        /// <summary>
        /// Remove attribute by namespace and local name.
        /// </summary>
        public Attr RemoveNamedItemNS(string namespaceUri, string localName)
        {
            var attr = GetNamedItemNS(namespaceUri, localName);
            if (attr == null)
                throw new DomException("NotFoundError", $"Attribute '{localName}' not found");
            _attributes.Remove(attr);
            attr.OwnerElement = null;
            return attr;
        }

        /// <summary>
        /// Check if an attribute exists by name.
        /// </summary>
        public bool Contains(string qualifiedName)
        {
            return GetNamedItem(qualifiedName) != null;
        }

        // Note: Item(int index) is implicitly provided by the int indexer this[int index]

        // Internal: Add attribute directly (used during parsing)
        internal void AddInternal(Attr attr)
        {
            if (attr == null) return;
            attr.OwnerElement = _ownerElement;
            _attributes.Add(attr);
        }

        // Internal: Clear all attributes
        internal void Clear()
        {
            foreach (var attr in _attributes)
                attr.OwnerElement = null;
            _attributes.Clear();
        }

        // IEnumerable implementation
        public IEnumerator<Attr> GetEnumerator() => _attributes.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// DOM Exception for NamedNodeMap operations.
    /// </summary>
    public class DomException : Exception
    {
        public string Name { get; }

        public DomException(string name, string message) : base(message)
        {
            Name = name;
        }
    }
}
