// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    public abstract class NodeList : IEnumerable<Node>
    {
        public abstract int Length { get; }

        [System.Runtime.CompilerServices.IndexerName("ItemAt")]
        public abstract Node this[int index] { get; }

        public Node Item(int index) => this[index];

        public abstract IEnumerator<Node> GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

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
            var snapshot = new List<Node>();
            for (var child = _parent.FirstChild; child != null; child = child._nextSibling)
                snapshot.Add(child);
            return snapshot.GetEnumerator();
        }
    }

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

    internal sealed class StaticNodeList : NodeList
    {
        private readonly Node[] _nodes;

        public StaticNodeList(IEnumerable<Node> nodes)
        {
            ArgumentNullException.ThrowIfNull(nodes);
            _nodes = new List<Node>(nodes).ToArray();
        }

        public StaticNodeList(List<Node> nodes)
        {
            ArgumentNullException.ThrowIfNull(nodes);
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

    public abstract class HTMLCollection : IEnumerable<Element>
    {
        public abstract int Length { get; }

        [System.Runtime.CompilerServices.IndexerName("ItemAt")]
        public abstract Element this[int index] { get; }

        public Element Item(int index) => this[index];

        public abstract Element NamedItem(string name);

        public abstract IEnumerator<Element> GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static readonly HTMLCollection Empty = new StaticHTMLCollection(Array.Empty<Element>());
    }

    internal sealed class StaticHTMLCollection : HTMLCollection
    {
        private readonly Element[] _elements;

        public StaticHTMLCollection(IEnumerable<Element> elements)
        {
            ArgumentNullException.ThrowIfNull(elements);
            _elements = new List<Element>(elements).ToArray();
        }

        public StaticHTMLCollection(List<Element> elements)
        {
            ArgumentNullException.ThrowIfNull(elements);
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
    /// A live HTMLCollection over the descendants of a root that satisfy a
    /// predicate, in tree order (DOM §4.2.10.2). Backs the document collections
    /// of HTML §3.1.3 (forms, links, images, ...), whose membership is a
    /// per-element test rather than a tag name. Named access matches id, and
    /// name for the element kinds the spec lists.
    /// </summary>
    public sealed class FilteredHTMLCollection : HTMLCollection
    {
        private readonly ContainerNode _root;
        private readonly Func<Element, bool> _predicate;
        private readonly bool _nameMatches;

        public FilteredHTMLCollection(ContainerNode root, Func<Element, bool> predicate, bool nameMatches = true)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
            _nameMatches = nameMatches;
        }

        public override int Length
        {
            get
            {
                int count = 0;
                foreach (var node in _root.Descendants())
                {
                    if (node is Element el && _predicate(el))
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
                    if (node is Element el && _predicate(el))
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
                if (node is Element el && _predicate(el) &&
                    (el.Id == name || (_nameMatches && el.GetAttribute("name") == name)))
                {
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
                if (node is Element el && _predicate(el))
                    snapshot.Add(el);
            }
            return snapshot.GetEnumerator();
        }
    }

    /// <summary>
    /// A live HTMLCollection whose membership is recomputed by a delegate on
    /// every access, for collections whose order is not tree order (a table's
    /// rows: thead rows first, tfoot rows last - HTML §4.9.1).
    /// </summary>
    public sealed class ComputedHTMLCollection : HTMLCollection
    {
        private readonly Func<List<Element>> _compute;
        private readonly bool _nameMatches;

        public ComputedHTMLCollection(Func<List<Element>> compute, bool nameMatches = false)
        {
            _compute = compute ?? throw new ArgumentNullException(nameof(compute));
            _nameMatches = nameMatches;
        }

        public override int Length => _compute().Count;

        public override Element this[int index]
        {
            get
            {
                var items = _compute();
                return index >= 0 && index < items.Count ? items[index] : null;
            }
        }

        public override Element NamedItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var el in _compute())
            {
                if (el.Id == name || (_nameMatches && el.GetAttribute("name") == name))
                    return el;
            }
            return null;
        }

        public override IEnumerator<Element> GetEnumerator() => _compute().GetEnumerator();
    }

    internal sealed class TagNameHTMLCollection : HTMLCollection
    {
        private readonly ContainerNode _root;
        private readonly string _tagName;
        private readonly bool _matchAll;

        public TagNameHTMLCollection(ContainerNode root, string tagName)
        {
            _root = root;
            _tagName = tagName ?? "*";
            _matchAll = _tagName == "*";
        }

        public override int Length
        {
            get
            {
                int count = 0;
                foreach (var node in _root.Descendants())
                {
                    if (node is Element el && MatchesTag(el))
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
                    if (node is Element el && MatchesTag(el))
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
                if (node is Element el && MatchesTag(el))
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
                if (node is Element el && MatchesTag(el))
                    snapshot.Add(el);
            }
            return snapshot.GetEnumerator();
        }

        private bool MatchesTag(Element element)
        {
            if (_matchAll)
                return true;

            if (string.Equals(element.NamespaceUri, Namespaces.Html, StringComparison.Ordinal))
            {
                return string.Equals(element.TagName, _tagName, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(element.LocalName, _tagName, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(element.LocalName, _tagName, StringComparison.Ordinal);
        }
    }

    internal sealed class ClassNameHTMLCollection : HTMLCollection
    {
        private readonly ContainerNode _root;
        private readonly string[] _classNames;
        private List<WeakReference<Element>> _snapshot;
        private long _snapshotVersion = -1;

        public ClassNameHTMLCollection(ContainerNode root, string classNames)
        {
            _root = root;
            _classNames = (classNames ?? "")
                .Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public override int Length => GetSnapshot().Count;

        public override Element this[int index]
        {
            get
            {
                if (index < 0) return null;
                var snapshot = GetSnapshot();
                return index < snapshot.Count && snapshot[index].TryGetTarget(out var element)
                    ? element
                    : null;
            }
        }

        public override Element NamedItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var reference in GetSnapshot())
            {
                if (reference.TryGetTarget(out var element) &&
                    (element.Id == name || element.GetAttribute("name") == name))
                {
                    return element;
                }
            }
            return null;
        }

        public override IEnumerator<Element> GetEnumerator()
        {
            foreach (var reference in GetSnapshot())
            {
                if (reference.TryGetTarget(out var element))
                {
                    yield return element;
                }
            }
        }

        private List<WeakReference<Element>> GetSnapshot()
        {
            var version = Node.MutationSequence;
            if (_snapshot != null && _snapshotVersion == version)
            {
                return _snapshot;
            }

            var snapshot = new List<WeakReference<Element>>();
            foreach (var el in MatchingElements())
            {
                snapshot.Add(new WeakReference<Element>(el));
            }

            _snapshot = snapshot;
            _snapshotVersion = version;
            return snapshot;
        }

        private IEnumerable<Element> MatchingElements()
        {
            if (_classNames.Length == 0)
                yield break;

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
