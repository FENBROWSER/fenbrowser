// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// Represents a tree scope in the DOM.
    /// Each Document and ShadowRoot has its own TreeScope.
    /// Used for shadow DOM isolation and getElementById scoping.
    /// </summary>
    internal class TreeScope
    {
        private readonly ContainerNode _root;
        private Dictionary<string, Element> _idIndex;
        private bool _idIndexDirty = true;

        public TreeScope(ContainerNode root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        /// <summary>
        /// The root node of this tree scope (Document or ShadowRoot).
        /// </summary>
        public ContainerNode Root => _root;

        /// <summary>
        /// The owning document of this tree scope.
        /// </summary>
        public Document OwnerDocument
        {
            get
            {
                if (_root is Document doc)
                    return doc;
                if (_root is ShadowRoot shadow)
                    return shadow.Host?._ownerDocument;
                return null;
            }
        }

        /// <summary>
        /// Gets an element by ID within this tree scope.
        /// </summary>
        public Element GetElementById(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            // Use index if available and clean
            if (_idIndex != null && !_idIndexDirty)
            {
                _idIndex.TryGetValue(id, out var element);
                return element;
            }

            // Rebuild index if needed
            RebuildIdIndex();
            _idIndex.TryGetValue(id, out var result);
            return result;
        }

        /// <summary>
        /// Registers an element's ID in this tree scope.
        /// </summary>
        public void RegisterId(string id, Element element)
        {
            if (string.IsNullOrEmpty(id) || element == null)
                return;

            if (_idIndexDirty)
                return;

            _idIndex ??= new Dictionary<string, Element>(StringComparer.Ordinal);

            // First element wins (per spec)
            if (!_idIndex.ContainsKey(id))
                _idIndex[id] = element;
        }

        /// <summary>
        /// Unregisters an element's ID from this tree scope.
        /// </summary>
        public void UnregisterId(string id, Element element = null)
        {
            if (string.IsNullOrEmpty(id) || _idIndex == null)
                return;

            if (!_idIndex.TryGetValue(id, out var existing))
                return;

            if (element == null || ReferenceEquals(existing, element))
            {
                _idIndexDirty = true;
            }
        }

        /// <summary>
        /// Marks the ID index as dirty (needs rebuild on next access).
        /// </summary>
        public void InvalidateIdIndex()
        {
            _idIndexDirty = true;
        }

        private void RebuildIdIndex()
        {
            _idIndex ??= new Dictionary<string, Element>(StringComparer.Ordinal);
            _idIndex.Clear();

            if (_root is Element rootElement && !string.IsNullOrEmpty(rootElement.Id))
            {
                _idIndex[rootElement.Id] = rootElement;
            }

            foreach (var node in _root.Descendants())
            {
                if (node is Element el)
                {
                    var id = el.Id;
                    if (!string.IsNullOrEmpty(id) && !_idIndex.ContainsKey(id))
                        _idIndex[id] = el;
                }
            }

            _idIndexDirty = false;
        }
    }
}
