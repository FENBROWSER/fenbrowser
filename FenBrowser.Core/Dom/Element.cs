using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using FenBrowser.Core.Engine; // Phase protection

namespace FenBrowser.Core.Dom
{
    public class Element : Node
    {
        public override NodeType NodeType => NodeType.Element;
        public override string NodeName => TagName;

        public string TagName { get; private set; }

        // Attribute storage - DOM Living Standard §4.10
        private readonly NamedNodeMap _attributes;
        
        /// <summary>
        /// DOM spec: Returns a NamedNodeMap containing the attributes of this element.
        /// </summary>
        public NamedNodeMap NamedAttributes => _attributes;

        // Legacy attribute storage (for backward compatibility)
        public Dictionary<string, string> Attributes { get; private set; }
        public Dictionary<string, string> AttributesRaw { get; private set; }
        private readonly Dictionary<string, string> _attrOriginalNames;

        public Element(string tagName)
        {
            TagName = (tagName ?? "DIV").ToUpperInvariant(); // Canonical upper case for HTML
            Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AttributesRaw = new Dictionary<string, string>(StringComparer.Ordinal);
            _attrOriginalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _attributes = new NamedNodeMap(this);
        }

        // ---- Backward Compatibility Aliases (for LiteElement migration) ----
        
        /// <summary>Alias for TagName (LiteElement compatibility)</summary>
        public string Tag => TagName;
        
        /// <summary>Alias for Attributes (LiteElement compatibility)</summary>
        public Dictionary<string, string> Attr => Attributes;
        
        /// <summary>Text content property (LiteElement compatibility)</summary>
        public string Text
        {
            get => CollectText();
            set
            {
                Children.Clear();
                if (!string.IsNullOrEmpty(value))
                    AppendChild(new Text(value));
            }
        }
        
        /// <summary>Check if this is a text node (LiteElement compatibility)</summary>
        public bool IsText => NodeType == NodeType.Text;
        
        /// <summary>Alias for AppendChild (LiteElement compatibility)</summary>
        public void Append(Node child) => AppendChild(child);
        
        /// <summary>Copy attributes from another element (LiteElement compatibility)</summary>
        public void CopyAttributesFrom(Element source)
        {
            if (source?.Attributes == null) return;
            foreach (var kv in source.Attributes)
            {
                SetAttribute(kv.Key, kv.Value);
            }
        }
        
        /// <summary>Get original attribute name for case preservation</summary>
        public string GetOriginalAttributeName(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return key;
            return _attrOriginalNames.TryGetValue(key.ToLowerInvariant(), out var orig) ? orig : key;
        }

        /// <summary>Active element (Document compatibility)</summary>
        public Element ActiveElement { get; set; }
        
        /// <summary>Classes list as HashSet (LiteElement compatibility)</summary>
        public HashSet<string> Classes
        {
            get => new HashSet<string>(ClassList, StringComparer.Ordinal);
        }
        
        /// <summary>Serialize element to HTML (LiteElement compatibility)</summary>
        public string ToHtml()
        {
            var sb = new StringBuilder();
            sb.Append('<').Append(TagName.ToLowerInvariant());
            foreach (var kv in Attributes)
            {
                sb.Append(' ').Append(kv.Key).Append("=\"").Append(System.Net.WebUtility.HtmlEncode(kv.Value ?? "")).Append('"');
            }
            sb.Append('>');
            foreach (var child in Children)
            {
                if (child is Element el) sb.Append(el.ToHtml());
                else if (child is Text t) sb.Append(System.Net.WebUtility.HtmlEncode(t.Data ?? ""));
            }
            sb.Append("</").Append(TagName.ToLowerInvariant()).Append('>');
            return sb.ToString();
        }
        
        /// <summary>Clone element (LiteElement compatibility)</summary>
        public Element Clone(bool deep = false)
        {
            return deep ? DeepClone() : ShallowClone();
        }
        
