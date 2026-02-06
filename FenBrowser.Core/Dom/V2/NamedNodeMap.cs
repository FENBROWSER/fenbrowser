// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: NamedNodeMap interface.
    /// https://dom.spec.whatwg.org/#interface-namednodemap
    ///
    /// The SINGLE source of truth for element attributes.
    /// Uses slot-based storage for memory efficiency (4 inline slots).
    /// </summary>
    public sealed class NamedNodeMap : IEnumerable<Attr>
    {
        // Inline slots for 0-4 attributes (common case, no heap allocation)
        private Attr _slot0, _slot1, _slot2, _slot3;
        private List<Attr> _overflow;
        private int _count;

        private readonly Element _owner;

        /// <summary>
        /// Creates a NamedNodeMap for the given element.
        /// </summary>
        internal NamedNodeMap(Element owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        /// <summary>
        /// Returns the number of attributes.
        /// https://dom.spec.whatwg.org/#dom-namednodemap-length
        /// </summary>
        public int Length => _count;

        /// <summary>
        /// Returns the attribute at the specified index.
        /// https://dom.spec.whatwg.org/#dom-namednodemap-item
        /// </summary>
        /// <summary>
        /// Returns the attribute at the specified index.
        /// https://dom.spec.whatwg.org/#dom-namednodemap-item
        /// </summary>
        [System.Runtime.CompilerServices.IndexerName("ItemAt")]
        public Attr this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                    return null;
                return GetAtIndex(index);
            }
        }

        /// <summary>
        /// Returns the attribute at the specified index.
        /// </summary>
        public Attr Item(int index) => this[index];

        /// <summary>
        /// Returns the attribute with the specified name (case-insensitive for HTML).
        /// https://dom.spec.whatwg.org/#dom-namednodemap-getnameditem
        /// </summary>
        public Attr GetNamedItem(string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                return null;

            // HTML attributes are case-insensitive
            for (int i = 0; i < _count; i++)
            {
                var attr = GetAtIndex(i);
                if (string.Equals(attr.Name, qualifiedName, StringComparison.OrdinalIgnoreCase))
                    return attr;
            }
            return null;
        }

        /// <summary>
        /// Returns the attribute with the specified namespace and local name.
        /// https://dom.spec.whatwg.org/#dom-namednodemap-getnameditemns
        /// </summary>
        public Attr GetNamedItemNS(string namespaceUri, string localName)
        {
            if (string.IsNullOrEmpty(localName))
                return null;

            for (int i = 0; i < _count; i++)
            {
                var attr = GetAtIndex(i);
                if (attr.NamespaceUri == namespaceUri &&
                    string.Equals(attr.LocalName, localName, StringComparison.Ordinal))
                    return attr;
            }
            return null;
        }

        /// <summary>
        /// Sets an attribute, returning the old one if replaced.
        /// https://dom.spec.whatwg.org/#dom-namednodemap-setnameditem
        /// </summary>
        public Attr SetNamedItem(Attr attr)
        {
            if (attr == null)
                throw new ArgumentNullException(nameof(attr));

            if (attr.OwnerElement != null && attr.OwnerElement != _owner)
                throw new DomException("InUseAttributeError",
                    "Attribute is already in use by another element");

            // Check if attribute with same name exists
            var existing = GetNamedItem(attr.Name);
            if (existing != null)
            {
                int idx = IndexOf(existing);
                SetAtIndex(idx, attr);
                existing.OwnerElement = null;
            }
            else
            {
                Add(attr);
            }

            attr.OwnerElement = _owner;
            return existing;
        }

        /// <summary>
        /// Sets an attribute with namespace, returning the old one if replaced.
        /// https://dom.spec.whatwg.org/#dom-namednodemap-setnameditemns
        /// </summary>
        public Attr SetNamedItemNS(Attr attr)
        {
            if (attr == null)
                throw new ArgumentNullException(nameof(attr));

            if (attr.OwnerElement != null && attr.OwnerElement != _owner)
                throw new DomException("InUseAttributeError",
                    "Attribute is already in use by another element");

            // Check if attribute with same namespace and local name exists
            var existing = GetNamedItemNS(attr.NamespaceUri, attr.LocalName);
            if (existing != null)
            {
                int idx = IndexOf(existing);
                SetAtIndex(idx, attr);
                existing.OwnerElement = null;
            }
            else
            {
                Add(attr);
            }

            attr.OwnerElement = _owner;
            return existing;
        }

        /// <summary>
        /// Removes the attribute with the specified name.
        /// https://dom.spec.whatwg.org/#dom-namednodemap-removenameditem
        /// </summary>
        public Attr RemoveNamedItem(string qualifiedName)
        {
            var attr = GetNamedItem(qualifiedName);
            if (attr == null)
                throw new DomException("NotFoundError",
                    $"No attribute named '{qualifiedName}' exists");

            Remove(attr);
            attr.OwnerElement = null;
            return attr;
        }

        /// <summary>
        /// Removes the attribute with the specified namespace and local name.
        /// https://dom.spec.whatwg.org/#dom-namednodemap-removenameditemns
        /// </summary>
        public Attr RemoveNamedItemNS(string namespaceUri, string localName)
        {
            var attr = GetNamedItemNS(namespaceUri, localName);
            if (attr == null)
                throw new DomException("NotFoundError",
                    $"No attribute exists with namespace '{namespaceUri}' and local name '{localName}'");

            Remove(attr);
            attr.OwnerElement = null;
            return attr;
        }

        // --- Internal Operations ---

        internal void Add(Attr attr)
        {
            if (_count < 4)
            {
                SetAtIndex(_count, attr);
            }
            else
            {
                EnsureOverflow();
                _overflow.Add(attr);
            }
            _count++;
        }

        internal void Remove(Attr attr)
        {
            int idx = IndexOf(attr);
            if (idx < 0) return;

            // Shift elements
            if (_count <= 4)
            {
                for (int i = idx; i < _count - 1; i++)
                    SetAtIndex(i, GetAtIndex(i + 1));
                SetAtIndex(_count - 1, null);
            }
            else if (idx < 4)
            {
                // Shift inline slots
                for (int i = idx; i < 3; i++)
                    SetAtIndex(i, GetAtIndex(i + 1));

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
                _overflow.RemoveAt(idx - 4);
            }

            _count--;
        }

        internal int IndexOf(Attr attr)
        {
            for (int i = 0; i < _count; i++)
            {
                if (ReferenceEquals(GetAtIndex(i), attr))
                    return i;
            }
            return -1;
        }

        private Attr GetAtIndex(int index)
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

        private void SetAtIndex(int index, Attr attr)
        {
            if (index < 4)
            {
                switch (index)
                {
                    case 0: _slot0 = attr; break;
                    case 1: _slot1 = attr; break;
                    case 2: _slot2 = attr; break;
                    case 3: _slot3 = attr; break;
                }
            }
            else
            {
                EnsureOverflow();
                int overflowIdx = index - 4;
                while (_overflow.Count <= overflowIdx)
                    _overflow.Add(null);
                _overflow[overflowIdx] = attr;
            }
        }

        private void EnsureOverflow()
        {
            _overflow ??= new List<Attr>(4);
        }

        // --- Legacy Compatibility ---

        /// <summary>
        /// Returns a read-only dictionary view of attributes (for legacy compatibility).
        /// NOTE: This creates a new dictionary each call. Avoid in hot paths.
        /// </summary>
        public IReadOnlyDictionary<string, string> ToDictionary()
        {
            var dict = new Dictionary<string, string>(_count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _count; i++)
            {
                var attr = GetAtIndex(i);
                dict[attr.Name.ToLowerInvariant()] = attr.Value;
            }
            return dict;
        }

        /// <summary>
        /// Checks if an attribute with the given name exists.
        /// </summary>
        public bool Contains(string qualifiedName)
        {
            return GetNamedItem(qualifiedName) != null;
        }

        // --- IEnumerable Implementation ---

        public IEnumerator<Attr> GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
                yield return GetAtIndex(i);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString()
        {
            var parts = new List<string>(_count);
            for (int i = 0; i < _count; i++)
                parts.Add(GetAtIndex(i).ToString());
            return string.Join(" ", parts);
        }
    }
}
