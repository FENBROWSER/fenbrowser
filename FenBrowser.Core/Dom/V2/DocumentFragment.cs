// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: DocumentFragment interface.
    /// https://dom.spec.whatwg.org/#interface-documentfragment
    ///
    /// A lightweight document that can hold nodes.
    /// Used for batch DOM operations.
    /// </summary>
    public class DocumentFragment : ContainerNode, INonElementParentNode
    {
        public override NodeType NodeType => NodeType.DocumentFragment;
        public override string NodeName => "#document-fragment";

        // --- ID Index (for getElementById within fragment) ---
        private Dictionary<string, Element> _idIndex;

        /// <summary>
        /// Returns the element with the given ID within this fragment.
        /// https://dom.spec.whatwg.org/#dom-nonelementparentnode-getelementbyid
        /// </summary>
        public Element GetElementById(string elementId)
        {
            if (string.IsNullOrEmpty(elementId))
                return null;

            // Rebuild index lazily
            if (_idIndex == null)
            {
                _idIndex = new Dictionary<string, Element>(System.StringComparer.Ordinal);
                foreach (var node in Descendants())
                {
                    if (node is Element el && !string.IsNullOrEmpty(el.Id))
                    {
                        if (!_idIndex.ContainsKey(el.Id))
                            _idIndex[el.Id] = el;
                    }
                }
            }

            _idIndex.TryGetValue(elementId, out var element);
            return element;
        }

        /// <summary>
        /// Invalidates the ID index when children change.
        /// </summary>
        internal void InvalidateIdIndex()
        {
            _idIndex = null;
        }

        // --- Constructor ---

        public DocumentFragment(Document owner = null)
        {
            _ownerDocument = owner;
            _flags |= NodeFlags.IsDocumentFragment | NodeFlags.IsContainer;
        }

        // --- Cloning ---

        public override Node CloneNode(bool deep = false)
        {
            var fragment = new DocumentFragment(_ownerDocument);

            if (deep)
            {
                for (var child = FirstChild; child != null; child = child._nextSibling)
                    fragment.AppendChild(child.CloneNode(true));
            }

            return fragment;
        }

        public override string ToString() => "#document-fragment";
    }
}