        /// <summary>Query elements by tag name (LiteElement compatibility)</summary>
        public IEnumerable<Element> QueryByTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return Enumerable.Empty<Element>();
            return Descendants().OfType<Element>().Where(e => 
                string.Equals(e.TagName, tagName, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>Self and all descendants (LiteElement compatibility)</summary>
        public IEnumerable<Element> SelfAndDescendants()
        {
            var stack = new Stack<Element>();
            stack.Push(this);
            
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                yield return el;
                
                if (el.Children != null && el.Children.Count > 0)
                {
                    // Push in reverse order to maintain original iteration order
                    for (int i = el.Children.Count - 1; i >= 0; i--)
                    {
                        if (el.Children[i] is Element childEl)
                        {
                            stack.Push(childEl);
                        }
                    }
                }
            }
        }


        public string Id
        {
            get => GetAttribute("id");
            set => SetAttribute("id", value);
        }

        private DOMTokenList _classList;
        
        /// <summary>
        /// DOM Living Standard: Returns a DOMTokenList for the class attribute.
        /// 10/10 Spec: Full DOMTokenList API (add, remove, toggle, replace, contains).
        /// </summary>
        public DOMTokenList ClassList 
        {
            get
            {
                _classList ??= new DOMTokenList(this, "class");
                return _classList;
            }
        }

        public void AddClass(string className)
        {
            if (string.IsNullOrWhiteSpace(className)) return;
            var cur = GetAttribute("class") ?? "";
            var parts = cur.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!parts.Contains(className, StringComparer.Ordinal)) parts.Add(className);
            SetAttribute("class", string.Join(" ", parts));
        }

        public void RemoveClass(string className)
        {
             if (string.IsNullOrWhiteSpace(className)) return;
            var cur = GetAttribute("class") ?? "";
            var parts = cur.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                           .Where(c => !string.Equals(c, className, StringComparison.Ordinal)).ToArray();
            SetAttribute("class", string.Join(" ", parts));
        }

        public bool HasClass(string className)
        {
             if (string.IsNullOrWhiteSpace(className)) return false;
             return ClassList.Contains(className, StringComparer.Ordinal);
        }

        public override string GetAttribute(string name)
        {
            var attr = GetAttributeNode(name);
            return attr?.Value;
        }

        public override void SetAttribute(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            
            // Phase Guard handled in SetAttributeNode
            
            var key = name.Trim().ToLowerInvariant();
            var existing = _attributes.GetNamedItem(key);
            
            if (existing != null)
            {
                existing.Value = value ?? "";
                SetAttributeNode(existing);
            }
            else
            {
                SetAttributeNode(new Attr(name.Trim(), value ?? ""));
            }
        }

        public override bool HasAttribute(string name)
        {
            return GetAttributeNode(name) != null;
        }

        public override bool RemoveAttribute(string name)
        {
            var attr = GetAttributeNode(name);
            if (attr != null)
            {
                RemoveAttributeNode(attr);
                return true;
            }
            return false;
        }

        public Attr GetAttributeNode(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return _attributes.GetNamedItem(name);
        }

        public Attr SetAttributeNode(Attr newAttr)
        {
            if (newAttr == null) return null;
            
            // Phase Guard
            EngineContext.Current.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            if (newAttr.OwnerElement != null && newAttr.OwnerElement != this)
                throw new DomException("InUseAttributeError", "Attribute is already in use by another element");

            var result = _attributes.SetNamedItem(newAttr);
            
            // Sync Legacy Dictionaries
            SyncAttributeToLegacyStorage(newAttr);
            
            NotifyMutation(new MutationRecord
            {
                Type = "attributes",
                Target = this,
                AttributeName = newAttr.Name.ToLowerInvariant()
            });

            return result;
        }

        public Attr RemoveAttributeNode(Attr oldAttr)
        {
            if (oldAttr == null) throw new ArgumentNullException(nameof(oldAttr));
            if (oldAttr.OwnerElement != this) 
                throw new DomException("NotFoundError", "Attribute does not belong to this element");

            // Phase Guard
            EngineContext.Current.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            _attributes.RemoveNamedItem(oldAttr.Name);
            
            // Sync Legacy Dictionaries
            var key = oldAttr.Name.Trim().ToLowerInvariant();
            Attributes.Remove(key);
            AttributesRaw.Remove(oldAttr.Name); // Or check original names?
            _attrOriginalNames.Remove(key);
            
            NotifyMutation(new MutationRecord
            {
                Type = "attributes",
                Target = this,
                AttributeName = key
            });

            return oldAttr;
        }

        private void SyncAttributeToLegacyStorage(Attr attr)
        {
            if (attr == null) return;
            var key = attr.Name.Trim().ToLowerInvariant();
            var originalName = attr.Name;
            
            // Handle rename collision if needed (ignored for now)
            
            Attributes[key] = attr.Value;
            AttributesRaw[originalName] = attr.Value;
            _attrOriginalNames[key] = originalName;
        }

        // Shadow DOM support
        public Element ShadowRoot { get; set; }
        
