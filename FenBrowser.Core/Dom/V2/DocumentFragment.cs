// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

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

        /// <summary>
        /// Returns the first element with the given ID within this fragment, in tree order.
        /// https://dom.spec.whatwg.org/#dom-nonelementparentnode-getelementbyid
        /// </summary>
        public Element GetElementById(string elementId)
        {
            if (string.IsNullOrEmpty(elementId))
                return null;

            // Do not retain a fragment-local ID dictionary here. Child-list mutations
            // can invalidate such a cache, but an element's `id` can change without
            // changing the fragment's child list or tree scope. A stale dictionary
            // would then return the old element indefinitely. DocumentFragments are
            // transient and getElementById is not a layout hot path, so a tree-order
            // scan is both correct and avoids cross-cutting attribute invalidation.
            foreach (var node in Descendants())
            {
                if (node is Element element &&
                    string.Equals(element.Id, elementId, System.StringComparison.Ordinal))
                {
                    return element;
                }
            }

            return null;
        }

        /// <summary>
        /// Retained for mutation-call-site compatibility. ID lookup is live and has
        /// no fragment-local cache to invalidate.
        /// </summary>
        internal void InvalidateIdIndex()
        {
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
