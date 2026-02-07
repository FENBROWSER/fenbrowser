// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Dom.V2.Selectors;
using FenBrowser.Core.Dom.V2.Security;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: Element interface.
    /// https://dom.spec.whatwg.org/#interface-element
    ///
    /// Represents an HTML/SVG/MathML element.
    /// Single source of truth for attributes (NamedNodeMap).
    /// </summary>
    public class Element : ContainerNode, IChildNode, INonDocumentTypeChildNode, ISlottable
    {
        public override NodeType NodeType => NodeType.Element;
        public override string NodeName => TagName;

        // --- Identity ---

        /// <summary>
        /// The qualified tag name (uppercase for HTML).
        /// https://dom.spec.whatwg.org/#dom-element-tagname
        /// </summary>
        public string TagName { get; }

        /// <summary>
        /// The local name (lowercase for HTML).
        /// https://dom.spec.whatwg.org/#dom-element-localname
        /// </summary>
        public string LocalName { get; }

        /// <summary>
        /// The namespace URI.
        /// https://dom.spec.whatwg.org/#dom-element-namespaceuri
        /// </summary>
        public string NamespaceUri { get; }

        /// <summary>
        /// The namespace prefix.
        /// https://dom.spec.whatwg.org/#dom-element-prefix
        /// </summary>
        public string Prefix { get; set; }

        // --- Attributes (Single Source of Truth) ---

        private readonly NamedNodeMap _attributes;

        /// <summary>
        /// Returns the attributes collection.
        /// https://dom.spec.whatwg.org/#dom-element-attributes
        /// </summary>
        public NamedNodeMap Attributes => _attributes;

        // --- Legacy V1 Compatibility ---
        [Obsolete("Use GetAttribute/SetAttribute/HasAttribute")]
        public AttributeCollectionAdapter Attr => new AttributeCollectionAdapter(this);

        public class AttributeCollectionAdapter : IEnumerable<KeyValuePair<string, string>>
        {
            private readonly Element _el;
            public AttributeCollectionAdapter(Element el) { _el = el; }

            public string this[string key]
            {
                get => _el.GetAttribute(key);
                set => _el.SetAttribute(key, value);
            }

            public bool ContainsKey(string key) => _el.HasAttribute(key);

            public bool TryGetValue(string key, out string value)
            {
                value = _el.GetAttribute(key);
                return value != null;
            }

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            {
                foreach (var attr in _el.Attributes)
                {
                    yield return new KeyValuePair<string, string>(attr.Name, attr.Value);
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        // --- Computed Accessors ---

        /// <summary>
        /// Gets or sets the id attribute.
        /// https://dom.spec.whatwg.org/#dom-element-id
        /// </summary>
        public string Id
        {
            get => GetAttribute("id");
            set => SetAttribute("id", value);
        }

        /// <summary>
        /// Gets or sets the class attribute.
        /// https://dom.spec.whatwg.org/#dom-element-classname
        /// </summary>
        public string ClassName
        {
            get => GetAttribute("class");
            set => SetAttribute("class", value);
        }

        private DOMTokenList _classList;

        /// <summary>
        /// Returns the classList DOMTokenList.
        /// https://dom.spec.whatwg.org/#dom-element-classlist
        /// </summary>
        public DOMTokenList ClassList => _classList ??= new DOMTokenList(this, "class");

        // --- Constructor ---

        /// <summary>
        /// Creates a new Element with the given local name.
        /// </summary>
        public Element(string localName, Document owner = null, string namespaceUri = null)
        {
            if (string.IsNullOrEmpty(localName))
                throw new ArgumentException("Element name cannot be null or empty", nameof(localName));

            LocalName = localName.ToLowerInvariant();

            // HTML elements have uppercase TagName, others preserve case
            if (namespaceUri == null || namespaceUri == Namespaces.Html)
            {
                TagName = localName.ToUpperInvariant();
                NamespaceUri = Namespaces.Html;
            }
            else
            {
                TagName = localName;
                NamespaceUri = namespaceUri;
            }

            _ownerDocument = owner;
            _attributes = new NamedNodeMap(this);
            _flags |= NodeFlags.IsElement | NodeFlags.IsContainer;
        }

        // --- Attribute API ---

        /// <summary>
        /// Returns the value of the attribute with the given name.
        /// https://dom.spec.whatwg.org/#dom-element-getattribute
        /// </summary>
        public string GetAttribute(string qualifiedName)
        {
            return _attributes.GetNamedItem(qualifiedName)?.Value;
        }

        /// <summary>
        /// Returns the value of the attribute with the given namespace and local name.
        /// https://dom.spec.whatwg.org/#dom-element-getattributens
        /// </summary>
        public string GetAttributeNS(string namespaceUri, string localName)
        {
            return _attributes.GetNamedItemNS(namespaceUri, localName)?.Value;
        }

        /// <summary>
        /// Sets the attribute with the given name to the given value.
        /// https://dom.spec.whatwg.org/#dom-element-setattribute
        /// Validates and sanitizes attribute names and values for security.
        /// </summary>
        public void SetAttribute(string qualifiedName, string value)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                throw new DomException("InvalidCharacterError", "Attribute name cannot be empty");

            // Validate attribute name
            var nameResult = AttributeSanitizer.ValidateName(qualifiedName);
            if (!nameResult.IsValid)
                throw new DomException("InvalidCharacterError", nameResult.Message);

            // Validate and sanitize attribute value
            var valueResult = AttributeSanitizer.ValidateValue(qualifiedName, value, out var sanitizedValue);
            // Use sanitized value (may be modified or emptied for security)
            value = sanitizedValue;

            // Phase guard
            EngineContext.Current.AssertNotInPhase(
                EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            var existing = _attributes.GetNamedItem(qualifiedName);
            if (existing != null)
            {
                // Update existing attribute
                existing.Value = value ?? "";
            }
            else
            {
                // Create new attribute
                var attr = new Attr(qualifiedName, value ?? "", this);
                _attributes.Add(attr);
                OnAttributeAdded(attr);
            }
        }

        /// <summary>
        /// Sets the attribute without security sanitization.
        /// WARNING: Only use this for trusted content (e.g., parsed from HTML).
        /// </summary>
        public void SetAttributeUnsafe(string qualifiedName, string value)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                return;

            var existing = _attributes.GetNamedItem(qualifiedName);
            if (existing != null)
            {
                existing.Value = value ?? "";
            }
            else
            {
                var attr = new Attr(qualifiedName, value ?? "", this);
                _attributes.Add(attr);
                OnAttributeAdded(attr);
            }
        }

        /// <summary>
        /// Sets the attribute with the given namespace to the given value.
        /// https://dom.spec.whatwg.org/#dom-element-setattributens
        /// </summary>
        public void SetAttributeNS(string namespaceUri, string qualifiedName, string value)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                throw new DomException("InvalidCharacterError", "Attribute name cannot be empty");

            EngineContext.Current.AssertNotInPhase(
                EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            // Extract local name and prefix
            int colon = qualifiedName.IndexOf(':');
            var localName = colon >= 0 ? qualifiedName.Substring(colon + 1) : qualifiedName;

            var existing = _attributes.GetNamedItemNS(namespaceUri, localName);
            if (existing != null)
            {
                existing.Value = value ?? "";
            }
            else
            {
                var attr = new Attr(namespaceUri, qualifiedName, value ?? "", this);
                _attributes.Add(attr);
                OnAttributeAdded(attr);
            }
        }

        /// <summary>
        /// Returns true if the attribute with the given name exists.
        /// https://dom.spec.whatwg.org/#dom-element-hasattribute
        /// </summary>
        public bool HasAttribute(string qualifiedName)
        {
            return _attributes.GetNamedItem(qualifiedName) != null;
        }

        /// <summary>
        /// Returns true if the attribute with the given namespace exists.
        /// https://dom.spec.whatwg.org/#dom-element-hasattributens
        /// </summary>
        public bool HasAttributeNS(string namespaceUri, string localName)
        {
            return _attributes.GetNamedItemNS(namespaceUri, localName) != null;
        }

        /// <summary>
        /// Returns true if the element has any attributes.
        /// https://dom.spec.whatwg.org/#dom-element-hasattributes
        /// </summary>
        public bool HasAttributes()
        {
            return _attributes.Length > 0;
        }

        /// <summary>
        /// Removes the attribute with the given name.
        /// https://dom.spec.whatwg.org/#dom-element-removeattribute
        /// </summary>
        public void RemoveAttribute(string qualifiedName)
        {
            var attr = _attributes.GetNamedItem(qualifiedName);
            if (attr != null)
            {
                EngineContext.Current.AssertNotInPhase(
                    EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

                var oldValue = attr.Value;
                _attributes.Remove(attr);
                attr.OwnerElement = null;
                OnAttributeRemoved(attr, oldValue);
            }
        }

        /// <summary>
        /// Removes the attribute with the given namespace.
        /// https://dom.spec.whatwg.org/#dom-element-removeattributens
        /// </summary>
        public void RemoveAttributeNS(string namespaceUri, string localName)
        {
            var attr = _attributes.GetNamedItemNS(namespaceUri, localName);
            if (attr != null)
            {
                EngineContext.Current.AssertNotInPhase(
                    EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

                var oldValue = attr.Value;
                _attributes.Remove(attr);
                attr.OwnerElement = null;
                OnAttributeRemoved(attr, oldValue);
            }
        }

        /// <summary>
        /// Toggles an attribute (removes if exists, adds if doesn't).
        /// https://dom.spec.whatwg.org/#dom-element-toggleattribute
        /// </summary>
        public bool ToggleAttribute(string qualifiedName, bool? force = null)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                throw new DomException("InvalidCharacterError", "Attribute name cannot be empty");

            var exists = HasAttribute(qualifiedName);

            if (force.HasValue)
            {
                if (force.Value && !exists)
                {
                    SetAttribute(qualifiedName, "");
                    return true;
                }
                if (!force.Value && exists)
                {
                    RemoveAttribute(qualifiedName);
                    return false;
                }
                return force.Value;
            }

            if (exists)
            {
                RemoveAttribute(qualifiedName);
                return false;
            }
            else
            {
                SetAttribute(qualifiedName, "");
                return true;
            }
        }

        /// <summary>
        /// Returns the attribute node with the given name.
        /// https://dom.spec.whatwg.org/#dom-element-getattributenode
        /// </summary>
        public Attr GetAttributeNode(string qualifiedName)
        {
            return _attributes.GetNamedItem(qualifiedName);
        }

        /// <summary>
        /// Returns the attribute node with the given namespace.
        /// https://dom.spec.whatwg.org/#dom-element-getattributenodens
        /// </summary>
        public Attr GetAttributeNodeNS(string namespaceUri, string localName)
        {
            return _attributes.GetNamedItemNS(namespaceUri, localName);
        }

        /// <summary>
        /// Sets the attribute node, returning the old one if replaced.
        /// https://dom.spec.whatwg.org/#dom-element-setattributenode
        /// </summary>
        public Attr SetAttributeNode(Attr attr)
        {
            return _attributes.SetNamedItem(attr);
        }

        /// <summary>
        /// Removes the attribute node.
        /// https://dom.spec.whatwg.org/#dom-element-removeattributenode
        /// </summary>
        public Attr RemoveAttributeNode(Attr attr)
        {
            if (attr == null)
                throw new ArgumentNullException(nameof(attr));
            if (attr.OwnerElement != this)
                throw new DomException("NotFoundError", "Attribute does not belong to this element");

            _attributes.Remove(attr);
            attr.OwnerElement = null;
            OnAttributeRemoved(attr, attr.Value);
            return attr;
        }

        // --- Attribute Change Callbacks ---

        internal void OnAttributeValueChanged(Attr attr, string oldValue)
        {
            var name = attr.Name;

            // Update ID index
            if (name.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(oldValue))
                    _treeScope?.UnregisterId(oldValue);
                if (!string.IsNullOrEmpty(attr.Value))
                    _treeScope?.RegisterId(attr.Value, this);

                _flags = string.IsNullOrEmpty(attr.Value)
                    ? (_flags & ~NodeFlags.HasId)
                    : (_flags | NodeFlags.HasId);
            }

            // Update class flag
            if (name.Equals("class", StringComparison.OrdinalIgnoreCase))
            {
                _flags = string.IsNullOrEmpty(attr.Value)
                    ? (_flags & ~NodeFlags.HasClass)
                    : (_flags | NodeFlags.HasClass);
            }

            // Update style flag
            if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                _flags = string.IsNullOrEmpty(attr.Value)
                    ? (_flags & ~NodeFlags.HasStyleAttribute)
                    : (_flags | NodeFlags.HasStyleAttribute);
            }

            // Mark style dirty for style-affecting attributes
            if (IsStyleAffectingAttribute(name))
            {
                MarkDirty(InvalidationKind.Style);
            }

            // Update ancestor filter for selector optimization
            if (name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("class", StringComparison.OrdinalIgnoreCase))
            {
                UpdateAncestorFilter();
            }

            // Notify mutation observers
            NotifyAttributeMutation(attr, oldValue);
        }

        private void OnAttributeAdded(Attr attr)
        {
            OnAttributeValueChanged(attr, null);
        }

        private void OnAttributeRemoved(Attr attr, string oldValue)
        {
            var name = attr.Name;

            if (name.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(oldValue))
                    _treeScope?.UnregisterId(oldValue);
                _flags &= ~NodeFlags.HasId;
            }

            if (name.Equals("class", StringComparison.OrdinalIgnoreCase))
                _flags &= ~NodeFlags.HasClass;

            if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
                _flags &= ~NodeFlags.HasStyleAttribute;

            if (IsStyleAffectingAttribute(name))
                MarkDirty(InvalidationKind.Style);

            UpdateAncestorFilter();
            NotifyAttributeMutation(attr, oldValue);
        }

        private static bool IsStyleAffectingAttribute(string name)
        {
            return name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("class", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("style", StringComparison.OrdinalIgnoreCase);
        }

        private void NotifyAttributeMutation(Attr attr, string oldValue)
        {
            // Notify static event for DevTools
            Node.NotifyMutation(this, "attributes", attr.Name, attr.NamespaceUri, null, null);
        }

        // --- Ancestor Bloom Filter (Selector Optimization) ---

        public long AncestorFilter { get; private set; }

        internal void UpdateAncestorFilter()
        {
            long newFilter = 0;
            if (_parentNode is Element parent)
            {
                newFilter = parent.AncestorFilter | parent.ComputeFeatureHash();
            }

            if (newFilter != AncestorFilter)
            {
                AncestorFilter = newFilter;
                // Propagate to children
                for (var child = FirstChild; child != null; child = child._nextSibling)
                {
                    if (child is Element childEl)
                        childEl.UpdateAncestorFilter();
                }
            }
        }

        internal long ComputeFeatureHash()
        {
            long hash = 0;
            hash |= BloomHash(TagName);

            var id = Id;
            if (!string.IsNullOrEmpty(id))
                hash |= BloomHash("#" + id);

            foreach (var cls in ClassList)
                hash |= BloomHash("." + cls);

            return hash;
        }

        private static long BloomHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++)
                h = (h ^ s[i]) * 16777619;
            return (1L << (int)(h % 64)) | (1L << (int)((h >> 6) % 64));
        }

        // --- Shadow DOM ---

        private ShadowRoot _shadowRoot;

        /// <summary>
        /// Returns the shadow root attached to this element.
        /// https://dom.spec.whatwg.org/#dom-element-shadowroot
        /// </summary>
        public ShadowRoot ShadowRoot => _shadowRoot?.Mode == ShadowRootMode.Open ? _shadowRoot : null;

        /// <summary>
        /// Attaches a shadow root to this element.
        /// https://dom.spec.whatwg.org/#dom-element-attachshadow
        /// </summary>
        public ShadowRoot AttachShadow(ShadowRootInit init)
        {
            if (_shadowRoot != null)
                throw new DomException("NotSupportedError", "Element already has a shadow root");

            // Validate element can have shadow
            if (!CanHaveShadowRoot())
                throw new DomException("NotSupportedError",
                    $"Element <{LocalName}> cannot have a shadow root");

            _shadowRoot = new ShadowRoot(this, init.Mode, init.DelegatesFocus, init.SlotAssignment);
            _flags |= NodeFlags.HasShadowRoot;
            return _shadowRoot;
        }

        private bool CanHaveShadowRoot()
        {
            // Valid custom element (contains hyphen)
            if (LocalName.Contains('-'))
                return true;

            // Built-in elements that support shadow
            return LocalName switch
            {
                "article" or "aside" or "blockquote" or "body" or "div" or
                "footer" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
                "header" or "main" or "nav" or "p" or "section" or "span" => true,
                _ => false
            };
        }

        // --- ISlottable ---

        public Element AssignedSlot
        {
            get
            {
                // TODO: Implement slot assignment
                return null;
            }
        }

        // --- IChildNode ---

        public void Remove()
        {
            if (_parentNode is ContainerNode parent)
                parent.RemoveChild(this);
        }

        public void Before(params Node[] nodes)
        {
            if (_parentNode is not ContainerNode parent) return;
            foreach (var node in nodes)
                parent.InsertBefore(node, this);
        }

        public void After(params Node[] nodes)
        {
            if (_parentNode is not ContainerNode parent) return;
            var refNode = _nextSibling;
            foreach (var node in nodes)
            {
                if (refNode != null)
                    parent.InsertBefore(node, refNode);
                else
                    parent.AppendChild(node);
            }
        }

        public void ReplaceWith(params Node[] nodes)
        {
            if (_parentNode is not ContainerNode parent) return;
            var refNode = _nextSibling;
            parent.RemoveChild(this);
            foreach (var node in nodes)
            {
                if (refNode != null)
                    parent.InsertBefore(node, refNode);
                else
                    parent.AppendChild(node);
            }
        }

        // --- INonDocumentTypeChildNode ---

        public Element PreviousElementSibling
        {
            get
            {
                for (var node = _previousSibling; node != null; node = node._previousSibling)
                {
                    if (node is Element el) return el;
                }
                return null;
            }
        }

        public Element NextElementSibling
        {
            get
            {
                for (var node = _nextSibling; node != null; node = node._nextSibling)
                {
                    if (node is Element el) return el;
                }
                return null;
            }
        }

        // --- Selector Methods ---

        /// <summary>
        /// Returns true if this element matches the selector.
        /// https://dom.spec.whatwg.org/#dom-element-matches
        /// Uses compiled SelectorEngine with bloom filter optimization.
        /// </summary>
        /// <param name="selectors">CSS selector string</param>
        /// <returns>True if this element matches</returns>
        /// <exception cref="DomException">SyntaxError if selector is invalid</exception>
        public bool Matches(string selectors)
        {
            if (string.IsNullOrWhiteSpace(selectors))
                throw new DomException("SyntaxError", "Selector cannot be empty");

            try
            {
                return SelectorEngine.Matches(this, selectors);
            }
            catch (DomException)
            {
                throw; // Re-throw DOMExceptions (invalid selectors)
            }
            catch (Exception ex)
            {
                throw new DomException("SyntaxError", $"Invalid selector: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the closest ancestor (or self) matching the selector.
        /// https://dom.spec.whatwg.org/#dom-element-closest
        /// Uses compiled SelectorEngine with bloom filter optimization.
        /// </summary>
        /// <param name="selectors">CSS selector string</param>
        /// <returns>Closest matching element or null</returns>
        /// <exception cref="DomException">SyntaxError if selector is invalid</exception>
        public Element Closest(string selectors)
        {
            if (string.IsNullOrWhiteSpace(selectors))
                throw new DomException("SyntaxError", "Selector cannot be empty");

            try
            {
                return SelectorEngine.Closest(this, selectors);
            }
            catch (DomException)
            {
                throw; // Re-throw DOMExceptions (invalid selectors)
            }
            catch (Exception ex)
            {
                throw new DomException("SyntaxError", $"Invalid selector: {ex.Message}");
            }
        }

        /// <summary>
        /// Alternative name for Matches (non-standard but widely supported).
        /// </summary>
        [Obsolete("Use Matches() instead - this is a non-standard alias")]
        public bool MatchesSelector(string selectors) => Matches(selectors);

        /// <summary>
        /// Webkit-prefixed version of Matches (deprecated).
        /// </summary>
        [Obsolete("Use Matches() instead - webkit prefix is deprecated")]
        public bool WebkitMatchesSelector(string selectors) => Matches(selectors);

        // --- Cloning ---

        public override Node CloneNode(bool deep = false)
        {
            var clone = new Element(LocalName, _ownerDocument, NamespaceUri);

            // Copy attributes
            foreach (var attr in _attributes)
                clone.SetAttribute(attr.Name, attr.Value);

            if (deep)
            {
                for (var child = FirstChild; child != null; child = child._nextSibling)
                    clone.AppendChild(child.CloneNode(true));
            }

            return clone;
        }

        // --- Equality ---

        public override bool IsEqualNode(Node other)
        {
            if (other is not Element otherEl) return false;
            if (NamespaceUri != otherEl.NamespaceUri) return false;
            if (LocalName != otherEl.LocalName) return false;
            if (_attributes.Length != otherEl._attributes.Length) return false;

            // Compare attributes
            foreach (var attr in _attributes)
            {
                var otherAttr = otherEl._attributes.GetNamedItemNS(attr.NamespaceUri, attr.LocalName);
                if (otherAttr == null || attr.Value != otherAttr.Value)
                    return false;
            }

            return base.IsEqualNode(other);
        }

        // --- Serialization ---

        /// <summary>
        /// Gets or sets the HTML content inside this element.
        /// https://dom.spec.whatwg.org/#dom-element-innerhtml
        /// </summary>
        public string InnerHTML
        {
            get => SerializeChildren();
            set
            {
                while (FirstChild != null)
                    RemoveChild(FirstChild);

                if (string.IsNullOrEmpty(value))
                    return;

                try
                {
                    // Parse as fragment by wrapping inside <body>, then move children into this element.
                    var html = "<!doctype html><html><body>" + value + "</body></html>";
                    var parsed = new Parsing.HtmlParser(html).Parse();
                    var source = parsed?.Body;
                    if (source == null)
                    {
                        AppendChild(new Text(value, _ownerDocument));
                        return;
                    }

                    while (source.FirstChild != null)
                        AppendChild(source.FirstChild);
                }
                catch
                {
                    // Keep behavior safe and non-throwing for malformed HTML fragments.
                    AppendChild(new Text(value, _ownerDocument));
                }
            }
        }

        public override string ToHtml() => SerializeElement();

        public string CollectText() => TextContent;

        /// <summary>
        /// Gets the HTML representation of this element and its contents.
        /// https://dom.spec.whatwg.org/#dom-element-outerhtml
        /// </summary>
        public string OuterHTML => SerializeElement();

        private string SerializeElement()
        {
            var sb = new StringBuilder();
            sb.Append('<').Append(LocalName);

            foreach (var attr in _attributes)
                sb.Append(' ').Append(attr.Name).Append("=\"")
                  .Append(EscapeAttribute(attr.Value)).Append('"');

            // Void elements
            if (IsVoidElement())
            {
                sb.Append(" />");
            }
            else
            {
                sb.Append('>');
                sb.Append(SerializeChildren());
                sb.Append("</").Append(LocalName).Append('>');
            }

            return sb.ToString();
        }

        private string SerializeChildren()
        {
            var sb = new StringBuilder();
            for (var child = FirstChild; child != null; child = child._nextSibling)
            {
                if (child is Element el)
                    sb.Append(el.SerializeElement());
                else if (child is Text t)
                    sb.Append(EscapeText(t.Data));
                else if (child is Comment c)
                    sb.Append("<!--").Append(c.Data).Append("-->");
            }
            return sb.ToString();
        }

        private bool IsVoidElement()
        {
            return LocalName switch
            {
                "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or
                "input" or "link" or "meta" or "param" or "source" or "track" or "wbr" => true,
                _ => false
            };
        }

        private static string EscapeAttribute(string value)
        {
            return value?.Replace("&", "&amp;")
                        .Replace("\"", "&quot;")
                        .Replace("<", "&lt;")
                        .Replace(">", "&gt;") ?? "";
        }

        private static string EscapeText(string text)
        {
            return text?.Replace("&", "&amp;")
                       .Replace("<", "&lt;")
                       .Replace(">", "&gt;") ?? "";
        }

        public override string ToString() => $"<{LocalName}{(Id != null ? $"#{Id}" : "")}>";

    }

    /// <summary>
    /// Options for attachShadow.
    /// </summary>
    public struct ShadowRootInit
    {
        public ShadowRootMode Mode;
        public bool DelegatesFocus;
        public SlotAssignmentMode SlotAssignment;
    }

    /// <summary>
    /// Namespace URIs for HTML, SVG, and MathML.
    /// </summary>
    public static class Namespaces
    {
        public const string Html = "http://www.w3.org/1999/xhtml";
        public const string Svg = "http://www.w3.org/2000/svg";
        public const string MathML = "http://www.w3.org/1998/Math/MathML";
        public const string XLink = "http://www.w3.org/1999/xlink";
        public const string Xml = "http://www.w3.org/XML/1998/namespace";
        public const string Xmlns = "http://www.w3.org/2000/xmlns/";
    }
}
