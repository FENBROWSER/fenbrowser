// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections.Generic;
using System.Threading;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: Document interface.
    /// https://dom.spec.whatwg.org/#interface-document
    ///
    /// IMPORTANT: Document does NOT extend Element (unlike legacy code).
    /// It extends ContainerNode directly.
    ///
    /// Thread-safe initialization and state management.
    /// </summary>
    public sealed class Document : ContainerNode, INonElementParentNode
    {
        public override NodeType NodeType => NodeType.Document;
        public override string NodeName => "#document";

        // --- Static Factory ---

        /// <summary>
        /// Creates a new HTML document.
        /// </summary>
        public static Document CreateHtmlDocument(string title = null)
        {
            var doc = new Document(contentType: "text/html");

            // Create standard HTML structure
            var doctype = doc.CreateDocumentType("html");
            doc.AppendChild(doctype);

            var html = doc.CreateElement("html");
            doc.AppendChild(html);

            var head = doc.CreateElement("head");
            html.AppendChild(head);

            if (!string.IsNullOrEmpty(title))
            {
                var titleEl = doc.CreateElement("title");
                titleEl.TextContent = title;
                head.AppendChild(titleEl);
            }

            var body = doc.CreateElement("body");
            html.AppendChild(body);

            doc._readyState = DocumentReadyState.Complete;
            return doc;
        }

        /// <summary>
        /// Creates a new XML document.
        /// </summary>
        public static Document CreateXmlDocument(string namespaceUri = null, string qualifiedName = null)
        {
            var doc = new Document(contentType: "application/xml");

            if (!string.IsNullOrEmpty(qualifiedName))
            {
                var root = doc.CreateElementNS(namespaceUri, qualifiedName);
                doc.AppendChild(root);
            }

            doc._readyState = DocumentReadyState.Complete;
            return doc;
        }

        // --- Document Properties ---

        /// <summary>
        /// The doctype of this document.
        /// https://dom.spec.whatwg.org/#dom-document-doctype
        /// </summary>
        public DocumentType Doctype
        {
            get
            {
                for (var child = FirstChild; child != null; child = child._nextSibling)
                {
                    if (child is DocumentType dt)
                        return dt;
                }
                return null;
            }
        }

        /// <summary>
        /// The document element (usually &lt;html&gt;).
        /// https://dom.spec.whatwg.org/#dom-document-documentelement
        /// </summary>
        public Element DocumentElement => FirstElementChild;

        /// <summary>
        /// The &lt;head&gt; element.
        /// </summary>
        public Element Head
        {
            get
            {
                var html = DocumentElement;
                if (html == null) return null;
                for (var child = html.FirstChild; child != null; child = child._nextSibling)
                {
                    if (child is Element el && el.LocalName == "head")
                        return el;
                }
                return null;
            }
        }

        /// <summary>
        /// The &lt;body&gt; element.
        /// </summary>
        public Element Body
        {
            get
            {
                var html = DocumentElement;
                if (html == null) return null;
                for (var child = html.FirstChild; child != null; child = child._nextSibling)
                {
                    if (child is Element el && (el.LocalName == "body" || el.LocalName == "frameset"))
                        return el;
                }
                return null;
            }
            set
            {
                var html = DocumentElement;
                if (html == null)
                    throw new DomException("HierarchyRequestError", "Document has no document element");
                if (value == null)
                    throw new DomException("HierarchyRequestError", "Body cannot be null");
                if (value.LocalName != "body" && value.LocalName != "frameset")
                    throw new DomException("HierarchyRequestError", "Body must be a body or frameset element");

                var existingBody = Body;
                if (existingBody != null)
                    html.ReplaceChild(value, existingBody);
                else
                    html.AppendChild(value);
            }
        }

        /// <summary>
        /// The document's title (from the title element).
        /// https://html.spec.whatwg.org/#document.title
        /// </summary>
        public string Title
        {
            get
            {
                var titleEl = Head?.QuerySelector("title");
                return titleEl?.TextContent?.Trim() ?? "";
            }
            set
            {
                var head = Head;
                if (head == null) return;

                var titleEl = head.QuerySelector("title");
                if (titleEl == null)
                {
                    titleEl = CreateElement("title");
                    head.AppendChild(titleEl);
                }
                titleEl.TextContent = value ?? "";
            }
        }

        /// <summary>
        /// The document's URL.
        /// https://dom.spec.whatwg.org/#dom-document-url
        /// </summary>
        public string URL { get; set; } = "about:blank";

        /// <summary>
        /// Alias for URL.
        /// https://dom.spec.whatwg.org/#dom-document-documenturi
        /// </summary>
        public string DocumentURI => URL;

        /// <summary>
        /// The document's base URL for relative URLs.
        /// </summary>
        public string BaseURI { get; set; }

        /// <summary>Origin of this document per HTML spec §7.5.</summary>
        public FenBrowser.Core.Security.Origin Origin { get; set; }

        /// <summary>
        /// The document's content type.
        /// https://dom.spec.whatwg.org/#dom-document-contenttype
        /// </summary>
        public string ContentType { get; internal set; } = "text/html";

        /// <summary>
        /// The document's character encoding.
        /// https://dom.spec.whatwg.org/#dom-document-characterset
        /// </summary>
        public string CharacterSet { get; internal set; } = "UTF-8";

        /// <summary>
        /// The document's compatibility mode (quirks, limited quirks, or no quirks).
        /// https://dom.spec.whatwg.org/#dom-document-compatmode
        /// </summary>
        public string CompatMode => Mode == QuirksMode.Quirks ? "BackCompat" : "CSS1Compat";

        /// <summary>
        /// Internal quirks mode setting.
        /// </summary>
        public QuirksMode Mode { get; set; } = QuirksMode.NoQuirks;

        // --- Ready State ---

        private volatile DocumentReadyState _readyState = DocumentReadyState.Loading;

        /// <summary>
        /// The document's ready state.
        /// https://html.spec.whatwg.org/#current-document-readiness
        /// </summary>
        public DocumentReadyState ReadyState
        {
            get => _readyState;
            internal set
            {
                if (_readyState != value)
                {
                    _readyState = value;
                    // Fire readystatechange event
                    OnReadyStateChange?.Invoke(this);
                }
            }
        }

        /// <summary>
        /// Fired when the ready state changes.
        /// </summary>
        public event Action<Document> OnReadyStateChange;

        // --- Document Visibility ---

        private volatile DocumentVisibilityState _visibilityState = DocumentVisibilityState.Visible;

        /// <summary>
        /// The document's visibility state.
        /// https://html.spec.whatwg.org/#dom-document-visibilitystate
        /// </summary>
        public DocumentVisibilityState VisibilityState
        {
            get => _visibilityState;
            set
            {
                if (_visibilityState != value)
                {
                    _visibilityState = value;
                    OnVisibilityChange?.Invoke(this);
                }
            }
        }

        /// <summary>
        /// Whether the document is hidden.
        /// </summary>
        public bool Hidden => _visibilityState == DocumentVisibilityState.Hidden;

        /// <summary>
        /// Fired when visibility state changes.
        /// </summary>
        public event Action<Document> OnVisibilityChange;

        // --- Last Modified ---

        /// <summary>
        /// The last modification date of the document.
        /// </summary>
        public DateTime? LastModified { get; set; }

        // --- Document Domain ---

        /// <summary>
        /// The document's domain.
        /// </summary>
        public string Domain
        {
            get
            {
                if (string.IsNullOrEmpty(URL) || URL == "about:blank")
                    return "";
                try
                {
                    var uri = new Uri(URL);
                    return uri.Host;
                }
                catch
                {
                    return "";
                }
            }
        }

        /// <summary>
        /// The document's referrer.
        /// </summary>
        public string Referrer { get; set; } = "";

        /// <summary>
        /// The document's cookie string.
        /// </summary>
        public string Cookie { get; set; } = "";

        /// <summary>
        /// Returns the element with the given ID.
        /// https://dom.spec.whatwg.org/#dom-nonelementparentnode-getelementbyid
        /// </summary>
        public Element GetElementById(string elementId)
        {
            return _treeScope?.GetElementById(elementId);
        }

        internal void RegisterId(string id, Element element)
        {
            _treeScope?.RegisterId(id, element);
        }

        internal void UnregisterId(string id, Element element = null)
        {
            _treeScope?.UnregisterId(id, element);
        }

        internal void InvalidateIdIndex()
        {
            _treeScope?.InvalidateIdIndex();
        }

        // --- Collections ---

        /// <summary>
        /// Returns all elements with the given tag name.
        /// https://dom.spec.whatwg.org/#dom-document-getelementsbytagname
        /// </summary>
        public override HTMLCollection GetElementsByTagName(string qualifiedName)
        {
            return new TagNameHTMLCollection(this, qualifiedName);
        }

        /// <summary>
        /// Returns all elements with the given class name(s).
        /// https://dom.spec.whatwg.org/#dom-document-getelementsbyclassname
        /// </summary>
        public override HTMLCollection GetElementsByClassName(string classNames)
        {
            return new ClassNameHTMLCollection(this, classNames);
        }

        // --- Factory Methods ---

        /// <summary>
        /// Creates a new element with the given local name.
        /// https://dom.spec.whatwg.org/#dom-document-createelement
        /// </summary>
        public Element CreateElement(string localName)
        {
            if (string.IsNullOrEmpty(localName))
                throw new DomException("InvalidCharacterError", "Element name cannot be empty");

            // Validate per XML Name production (https://www.w3.org/TR/xml/#NT-Name)
            // Name ::= NameStartChar (NameChar)*
            // NameStartChar ::= ":" | [A-Z] | "_" | [a-z] | [#xC0-#xD6] | ...
            if (!IsValidXmlName(localName))
                throw new DomException("InvalidCharacterError", $"'{localName}' is not a valid element name");
            var el = new Element(localName, this);
            return el;
        }

        /// <summary>
        /// Validates a name against the XML Name production.
        /// https://www.w3.org/TR/xml/#NT-Name
        /// </summary>
        private static bool IsValidXmlName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!IsNameStartChar(name[0])) return false;
            for (int i = 1; i < name.Length; i++)
                if (!IsNameChar(name[i])) return false;
            return true;
        }

        private static bool IsNameStartChar(char c) =>
            c == ':' || c == '_' ||
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '\xC0' && c <= '\xD6') || (c >= '\xD8' && c <= '\xF6') ||
            (c >= '\xF8' && c <= '\u02FF') || (c >= '\u0370' && c <= '\u037D') ||
            (c >= '\u037F' && c <= '\u1FFF') || (c >= '\u200C' && c <= '\u200D') ||
            (c >= '\u2070' && c <= '\u218F') || (c >= '\u2C00' && c <= '\u2FEF') ||
            (c >= '\u3001' && c <= '\uD7FF') || (c >= '\uF900' && c <= '\uFDCF') ||
            (c >= '\uFDF0' && c <= '\uFFFD');

        private static bool IsNameChar(char c) =>
            IsNameStartChar(c) || c == '-' || c == '.' ||
            (c >= '0' && c <= '9') || c == '\xB7' ||
            (c >= '\u0300' && c <= '\u036F') || (c >= '\u203F' && c <= '\u2040');

        /// <summary>
        /// Creates a new element with the given namespace and qualified name.
        /// https://dom.spec.whatwg.org/#dom-document-createelementns
        /// </summary>
        public Element CreateElementNS(string namespaceUri, string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                throw new DomException("InvalidCharacterError", "Element name cannot be empty");

            // Extract local name
            int colon = qualifiedName.IndexOf(':');
            var localName = colon >= 0 ? qualifiedName.Substring(colon + 1) : qualifiedName;

            var el = new Element(localName, this, namespaceUri);
            if (colon >= 0)
                el.Prefix = qualifiedName.Substring(0, colon);

            return el;
        }

        /// <summary>
        /// Creates a new text node.
        /// https://dom.spec.whatwg.org/#dom-document-createtextnode
        /// </summary>
        public Text CreateTextNode(string data)
        {
            return new Text(data ?? "", this);
        }

        /// <summary>
        /// Creates a new comment node.
        /// https://dom.spec.whatwg.org/#dom-document-createcomment
        /// </summary>
        public Comment CreateComment(string data)
        {
            return new Comment(data ?? "", this);
        }

        /// <summary>
        /// Creates a new document fragment.
        /// https://dom.spec.whatwg.org/#dom-document-createdocumentfragment
        /// </summary>
        public DocumentFragment CreateDocumentFragment()
        {
            return new DocumentFragment(this);
        }

        /// <summary>
        /// Creates a new doctype.
        /// </summary>
        public DocumentType CreateDocumentType(string name, string publicId = "", string systemId = "")
        {
            return new DocumentType(name, publicId, systemId, this);
        }

        /// <summary>
        /// Creates a new attribute.
        /// https://dom.spec.whatwg.org/#dom-document-createattribute
        /// </summary>
        public Attr CreateAttribute(string localName)
        {
            if (string.IsNullOrEmpty(localName))
                throw new DomException("InvalidCharacterError", "Attribute name cannot be empty");

            return new Attr(localName.ToLowerInvariant(), "");
        }

        /// <summary>
        /// Creates a new attribute with namespace.
        /// https://dom.spec.whatwg.org/#dom-document-createattributens
        /// </summary>
        public Attr CreateAttributeNS(string namespaceUri, string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                throw new DomException("InvalidCharacterError", "Attribute name cannot be empty");

            return new Attr(namespaceUri, qualifiedName, "");
        }

        // --- Ranges and Traversals ---

        /// <summary>
        /// Creates a new Range.
        /// https://dom.spec.whatwg.org/#dom-document-createrange
        /// </summary>
        public Range CreateRange()
        {
            return new Range(this);
        }

        /// <summary>
        /// Creates a new TreeWalker.
        /// https://dom.spec.whatwg.org/#dom-document-createtreewalker
        /// </summary>
        public TreeWalker CreateTreeWalker(Node root, uint whatToShow = 0xFFFFFFFF, NodeFilter filter = null)
        {
            return new TreeWalker(root, whatToShow, filter);
        }

        /// <summary>
        /// Creates a new NodeIterator.
        /// https://dom.spec.whatwg.org/#dom-document-createnodeiterator
        /// </summary>
        public NodeIterator CreateNodeIterator(Node root, uint whatToShow = 0xFFFFFFFF, NodeFilter filter = null)
        {
            var iterator = new NodeIterator(root, whatToShow, filter);
            RegisterNodeIterator(iterator);
            return iterator;
        }

        // --- Top Layer (for dialog showModal) ---

        public List<Element> TopLayer { get; } = new List<Element>();

        /// <summary>
        /// The currently focused element.
        /// </summary>
        public Element ActiveElement { get; set; }

        // --- Dirty Notification ---

        public event Action OnTreeDirty;

        internal void NotifyTreeDirty()
        {
            OnTreeDirty?.Invoke();
        }

        private readonly object _nodeIteratorLock = new();
        private readonly List<WeakReference<NodeIterator>> _nodeIterators = new();

        internal void RegisterNodeIterator(NodeIterator iterator)
        {
            if (iterator == null)
                return;

            lock (_nodeIteratorLock)
            {
                TrimDeadIterators_NoLock();
                _nodeIterators.Add(new WeakReference<NodeIterator>(iterator));
            }

            iterator.Attach(this);
        }

        internal void UnregisterNodeIterator(NodeIterator iterator)
        {
            if (iterator == null)
                return;

            lock (_nodeIteratorLock)
            {
                for (int i = _nodeIterators.Count - 1; i >= 0; i--)
                {
                    if (!_nodeIterators[i].TryGetTarget(out var target) || ReferenceEquals(target, iterator))
                    {
                        _nodeIterators.RemoveAt(i);
                    }
                }
            }
        }

        internal void NotifyNodeRemoved(Node node)
        {
            if (node == null)
                return;

            NodeIterator[] iterators;
            lock (_nodeIteratorLock)
            {
                TrimDeadIterators_NoLock();
                var live = new List<NodeIterator>(_nodeIterators.Count);
                foreach (var weakRef in _nodeIterators)
                {
                    if (weakRef.TryGetTarget(out var iterator))
                    {
                        live.Add(iterator);
                    }
                }

                iterators = live.ToArray();
            }

            foreach (var iterator in iterators)
            {
                iterator.OnNodeRemoved(node);
            }
        }

        private void TrimDeadIterators_NoLock()
        {
            for (int i = _nodeIterators.Count - 1; i >= 0; i--)
            {
                if (!_nodeIterators[i].TryGetTarget(out _))
                {
                    _nodeIterators.RemoveAt(i);
                }
            }
        }

        // --- Constructor ---

        /// <summary>
        /// Creates a new Document.
        /// </summary>
        /// <param name="url">The document URL (default: about:blank)</param>
        /// <param name="contentType">The content type (default: text/html)</param>
        /// <param name="characterSet">The character set (default: UTF-8)</param>
        public Document(
            string url = "about:blank",
            string contentType = "text/html",
            string characterSet = "UTF-8")
        {
            _ownerDocument = this; // Document owns itself
            _flags |= NodeFlags.IsDocument | NodeFlags.IsContainer | NodeFlags.IsConnected;
            _treeScope = new TreeScope(this);

            URL = url ?? "about:blank";
            BaseURI = url ?? "about:blank";
            ContentType = contentType ?? "text/html";
            CharacterSet = characterSet ?? "UTF-8";
            LastModified = DateTime.UtcNow;
        }

        /// <summary>
        /// Creates a new Document with options.
        /// </summary>
        public Document(DocumentInit options) : this(
            options?.Url ?? "about:blank",
            options?.ContentType ?? "text/html",
            options?.CharacterSet ?? "UTF-8")
        {
            if (options != null)
            {
                Mode = options.QuirksMode;
                Referrer = options.Referrer ?? "";
            }
        }

        // --- Child Validation Override ---

        protected override void ValidateChildType(Node node)
        {
            // Document can have: DocumentType (max 1), Element (max 1), Comment, ProcessingInstruction
            if (node is Document)
                throw new DomException("HierarchyRequestError", "Cannot insert a Document as a child");

            if (node is Text)
                throw new DomException("HierarchyRequestError", "Document cannot have Text children");

            if (node is DocumentType)
            {
                // Check if we already have a doctype
                if (Doctype != null)
                    throw new DomException("HierarchyRequestError", "Document already has a DocumentType");
            }

            if (node is Element)
            {
                // Check if we already have a document element
                if (DocumentElement != null)
                    throw new DomException("HierarchyRequestError", "Document already has a document element");
            }
        }

        // --- Cloning ---

        public override Node CloneNode(bool deep = false)
        {
            var doc = new Document();
            doc.URL = URL;
            doc.ContentType = ContentType;
            doc.CharacterSet = CharacterSet;
            doc.Mode = Mode;

            if (deep)
            {
                for (var child = FirstChild; child != null; child = child._nextSibling)
                    doc.AppendChild(child.CloneNode(true));
            }

            return doc;
        }

        public override string ToString() => "#document";
    }

    /// <summary>
    /// INonElementParentNode interface for getElementById.
    /// Implemented by Document and DocumentFragment.
    /// </summary>
    public interface INonElementParentNode
    {
        Element GetElementById(string elementId);
    }

    /// <summary>
    /// Options for creating a Document.
    /// </summary>
    public sealed class DocumentInit
    {
        /// <summary>The document URL.</summary>
        public string Url { get; set; } = "about:blank";

        /// <summary>The content type.</summary>
        public string ContentType { get; set; } = "text/html";

        /// <summary>The character set.</summary>
        public string CharacterSet { get; set; } = "UTF-8";

        /// <summary>The quirks mode.</summary>
        public QuirksMode QuirksMode { get; set; } = QuirksMode.NoQuirks;

        /// <summary>The referrer URL.</summary>
        public string Referrer { get; set; }
    }

    /// <summary>
    /// Document ready state values.
    /// https://html.spec.whatwg.org/#current-document-readiness
    /// </summary>
    public enum DocumentReadyState
    {
        /// <summary>The document is still loading.</summary>
        Loading,
        /// <summary>The document has finished parsing but is still loading sub-resources.</summary>
        Interactive,
        /// <summary>The document and all sub-resources have finished loading.</summary>
        Complete
    }

    /// <summary>
    /// Document visibility state values.
    /// https://html.spec.whatwg.org/#dom-document-visibilitystate
    /// </summary>
    public enum DocumentVisibilityState
    {
        /// <summary>The document is visible.</summary>
        Visible,
        /// <summary>The document is not visible.</summary>
        Hidden
    }
}
