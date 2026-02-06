// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: NodeList interface.
    /// https://dom.spec.whatwg.org/#interface-nodelist
    ///
    /// Abstract base for collections of nodes.
    /// </summary>
    public abstract class NodeList : IEnumerable<Node>
    {
        /// <summary>
        /// Returns the number of nodes in the collection.
        /// </summary>
        public abstract int Length { get; }

        /// <summary>
        /// Returns the node at the specified index.
        /// </summary>
        [System.Runtime.CompilerServices.IndexerName("ItemAt")]
        public abstract Node this[int index] { get; }

        /// <summary>
        /// Returns the node at the specified index (alias for indexer).
        /// </summary>
        public Node Item(int index) => this[index];

        /// <summary>
        /// Returns an enumerator for the nodes.
        /// </summary>
        public abstract IEnumerator<Node> GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Live NodeList that reflects current DOM state.
    /// Used by Node.childNodes.
    /// </summary>
    internal sealed class LiveChildNodeList : NodeList
    {
        private readonly ContainerNode _parent;

        public LiveChildNodeList(ContainerNode parent)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        }

        public override int Length => _parent.ChildCount;

        public override Node this[int index]
        {
            get
            {
                if (index < 0 || index >= Length)
                    return null;

                int i = 0;
                for (var child = _parent.FirstChild; child != null; child = child._nextSibling)
                {
                    if (i == index)
                        return child;
                    i++;
                }
                return null;
            }
        }

        public override IEnumerator<Node> GetEnumerator()
        {
            // Take a snapshot to handle modifications during iteration
            var snapshot = new List<Node>();
            for (var child = _parent.FirstChild; child != null; child = child._nextSibling)
                snapshot.Add(child);
            return snapshot.GetEnumerator();
        }
    }

    /// <summary>
    /// Live HTMLCollection of child elements only.
    /// Used by ParentNode.children.
    /// </summary>
    internal sealed class LiveElementChildList : NodeList
    {
        private readonly ContainerNode _parent;

        public LiveElementChildList(ContainerNode parent)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        }

        public override int Length => _parent.ChildElementCount;

        public override Node this[int index]
        {
            get
            {
                if (index < 0)
                    return null;

                int i = 0;
                for (var child = _parent.FirstChild; child != null; child = child._nextSibling)
                {
                    if (child is Element el)
                    {
                        if (i == index)
                            return el;
                        i++;
                    }
                }
                return null;
            }
        }

        public override IEnumerator<Node> GetEnumerator()
        {
            var snapshot = new List<Node>();
            for (var child = _parent.FirstChild; child != null; child = child._nextSibling)
            {
                if (child is Element)
                    snapshot.Add(child);
            }
            return snapshot.GetEnumerator();
        }
    }

    /// <summary>
    /// Static NodeList that does not reflect DOM changes.
    /// Used by querySelectorAll.
    /// </summary>
    internal sealed class StaticNodeList : NodeList
    {
        private readonly Node[] _nodes;

        public StaticNodeList(IEnumerable<Node> nodes)
        {
            _nodes = nodes is Node[] arr ? arr : new List<Node>(nodes).ToArray();
        }

        public StaticNodeList(List<Node> nodes)
        {
            _nodes = nodes.ToArray();
        }

        public override int Length => _nodes.Length;

        public override Node this[int index]
        {
            get
            {
                if (index < 0 || index >= _nodes.Length)
                    return null;
                return _nodes[index];
            }
        }

        public override IEnumerator<Node> GetEnumerator()
        {
            return ((IEnumerable<Node>)_nodes).GetEnumerator();
        }
    }

    /// <summary>
    /// Empty NodeList singleton.
    /// Used when a node has no children.
    /// </summary>
    internal sealed class EmptyNodeList : NodeList
    {
        public static readonly EmptyNodeList Instance = new EmptyNodeList();

        private EmptyNodeList() { }

        public override int Length => 0;
        public override Node this[int index] => null;

        public override IEnumerator<Node> GetEnumerator()
        {
            return ((IEnumerable<Node>)Array.Empty<Node>()).GetEnumerator();
        }
    }

    /// <summary>
    /// DOM Living Standard: HTMLCollection interface.
    /// https://dom.spec.whatwg.org/#interface-htmlcollection
    ///
    /// A live collection of elements (Element nodes only).
    /// </summary>
    public abstract class HTMLCollection : IEnumerable<Element>
    {
        /// <summary>
        /// Returns the number of elements in the collection.
        /// </summary>
        public abstract int Length { get; }

        /// <summary>
        /// Returns the element at the specified index.
        /// </summary>
        [System.Runtime.CompilerServices.IndexerName("ItemAt")]
        public abstract Element this[int index] { get; }

        /// <summary>
        /// Returns the element at the specified index.
        /// </summary>
        public Element Item(int index) => this[index];

        /// <summary>
        /// Returns the first element with the given ID or name.
        /// </summary>
        public abstract Element NamedItem(string name);

        public abstract IEnumerator<Element> GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Empty HTMLCollection singleton.
        /// </summary>
        public static readonly HTMLCollection Empty = new StaticHTMLCollection(Array.Empty<Element>());
    }

    /// <summary>
    /// Static (non-live) HTMLCollection.
    /// More efficient for one-time queries.
    /// </summary>
    internal sealed class StaticHTMLCollection : HTMLCollection
    {
        private readonly Element[] _elements;

        public StaticHTMLCollection(IEnumerable<Element> elements)
        {
            _elements = elements is Element[] arr ? arr : new List<Element>(elements).ToArray();
        }

        public StaticHTMLCollection(List<Element> elements)
        {
            _elements = elements.ToArray();
        }

        public override int Length => _elements.Length;

        public override Element this[int index]
        {
            get
            {
                if (index < 0 || index >= _elements.Length)
                    return null;
                return _elements[index];
            }
        }

        public override Element NamedItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var el in _elements)
            {
                if (el.Id == name || el.GetAttribute("name") == name)
                    return el;
            }
            return null;
        }

        public override IEnumerator<Element> GetEnumerator()
        {
            return ((IEnumerable<Element>)_elements).GetEnumerator();
        }
    }

    /// <summary>
    /// Live HTMLCollection of all descendant elements with a given tag name.
    /// </summary>
    internal sealed class TagNameHTMLCollection : HTMLCollection
    {
        private readonly ContainerNode _root;
        private readonly string _tagName;
        private readonly bool _matchAll;

        public TagNameHTMLCollection(ContainerNode root, string tagName)
        {
            _root = root;
            _tagName = tagName?.ToUpperInvariant() ?? "*";
            _matchAll = _tagName == "*";
        }

        public override int Length
        {
            get
            {
                int count = 0;
                foreach (var node in _root.Descendants())
                {
                    if (node is Element el && (_matchAll || el.TagName == _tagName))
                        count++;
                }
                return count;
            }
        }

        public override Element this[int index]
        {
            get
            {
                if (index < 0) return null;
                int i = 0;
                foreach (var node in _root.Descendants())
                {
                    if (node is Element el && (_matchAll || el.TagName == _tagName))
                    {
                        if (i == index) return el;
                        i++;
                    }
                }
                return null;
            }
        }

        public override Element NamedItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var node in _root.Descendants())
            {
                if (node is Element el && (_matchAll || el.TagName == _tagName))
                {
                    if (el.Id == name || el.GetAttribute("name") == name)
                        return el;
                }
            }
            return null;
        }

        public override IEnumerator<Element> GetEnumerator()
        {
            var snapshot = new List<Element>();
            foreach (var node in _root.Descendants())
            {
                if (node is Element el && (_matchAll || el.TagName == _tagName))
                    snapshot.Add(el);
            }
            return snapshot.GetEnumerator();
        }
    }

    /// <summary>
    /// Live HTMLCollection of all descendant elements with a given class name.
    /// </summary>
    internal sealed class ClassNameHTMLCollection : HTMLCollection
    {
        private readonly ContainerNode _root;
        private readonly string[] _classNames;

        public ClassNameHTMLCollection(ContainerNode root, string classNames)
        {
            _root = root;
            _classNames = (classNames ?? "")
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public override int Length
        {
            get
            {
                int count = 0;
                foreach (var el in MatchingElements())
                    count++;
                return count;
            }
        }

        public override Element this[int index]
        {
            get
            {
                if (index < 0) return null;
                int i = 0;
                foreach (var el in MatchingElements())
                {
                    if (i == index) return el;
                    i++;
                }
                return null;
            }
        }

        public override Element NamedItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var el in MatchingElements())
            {
                if (el.Id == name || el.GetAttribute("name") == name)
                    return el;
            }
            return null;
        }

        public override IEnumerator<Element> GetEnumerator()
        {
            var snapshot = new List<Element>();
            foreach (var el in MatchingElements())
                snapshot.Add(el);
            return snapshot.GetEnumerator();
        }

        private IEnumerable<Element> MatchingElements()
        {
            foreach (var node in _root.Descendants())
            {
                if (node is Element el && HasAllClasses(el))
                    yield return el;
            }
        }

        private bool HasAllClasses(Element el)
        {
            var classList = el.ClassList;
            foreach (var cls in _classNames)
            {
                if (!classList.Contains(cls))
                    return false;
            }
            return true;
        }
    }
}