        // Template support
        private DocumentFragment _templateContent;
        public DocumentFragment Content
        {
            get
            {
                if (!string.Equals(TagName, "TEMPLATE", StringComparison.OrdinalIgnoreCase)) return null;
                if (_templateContent == null)
                {
                    _templateContent = new DocumentFragment();
                    foreach(var child in Children)
                    {
                        _templateContent.AppendChild(child.CloneNode(true));
                    }
                }
                return _templateContent;
            }
        }
        
        // ---- Cloning ---------------------------------------------------------

        public override Node CloneNode(bool deep)
        {
            var c = new Element(this.TagName);
            foreach (var kv in Attributes)
            {
                c.Attributes[kv.Key] = kv.Value;
                c.AttributesRaw[kv.Key] = AttributesRaw.TryGetValue(kv.Key, out var raw) ? raw : kv.Value;
            }

            if (deep)
            {
                foreach (var child in Children)
                {
                    c.AppendChild(child.CloneNode(true));
                }
            }
            return c;
        }

        public Element ShallowClone() => (Element)CloneNode(false);
        public Element DeepClone() => (Element)CloneNode(true);

        // ---- Query Selectors -------------------------------------------------

        public Element QuerySelector(string selector)
        {
            var all = QuerySelectorAll(selector);
            return all.Length > 0 ? all[0] : null;
        }

        public Element[] QuerySelectorAll(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) return new Element[0];
            selector = selector.Trim();

            if (selector.StartsWith("#"))
            {
                var id = selector.Substring(1);
                var hit = FindById(id);
                return hit == null ? new Element[0] : new[] { hit };
            }

            if (selector.StartsWith("."))
            {
                var cls = selector.Substring(1);
                // Basic traversal for class
                var list = new List<Element>();
                foreach(var d in Descendants().OfType<Element>())
                {
                    if (d.HasClass(cls)) list.Add(d);
                }
                return list.ToArray();
            }

            var result = new List<Element>();
            foreach (var n in Descendants().OfType<Element>())
            {
                if (MatchesSimpleSelector(n, selector)) result.Add(n);
            }
            return result.ToArray();
        }

        public Element FindById(string id)
        {
             foreach(var d in Descendants().OfType<Element>())
             {
                 if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
             }
             return null;
        }

        private static bool MatchesSimpleSelector(Element n, string sel)
        {
             if (string.IsNullOrWhiteSpace(sel) || n == null) return false;

             // Descendant combinator support
            if (sel.IndexOf(' ') >= 0)
            {
                var parts = sel.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (!MatchesSimpleSelector(n, parts[parts.Length - 1])) return false;
                
                int k = parts.Length - 2;
                var anc = n.Ancestors().OfType<Element>().ToList();
                for (int i = 0; i < anc.Count && k >= 0; i++)
                {
                    if (MatchesSimpleSelector(anc[i], parts[k])) k--;
                }
                return k < 0;
            }

            // Tag and Attribute selector regex
            // Simplified from HtmlLite
             var m = System.Text.RegularExpressions.Regex.Match(sel,
                @"^(?:(?<tag>[a-zA-Z0-9_-]+))?(?:\[(?<attr>[a-zA-Z0-9_-]+)(?:(?<op>\^=|\$=|\*=|=)(?:'(?<val1>[^']*)'|""(?<val2>[^""]*)""))?\])?$");
            
            if (!m.Success)
                return string.Equals(n.TagName, sel, StringComparison.OrdinalIgnoreCase);

            var tag = m.Groups["tag"].Value;
            var attr = m.Groups["attr"].Value;
            var op = m.Groups["op"].Value;
            // val1 or val2
            var val = m.Groups["val1"].Success ? m.Groups["val1"].Value : (m.Groups["val2"].Success ? m.Groups["val2"].Value : null);

            if (!string.IsNullOrEmpty(tag) && !string.Equals(n.TagName, tag, StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.IsNullOrEmpty(attr)) return true;

            var av = n.GetAttribute(attr);
            if (av == null) return false;
            
            if (string.IsNullOrEmpty(op)) return val == null ? true : string.Equals(av, val, StringComparison.Ordinal);

            switch (op)
            {
                case "^=": return av.StartsWith(val, StringComparison.Ordinal);
                case "$=": return av.EndsWith(val, StringComparison.Ordinal);
                case "*=": return av.IndexOf(val, StringComparison.Ordinal) >= 0;
                case "=": return string.Equals(av, val, StringComparison.Ordinal);
                default: return false;
            }
        }
        
        // ---- Helpers ----
        
        public string CollectText()
        {
             var sb = new StringBuilder();
             foreach(var child in Children)
             {
                 if (child is Text t) sb.Append(t.Data);
                 else if (child is Element e) sb.Append(e.CollectText());
             }
             return sb.ToString();
        }

        public string Path()
        {
            var stack = new Stack<string>();
            for (Node n = this; n != null; n = n.Parent)
            {
                stack.Push(n is Element e ? e.TagName : n.NodeName);
            }
            return string.Join(" > ", stack);
        }
        
        // --- DOM Living Standard: Selector Methods (10/10 Spec) ---
        
        /// <summary>
        /// DOM Living Standard: Returns true if this element matches the given CSS selector.
        /// </summary>
        public bool Matches(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) return false;
            
            // Use existing internal Matches helper
            return MatchesSimpleSelector(this, selector);
        }
        
