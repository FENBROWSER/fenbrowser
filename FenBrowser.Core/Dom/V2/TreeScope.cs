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
