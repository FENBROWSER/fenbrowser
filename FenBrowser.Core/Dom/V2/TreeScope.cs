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
        private Dictionary<string, IdBucket> _idIndex;
        private bool _idIndexInitialized;
        private long _treeOrderGeneration;
        private Dictionary<string, List<Element>> _nameIndex;

        private sealed class IdBucket
        {
            public List<Element> Elements { get; } = new();
            public Element First { get; set; }
            public long ResolvedGeneration { get; set; } = -1;
        }

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

            EnsureIdIndex();
            if (!_idIndex.TryGetValue(id, out var bucket) || bucket.Elements.Count == 0)
                return null;
            if (bucket.Elements.Count == 1)
                return bucket.Elements[0];
            if (bucket.ResolvedGeneration != _treeOrderGeneration)
                ResolveFirstInTreeOrder(id, bucket);
            return bucket.First;
        }

        /// <summary>
        /// Registers an element's ID in this tree scope.
        /// </summary>
        public void RegisterId(string id, Element element)
        {
            if (string.IsNullOrEmpty(id) || element == null)
                return;

            if (!_idIndexInitialized)
                return;

            if (!_idIndex.TryGetValue(id, out var bucket))
            {
                bucket = new IdBucket();
                _idIndex.Add(id, bucket);
            }

            if (bucket.Elements.Contains(element))
                return;

            bucket.Elements.Add(element);
            bucket.ResolvedGeneration = -1;
        }

        /// <summary>
        /// Unregisters an element's ID from this tree scope.
        /// </summary>
        public void UnregisterId(string id, Element element = null)
        {
            if (string.IsNullOrEmpty(id) || !_idIndexInitialized || _idIndex == null)
                return;

            if (!_idIndex.TryGetValue(id, out var bucket))
                return;

            if (element == null)
            {
                _idIndex.Remove(id);
                return;
            }

            bucket.Elements.Remove(element);
            if (bucket.Elements.Count == 0)
                _idIndex.Remove(id);
            else
                bucket.ResolvedGeneration = -1;
        }

        /// <summary>
        /// Marks the ID index as dirty (needs rebuild on next access).
        /// </summary>
        public void InvalidateIdIndex()
        {
            _treeOrderGeneration++;
        }

        internal int FullRebuildCount { get; private set; }

        /// <summary>
        /// HTML 7.2.2.3: the elements whose name attribute makes them named objects of a
        /// Window - embed, form, img and object in the HTML namespace - plus the frame
        /// containers, whose name is their child navigable's target name.
        /// </summary>
        internal static bool IsNameExposed(Element element) =>
            element?.NamespaceUri == Namespaces.Html &&
            element.LocalName is "embed" or "form" or "img" or "object" or "iframe" or "frame";

        private static bool IsFrameContainer(Element element) => element.LocalName is "iframe" or "frame";

        /// <summary>
        /// The first iframe or frame in tree order whose name is <paramref name="name"/>: the
        /// container of the document-tree child navigable with that target name.
        /// </summary>
        public Element GetNamedFrameContainer(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            EnsureNameIndex();
            if (!_nameIndex.TryGetValue(name, out var named))
                return null;

            Element first = null;
            foreach (var element in named)
            {
                if (!IsFrameContainer(element))
                    continue;
                if (first == null ||
                    (element.CompareDocumentPosition(first) & DocumentPosition.Following) != 0)
                    first = element;
            }

            return first;
        }

        public void RegisterName(string name, Element element)
        {
            if (string.IsNullOrEmpty(name) || _nameIndex == null || !IsNameExposed(element))
                return;
            if (!_nameIndex.TryGetValue(name, out var list))
                _nameIndex[name] = list = new List<Element>();
            if (!list.Contains(element))
                list.Add(element);
        }

        public void UnregisterName(string name, Element element)
        {
            if (string.IsNullOrEmpty(name) || _nameIndex == null || !_nameIndex.TryGetValue(name, out var list))
                return;
            list.Remove(element);
            if (list.Count == 0)
                _nameIndex.Remove(name);
        }

        /// <summary>
        /// HTML 7.2.2.3 "named objects" of a Window with name <paramref name="name"/>, in
        /// tree order: HTML embed, form, img and object elements with that name, and
        /// elements of any namespace with that id. Empty when there are none.
        /// </summary>
        public IReadOnlyList<Element> GetNamedObjects(string name)
        {
            if (string.IsNullOrEmpty(name))
                return Array.Empty<Element>();

            EnsureIdIndex();
            EnsureNameIndex();
            List<Element> result = null;
            if (_idIndex.TryGetValue(name, out var bucket))
            {
                result = new List<Element>(bucket.Elements);
            }

            if (_nameIndex.TryGetValue(name, out var named))
            {
                foreach (var element in named)
                {
                    if (!IsFrameContainer(element) && (result == null || !result.Contains(element)))
                        (result ??= new List<Element>()).Add(element);
                }
            }

            if (result == null)
                return Array.Empty<Element>();
            if (result.Count > 1)
            {
                // Rare: put them in tree order by walking the tree once.
                var members = new HashSet<Element>(result);
                result.Clear();
                foreach (var node in _root.Descendants())
                {
                    if (node is Element element && members.Contains(element))
                        result.Add(element);
                }
            }

            return result;
        }

        private void EnsureNameIndex()
        {
            if (_nameIndex != null)
                return;

            _nameIndex = new Dictionary<string, List<Element>>(StringComparer.Ordinal);
            foreach (var node in _root.Descendants())
            {
                if (node is Element element && IsNameExposed(element))
                {
                    var name = element.GetAttributeNS(null, "name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        if (!_nameIndex.TryGetValue(name, out var list))
                            _nameIndex[name] = list = new List<Element>();
                        list.Add(element);
                    }
                }
            }
        }

        private void EnsureIdIndex()
        {
            if (_idIndexInitialized)
                return;

            _idIndex ??= new Dictionary<string, IdBucket>(StringComparer.Ordinal);
            _idIndex.Clear();

            if (_root is Element rootElement && !string.IsNullOrEmpty(rootElement.Id))
            {
                AddInitial(rootElement.Id, rootElement);
            }

            foreach (var node in _root.Descendants())
            {
                if (node is Element el)
                {
                    var id = el.Id;
                    if (!string.IsNullOrEmpty(id))
                        AddInitial(id, el);
                }
            }

            _idIndexInitialized = true;
            FullRebuildCount++;
        }

        private void AddInitial(string id, Element element)
        {
            if (!_idIndex.TryGetValue(id, out var bucket))
            {
                bucket = new IdBucket
                {
                    First = element,
                    ResolvedGeneration = _treeOrderGeneration
                };
                _idIndex.Add(id, bucket);
            }
            bucket.Elements.Add(element);
        }

        private void ResolveFirstInTreeOrder(string id, IdBucket bucket)
        {
            bucket.First = null;
            if (_root is Element rootElement && string.Equals(rootElement.Id, id, StringComparison.Ordinal))
            {
                bucket.First = rootElement;
            }
            else
            {
                foreach (var node in _root.Descendants())
                {
                    if (node is Element element && string.Equals(element.Id, id, StringComparison.Ordinal))
                    {
                        bucket.First = element;
                        break;
                    }
                }
            }
            bucket.ResolvedGeneration = _treeOrderGeneration;
        }
    }
}