        /// <summary>
        /// DOM Living Standard: Returns the closest ancestor (or self) that matches the selector.
        /// </summary>
        public Element Closest(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) return null;
            
            Element current = this;
            while (current != null)
            {
                if (MatchesSimpleSelector(current, selector))
                    return current;
                    
                current = current.Parent as Element;
            }
            
            return null;
        }
        
        // --- DOM Living Standard: Dataset (data-* attributes) ---
        
        private StringMap _dataset;
        
        /// <summary>
        /// DOM Living Standard: Returns a StringMap providing access to data-* attributes.
        /// </summary>
        public StringMap Dataset
        {
            get
            {
                _dataset ??= new StringMap(this);
                return _dataset;
            }
        }
    }
    
    /// <summary>
    /// DOM Living Standard: StringMap for data-* attributes.
    /// Maps camelCase JS names to kebab-case HTML attributes.
    /// </summary>
    public class StringMap : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly Element _element;
        
        public StringMap(Element element)
        {
            _element = element;
        }
        
        public string this[string name]
        {
            get => GetValue(name);
            set => SetValue(name, value);
        }
        
        public string GetValue(string camelCaseName)
        {
            if (string.IsNullOrWhiteSpace(camelCaseName)) return null;
            
            var attrName = "data-" + ToKebabCase(camelCaseName);
            return _element.GetAttribute(attrName);
        }
        
        public void SetValue(string camelCaseName, string value)
        {
            if (string.IsNullOrWhiteSpace(camelCaseName)) return;
            
            var attrName = "data-" + ToKebabCase(camelCaseName);
            if (value == null)
                _element.RemoveAttribute(attrName);
            else
                _element.SetAttribute(attrName, value);
        }
        
        public bool Contains(string camelCaseName)
        {
            if (string.IsNullOrWhiteSpace(camelCaseName)) return false;
            
            var attrName = "data-" + ToKebabCase(camelCaseName);
            return _element.HasAttribute(attrName);
        }
        
        public void Remove(string camelCaseName)
        {
            if (string.IsNullOrWhiteSpace(camelCaseName)) return;
            
            var attrName = "data-" + ToKebabCase(camelCaseName);
            _element.RemoveAttribute(attrName);
        }
        
        /// <summary>
        /// Enumerate all data-* attributes as camelCase name/value pairs.
        /// </summary>
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            foreach (var kv in _element.Attributes)
            {
                if (kv.Key.StartsWith("data-", StringComparison.OrdinalIgnoreCase))
                {
                    var camelName = ToCamelCase(kv.Key.Substring(5));
                    yield return new KeyValuePair<string, string>(camelName, kv.Value);
                }
            }
        }
        
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        
        /// <summary>
        /// Convert camelCase to kebab-case.
        /// Example: "userName" -> "user-name"
        /// </summary>
        private static string ToKebabCase(string camelCase)
        {
            if (string.IsNullOrEmpty(camelCase)) return camelCase;
            
            var sb = new StringBuilder();
            foreach (char c in camelCase)
            {
                if (char.IsUpper(c) && sb.Length > 0)
                {
                    sb.Append('-');
                    sb.Append(char.ToLower(c));
                }
                else
                {
                    sb.Append(char.ToLower(c));
                }
            }
            return sb.ToString();
        }
        
        /// <summary>
        /// Convert kebab-case to camelCase.
        /// Example: "user-name" -> "userName"
        /// </summary>
        private static string ToCamelCase(string kebabCase)
        {
            if (string.IsNullOrEmpty(kebabCase)) return kebabCase;
            
            var sb = new StringBuilder();
            bool nextUpper = false;
            foreach (char c in kebabCase)
            {
                if (c == '-')
                {
                    nextUpper = true;
                }
                else if (nextUpper)
                {
                    sb.Append(char.ToUpper(c));
                    nextUpper = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
