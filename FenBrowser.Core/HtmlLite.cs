using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace FenBrowser.Core
{
    public class LiteElement
    {
        public string Tag { get; set; }
        public string Text { get; set; }

        // Keep dictionary instance stable (callers rely on mutability)
        public Dictionary<string, string> Attr { get; private set; }
        public Dictionary<string, string> AttrRaw { get; private set; }

        private readonly Dictionary<string, string> _attrOriginalNames;

        public List<LiteElement> Children { get; private set; }
        public List<LiteElement> ShadowRoot { get; set; }

        // Parent is set by Append/Insert helpers
        public LiteElement Parent { get; private set; }
        public LiteElement OwnerDocument { get; private set; }

        public LiteElement(string tag)
        {
            Tag = (tag ?? "#document").ToLowerInvariant();
            Attr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AttrRaw = new Dictionary<string, string>(StringComparer.Ordinal);
            _attrOriginalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Children = new List<LiteElement>();
            if (Tag == "#document")
            {
                OwnerDocument = this;
            }
        }

        public bool IsText { get { return Tag == "#text"; } }

        public string Id
        {
            get
            {
                string v;
                return Attr != null && Attr.TryGetValue("id", out v) ? v : null;
            }
        }

        public IEnumerable<string> Classes
        {
            get
            {
                string v;
                if (Attr != null && Attr.TryGetValue("class", out v) && !string.IsNullOrEmpty(v))
                    return v.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return new string[0]; // avoid Array.Empty for older targets
            }
        }

        // ---- DOM mutation helpers (non-breaking additions) --------------------

        public void Append(LiteElement child)
        {
            if (child == null || child == this) return;
            // avoid accidental cycles
            for (var p = this; p != null; p = p.Parent) if (ReferenceEquals(p, child)) return;
            child.Parent = this;
            child.OwnerDocument = this.OwnerDocument;
            Children.Add(child);
        }

        public void Prepend(LiteElement child)
        {
            if (child == null || child == this) return;
            for (var p = this; p != null; p = p.Parent) if (ReferenceEquals(p, child)) return;
            child.Parent = this;
            Children.Insert(0, child);
        }

        public void InsertBefore(LiteElement newNode, LiteElement referenceNode)
        {
            if (newNode == null || referenceNode == null) return;
            if (!ReferenceEquals(referenceNode.Parent, this)) return;
            var idx = Children.IndexOf(referenceNode);
            if (idx < 0) return;
            newNode.Parent = this;
            newNode.OwnerDocument = this.OwnerDocument;
            Children.Insert(idx, newNode);
        }

        public void InsertAfter(LiteElement newNode, LiteElement referenceNode)
        {
            if (newNode == null || referenceNode == null) return;
            if (!ReferenceEquals(referenceNode.Parent, this)) return;
            var idx = Children.IndexOf(referenceNode);
            if (idx < 0) return;
            newNode.Parent = this;
            newNode.OwnerDocument = this.OwnerDocument;
            Children.Insert(idx + 1, newNode);
        }

        public void Remove()
        {
            var p = Parent;
            if (p == null) return;
            p.Children.Remove(this);
            Parent = null;
        }

        public void RemoveAllChildren()
        {
            for (int i = 0; i < Children.Count; i++)
                Children[i].Parent = null;
            Children.Clear();
        }

        public int IndexInParent()
        {
            if (Parent == null) return -1;
            return Parent.Children.IndexOf(this);
        }

        public LiteElement PreviousSibling()
        {
            if (Parent == null) return null;
            var i = IndexInParent();
            return i > 0 ? Parent.Children[i - 1] : null;
        }

        public LiteElement NextSibling()
        {
            if (Parent == null) return null;
            var i = IndexInParent();
            return (i >= 0 && i + 1 < Parent.Children.Count) ? Parent.Children[i + 1] : null;
        }

        // ---- Attribute helpers (keep dictionary stable) ----------------------

        public bool HasAttribute(string name)
        {
            if (Attr == null || string.IsNullOrWhiteSpace(name)) return false;
            return Attr.ContainsKey(name);
        }

        public string GetAttribute(string name)
        {
            if (Attr == null || string.IsNullOrWhiteSpace(name)) return null;
            string v; return Attr.TryGetValue(name, out v) ? v : null;
        }

        public void SetAttribute(string name, string value)
        {
            if (Attr == null || string.IsNullOrWhiteSpace(name)) return;
            var trimmed = name.Trim();
            if (trimmed.Length == 0) return;

            var key = trimmed.ToLowerInvariant();
            var val = value ?? string.Empty;

            string previousOriginal;
            _attrOriginalNames.TryGetValue(key, out previousOriginal);

            var originalName = string.IsNullOrEmpty(previousOriginal) ? trimmed : previousOriginal;
            if (!string.Equals(previousOriginal, originalName, StringComparison.Ordinal) && !string.IsNullOrEmpty(previousOriginal))
            {
                AttrRaw.Remove(previousOriginal);
            }

            Attr[key] = val;
            _attrOriginalNames[key] = originalName;
            AttrRaw[originalName] = val;
        }

        public bool RemoveAttribute(string name)
        {
            if (Attr == null || string.IsNullOrWhiteSpace(name)) return false;
            var key = name.Trim().ToLowerInvariant();
            string original;
            _attrOriginalNames.TryGetValue(key, out original);
            var removed = Attr.Remove(key);
            if (!string.IsNullOrEmpty(original)) AttrRaw.Remove(original);
            _attrOriginalNames.Remove(key);
            return removed;
        }

        public void SetAttributeInternal(string lowerName, string originalName, string value, string rawValue)
        {
            if (Attr == null || string.IsNullOrWhiteSpace(lowerName) || string.IsNullOrWhiteSpace(originalName)) return;
            var key = lowerName.ToLowerInvariant();
            var val = value ?? string.Empty;
            var raw = rawValue ?? string.Empty;

            string previousOriginal;
            if (_attrOriginalNames.TryGetValue(key, out previousOriginal) && !string.Equals(previousOriginal, originalName, StringComparison.Ordinal))
            {
                AttrRaw.Remove(previousOriginal);
            }

            Attr[key] = val;
            _attrOriginalNames[key] = originalName;
            AttrRaw[originalName] = raw;
        }

        public string GetOriginalAttributeName(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized)) return null;
            string original;
            return _attrOriginalNames.TryGetValue(normalized.ToLowerInvariant(), out original) ? original : null;
        }

        public void CopyAttributesFrom(LiteElement other)
        {
            if (other == null || other.Attr == null) return;
            foreach (var kv in other.Attr)
            {
                var original = other.GetOriginalAttributeName(kv.Key) ?? kv.Key;
                string raw;
                if (!string.IsNullOrEmpty(original) && other.AttrRaw != null && other.AttrRaw.TryGetValue(original, out raw))
                    SetAttributeInternal(kv.Key, original, kv.Value, raw);
                else
                    SetAttributeInternal(kv.Key, original, kv.Value, kv.Value);
            }
        }

        public void AddClass(string cls)
        {
            if (string.IsNullOrWhiteSpace(cls)) return;
            var cur = GetAttribute("class") ?? "";
            var parts = cur.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!parts.Contains(cls, StringComparer.Ordinal)) parts.Add(cls);
            SetAttribute("class", string.Join(" ", parts));
        }

        public void RemoveClass(string cls)
        {
            if (string.IsNullOrWhiteSpace(cls)) return;
            var cur = GetAttribute("class") ?? "";
            var parts = cur.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                           .Where(c => !string.Equals(c, cls, StringComparison.Ordinal)).ToArray();
            SetAttribute("class", string.Join(" ", parts));
        }

        public void ToggleClass(string cls)
        {
            if (string.IsNullOrWhiteSpace(cls)) return;
            var cur = GetAttribute("class") ?? "";
            var parts = cur.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            int idx = parts.FindIndex(c => string.Equals(c, cls, StringComparison.Ordinal));
            if (idx >= 0) parts.RemoveAt(idx); else parts.Add(cls);
            SetAttribute("class", string.Join(" ", parts));
        }

        // ---- Traversal -------------------------------------------------------

        public IEnumerable<LiteElement> Descendants()
        {
            for (int i = 0; i < Children.Count; i++)
            {
                var c = Children[i];
                yield return c;
                foreach (var d in c.Descendants())
                    yield return d;
            }
        }

        public IEnumerable<LiteElement> SelfAndDescendants()
        {
            yield return this;
            foreach (var d in Descendants()) yield return d;
        }

        public IEnumerable<LiteElement> Ancestors()
        {
            var p = Parent;
            while (p != null) { yield return p; p = p.Parent; }
        }

        public IEnumerable<LiteElement> QueryByTag(params string[] tags)
        {
            var set = new HashSet<string>(tags.Select(t => t.ToLowerInvariant()));
            foreach (var n in Descendants())
                if (!n.IsText && set.Contains(n.Tag)) yield return n;
        }

        public LiteElement FindById(string id)
        {
            foreach (var n in Descendants())
                if (string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase))
                    return n;
            return null;
        }

        public IEnumerable<LiteElement> FindByClass(string cls)
        {
            foreach (var n in Descendants())
                if (n.Classes.Contains(cls, StringComparer.OrdinalIgnoreCase))
                    yield return n;
        }

        public IEnumerable<LiteElement> FindAll(Func<LiteElement, bool> predicate)
        {
            if (predicate == null) yield break;
            foreach (var n in Descendants())
                if (predicate(n)) yield return n;
        }

        public LiteElement FindFirst(Func<LiteElement, bool> predicate)
        {
            if (predicate == null) return null;
            foreach (var n in Descendants())
                if (predicate(n)) return n;
            return null;
        }

        // ---- Simple selector queries (id, class, tag, basic [attr]/[attr=value]) ----

        public LiteElement QuerySelector(string selector)
        {
            var all = QuerySelectorAll(selector);
            return all.Length > 0 ? all[0] : null;
        }

        public LiteElement[] QuerySelectorAll(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) return new LiteElement[0];
            selector = selector.Trim();

            // Very small subset – mirrors what JavaScriptEngine.JsDocument supports
            if (selector.StartsWith("#"))
            {
                var id = selector.Substring(1);
                var hit = FindById(id);
                return hit == null ? new LiteElement[0] : new[] { hit };
            }

            if (selector.StartsWith("."))
            {
                var cls = selector.Substring(1);
                return FindByClass(cls).ToArray();
            }

            // tag or tag[attr[=value]]
            var list = new List<LiteElement>();
            foreach (var n in Descendants())
            {
                if (n.IsText) continue;
                if (MatchesSimpleSelector(n, selector)) list.Add(n);
            }
            return list.ToArray();
        }

        private static bool MatchesSimpleSelector(LiteElement n, string sel)
        {
            if (string.IsNullOrWhiteSpace(sel) || n == null) return false;

            // Support basic descendant combinator: "div span"
            if (sel.IndexOf(' ') >= 0)
            {
                var parts = sel.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                IEnumerable<LiteElement> current = new[] { n };
                // We test by walking ancestors to see if n is matched by the last token
                // and its ancestors match previous tokens in order.
                // Simpler approach: verify last token matches n, then scan ancestors.
                if (!MatchesSimpleSelector(n, parts[parts.Length - 1])) return false;
                int k = parts.Length - 2;
                var anc = n.Ancestors().ToList();
                for (int i = 0; i < anc.Count && k >= 0; i++)
                {
                    if (MatchesSimpleSelector(anc[i], parts[k])) k--;
                }
                return k < 0;
            }

            // attribute selectors and tag
            // supported forms:
            // tag, tag[attr], tag[attr='val'], tag[attr^='val'], tag[attr$='val'], tag[attr*='val'], [attr], [attr='val']
            var m = System.Text.RegularExpressions.Regex.Match(sel,
                @"^(?:(?<tag>[a-zA-Z0-9_-]+))?(?:\[(?<attr>[a-zA-Z0-9_-]+)(?:(?<op>\^=|\$=|\*=|=)(?:'(?<val1>[^']*)'|""(?<val2>[^""]*)""))?\])?$");
            if (!m.Success)
            {
                // simple tag only
                return string.Equals(n.Tag, sel, StringComparison.OrdinalIgnoreCase);
            }

            var tag = m.Groups["tag"].Value;
            var attr = m.Groups["attr"].Value;
            var op = m.Groups["op"].Value;
            var val = m.Groups["val1"].Success ? m.Groups["val1"].Value : (m.Groups["val2"].Success ? m.Groups["val2"].Value : null);

            if (!string.IsNullOrEmpty(tag) && !string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.IsNullOrEmpty(attr)) return true;

            string av;
            if (n.Attr == null || !n.Attr.TryGetValue(attr, out av)) return false;

            if (string.IsNullOrEmpty(op)) return val == null ? !string.IsNullOrEmpty(av) : string.Equals(av, val, StringComparison.Ordinal);

            switch (op)
            {
                case "^=": return av != null && av.StartsWith(val, StringComparison.Ordinal);
                case "$=": return av != null && av.EndsWith(val, StringComparison.Ordinal);
                case "*=": return av != null && av.IndexOf(val, StringComparison.Ordinal) >= 0;
                case "=": return string.Equals(av, val, StringComparison.Ordinal);
                default: return false;
            }
        }

        // ---- Text helpers ----------------------------------------------------

        public string CollectText(bool decodeHtml = false)
        {
            if (IsText) return decodeHtml ? WebUtility.HtmlDecode(Text ?? "") : (Text ?? "");
            var sb = new StringBuilder();
            foreach (var c in Children) sb.Append(c.CollectText(decodeHtml));
            return sb.ToString();
        }

        public void SetTextContent(string text)
        {
            RemoveAllChildren();
            Append(TextNode(text));
        }

        // ---- Cloning ---------------------------------------------------------

        public LiteElement ShallowClone()
        {
            var c = new LiteElement(this.Tag);
            c.CopyAttributesFrom(this);
            c.Text = this.Text;
            return c;
        }

        public LiteElement DeepClone()
        {
            var c = ShallowClone();
            foreach (var ch in this.Children)
            {
                var cc = ch.DeepClone();
                c.Append(cc);
            }
            return c;
        }

        // Keep exactly ONE helper like this in your project to avoid CS0121 ambiguity.
        public static LiteElement TextNode(string text)
        {
            var t = new LiteElement("#text");
            t.Text = text;
            return t;
        }

        // ---- Diagnostics -----------------------------------------------------

        public override string ToString()
        {
            if (IsText) return "#text: " + (Text ?? "");
            var id = Id;
            if (!string.IsNullOrEmpty(id)) return "<" + Tag + "#" + id + ">";
            var cls = GetAttribute("class");
            if (!string.IsNullOrEmpty(cls)) return "<" + Tag + "." + cls.Replace(' ', '.') + ">";
            return "<" + Tag + ">";
        }

        public string ToHtml()
        {
            var sb = new StringBuilder();
            ToHtml(this, sb);
            return sb.ToString();
        }

        private static void ToHtml(LiteElement node, StringBuilder sb)
        {
            if (node.IsText)
            {
                sb.Append(WebUtility.HtmlEncode(node.Text));
                return;
            }

            sb.Append('<').Append(node.Tag);
            if (node.Attr != null)
            {
                foreach (var pair in node.Attr)
                {
                    var original = node.GetOriginalAttributeName(pair.Key) ?? pair.Key;
                    sb.Append(' ').Append(original).Append("=\"").Append(WebUtility.HtmlEncode(pair.Value)).Append('"');
                }
            }

            if (HtmlLiteParser.IsVoid(node.Tag))
            {
                sb.Append(" />");
                return;
            }

            sb.Append('>');

            var children = node.ShadowRoot ?? node.Children;
            if (children != null)
            {
                foreach (var child in children)
                {
                    ToHtml(child, sb);
                }
            }

            sb.Append("</").Append(node.Tag).Append('>');
        }

        public string Path()
        {
            var stack = new Stack<string>();
            for (var n = this; n != null; n = n.Parent)
            {
                stack.Push(n.ToString());
            }
            return string.Join(" > ", stack.ToArray());
        }
    }

}
