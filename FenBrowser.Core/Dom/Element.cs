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

        // Attribute storage
        public Dictionary<string, string> Attributes { get; private set; }
        public Dictionary<string, string> AttributesRaw { get; private set; }
        private readonly Dictionary<string, string> _attrOriginalNames;

        public Element(string tagName)
        {
            TagName = (tagName ?? "DIV").ToUpperInvariant(); // Canonical upper case for HTML
            Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AttributesRaw = new Dictionary<string, string>(StringComparer.Ordinal);
            _attrOriginalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            yield return this;
            foreach (var child in Children)
            {
                if (child is Element el)
                {
                    foreach (var desc in el.SelfAndDescendants())
                        yield return desc;
                }
            }
        }


        public string Id
        {
            get => GetAttribute("id");
            set => SetAttribute("id", value);
        }

        public IEnumerable<string> ClassList
        {
            get
            {
                var v = GetAttribute("class");
                if (string.IsNullOrEmpty(v)) return Enumerable.Empty<string>();
                return v.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
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
            if (Attributes == null || string.IsNullOrWhiteSpace(name)) return null;
            return Attributes.TryGetValue(name, out var v) ? v : null;
        }

        public override void SetAttribute(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            
            // Phase Guard: No DOM writes during Layout/Paint
            EngineContext.Current.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            var trimmed = name.Trim();
            var key = trimmed.ToLowerInvariant();
            var val = value ?? string.Empty;

            string previousOriginal;
            _attrOriginalNames.TryGetValue(key, out previousOriginal);
            var originalName = string.IsNullOrEmpty(previousOriginal) ? trimmed : previousOriginal;

            if (!string.Equals(previousOriginal, originalName, StringComparison.Ordinal) && !string.IsNullOrEmpty(previousOriginal))
            {
                AttributesRaw.Remove(previousOriginal);
            }

            Attributes[key] = val;
            _attrOriginalNames[key] = originalName;
            AttributesRaw[originalName] = val;
            
            // Notify MutationObserver (Attributes)
            OnMutation?.Invoke(this, "attributes", key, null, null, null);
        }

        public override bool HasAttribute(string name)
        {
            return Attributes.ContainsKey(name);
        }

        public override bool RemoveAttribute(string name)
        {
             if (string.IsNullOrWhiteSpace(name)) return false;

            // Phase Guard
            EngineContext.Current.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            var key = name.Trim().ToLowerInvariant();
            string original;
            _attrOriginalNames.TryGetValue(key, out original);
            var removed = Attributes.Remove(key);
            if (!string.IsNullOrEmpty(original)) AttributesRaw.Remove(original);
            _attrOriginalNames.Remove(key);
            
            if (removed) OnMutation?.Invoke(this, "attributes", key, null, null, null);
            return removed;
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
                        // TODO: Deep clone
                       // _templateContent.AppendChild(child.Clone(true));
                       // For now just moving them might be wrong, need clone logic.
                       // Leaving as placeholder for simple refactor.
                    }
                }
                return _templateContent;
            }
        }
        
        // ---- Cloning ---------------------------------------------------------

        public Element ShallowClone()
        {
            var c = new Element(this.TagName);
            foreach (var kv in Attributes)
            {
                c.Attributes[kv.Key] = kv.Value;
                c.AttributesRaw[kv.Key] = AttributesRaw.TryGetValue(kv.Key, out var raw) ? raw : kv.Value;
            }
            return c;
        }

        public Element DeepClone()
        {
            var c = ShallowClone();
            foreach (var child in Children)
            {
                if (child is Element el) c.AppendChild(el.DeepClone());
                else if (child is Text txt) c.AppendChild(new Text(txt.Data));
                else if (child is Comment com) c.AppendChild(new Comment(com.Data));
            }
            return c;
        }

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
    }
}
