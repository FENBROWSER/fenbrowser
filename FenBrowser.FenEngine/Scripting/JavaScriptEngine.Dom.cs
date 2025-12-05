using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.FenEngine.Core;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Scripting
{
    public sealed partial class JavaScriptEngine
    {
        // --------------------------- Document / Element bridge (minimal) ---------------------------
        internal sealed class JsDocument
        {
            private readonly JavaScriptEngine _e;
            private LiteElement _root;
            public JsDocument(JavaScriptEngine e, LiteElement root) { _e = e; _root = root; }

            public object getElementById(string id)
            {
                if (string.IsNullOrEmpty(id) || _root == null) return null;
                foreach (var n in _root.Descendants())
                {
                    if (n.Attr != null)
                    {
                        string v; if (n.Attr.TryGetValue("id", out v) && string.Equals(v, id, StringComparison.Ordinal)) return new JsDomElement(_e, n);
                    }
                }
                return null;
            }

            public object[] getElementsByTagName(string tag)
            {
                if (string.IsNullOrEmpty(tag) || _root == null) return new object[0];
                var list = new List<object>();
                foreach (var n in _root.Descendants())
                    if (!n.IsText && string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase))
                        list.Add(new JsDomElement(_e, n));
                return list.ToArray();
            }

            public object querySelector(string sel)
            {
                var all = querySelectorAll(sel);
                return all != null && all.Length > 0 ? all[0] : null;
            }

            public object[] querySelectorAll(string sel)
            {
                if (string.IsNullOrWhiteSpace(sel) || _root == null) return new object[0];
                sel = sel.Trim();
                var list = new List<object>();
                if (sel.StartsWith("#"))
                {
                    var id = sel.Substring(1);
                    var e1 = getElementById(id);
                    return e1 == null ? new object[0] : new[] { e1 };
                }
                else if (sel.StartsWith("."))
                {
                    var cls = sel.Substring(1);
                    foreach (var n in _root.Descendants())
                    {
                        string v;
                        if (n.Attr != null && n.Attr.TryGetValue("class", out v) && !string.IsNullOrWhiteSpace(v))
                        {
                            var parts = v.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            for (int i = 0; i < parts.Length; i++)
                                if (string.Equals(parts[i], cls, StringComparison.Ordinal)) { list.Add(new JsDomElement(_e, n)); break; }
                        }
                    }
                }
                else
                {
                    if (sel.Contains(" "))
                    {
                        var parts = sel.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        var current = new List<LiteElement> { _root };
                        foreach (var p in parts)
                        {
                            var next = new List<LiteElement>();
                            foreach (var c in current)
                                foreach (var d in c.Descendants())
                                    if (MatchesSimpleSelector(d, p)) next.Add(d);
                            current = next;
                        }
                        foreach (var it in current) list.Add(new JsDomElement(_e, it));
                    }
                    else
                    {
                        foreach (var n in _root.Descendants()) if (MatchesSimpleSelector(n, sel)) list.Add(new JsDomElement(_e, n));
                    }
                }
                return list.ToArray();
            }

            internal static bool MatchesSimpleSelector(LiteElement n, string sel)
            {
                if (string.IsNullOrWhiteSpace(sel)) return false;
                if (sel.Contains("+") || sel.Contains("~"))
                {
                    var parts = sel.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 && (parts[1] == "+" || parts[1] == "~"))
                    {
                        var a = parts[0];
                        var op = parts[1];
                        var b = parts[2];
                        if (!MatchesSimpleSelector(n, b)) return false;
                        var parent = n.Parent;
                        if (parent == null) return false;
                        var siblings = parent.Children;
                        int idx = siblings.IndexOf(n);
                        if (idx <= 0) return false;
                        if (op == "+")
                        {
                            var prev = siblings[idx - 1];
                            return MatchesSimpleSelector(prev, a);
                        }
                        else
                        {
                            for (int i = 0; i < idx; i++) if (MatchesSimpleSelector(siblings[i], a)) return true;
                            return false;
                        }
                    }
                }

                var m = System.Text.RegularExpressions.Regex.Match(sel,
                    @"^(?:(?<tag>[a-zA-Z0-9_-]+))?(?:\[(?<attr>[a-zA-Z0-9_-]+)(?:(?<op>\^=|\$=|\*=|~=|\|=|=)(?:'(?<val1>[^']*)'|""(?<val2>[^""]*)""))?\])?$");
                if (m.Success)
                {
                    var tag = m.Groups["tag"].Value;
                    var attr = m.Groups["attr"].Value;
                    var op = m.Groups["op"].Value;
                    var val = m.Groups["val1"].Success ? m.Groups["val1"].Value : (m.Groups["val2"].Success ? m.Groups["val2"].Value : null);
                    if (!string.IsNullOrEmpty(tag) && !string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase)) return false;
                    if (string.IsNullOrEmpty(attr)) return true;
                    if (n.Attr == null) return false;
                    string av; if (!n.Attr.TryGetValue(attr, out av)) return false;
                    if (string.IsNullOrEmpty(op))
                    {
                        if (val == null) return !string.IsNullOrEmpty(av);
                        return string.Equals(av, val, StringComparison.Ordinal);
                    }
                    switch (op)
                    {
                        case "^=": return av != null && av.StartsWith(val, StringComparison.Ordinal);
                        case "$=": return av != null && av.EndsWith(val, StringComparison.Ordinal);
                        case "*=": return av != null && av.IndexOf(val, StringComparison.Ordinal) >= 0;
                        case "~=":
                            {
                                var parts = (av ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var p in parts) if (string.Equals(p, val, StringComparison.Ordinal)) return true;
                                return false;
                            }
                        case "|=":
                            return av != null && (string.Equals(av, val, StringComparison.Ordinal) || (av.StartsWith(val + "-", StringComparison.Ordinal)));
                        case "=":
                            return string.Equals(av, val, StringComparison.Ordinal);
                        default:
                            return false;
                    }
                }
                return string.Equals(n.Tag, sel, StringComparison.OrdinalIgnoreCase);
            }

            public JsDomElement createElement(string tag)
            {
                if (string.IsNullOrWhiteSpace(tag)) return null;
                var el = new LiteElement(tag.ToLowerInvariant());
                return new JsDomElement(_e, el);
            }

            public JsDomText createTextNode(string text)
            {
                var t = new LiteElement("#text");
                t.Text = text ?? "";
                return new JsDomText(_e, t);
            }

            public JsDomElement body
            {
                get
                {
                    foreach (var n in _root.Children) if (n.Tag == "body") return new JsDomElement(_e, n);
                    return new JsDomElement(_e, _root);
                }
            }

            public void appendChild(object child)
            {
                if (!_e.SandboxAllows(SandboxFeature.DomMutation, "document.appendChild")) return;
                var j = child as JsDomNodeBase;
                if (j == null) return;
                var host = body;
                host._node.Children.Add(j._node);
                try
                {
                    string __tmpId = null;
                    var name = j._node.Tag == "#text" ? (j._node.Text ?? "") : (j._node.Attr != null && j._node.Attr.TryGetValue("id", out __tmpId) ? "#" + __tmpId : j._node.Tag);
                    lock (_e._mutationLock)
                    {
                        _e._pendingMutations.Add(new MutationRecord { Type = "childList", Added = new List<string> { name }, Removed = new List<string>() });
                    }
                }
                catch { }
                _e.RequestRepaint();
            }

            public void removeChild(object child)
            {
                if (!_e.SandboxAllows(SandboxFeature.DomMutation, "document.removeChild")) return;
                var j = child as JsDomNodeBase;
                if (j == null) return;
                var host = body;
                try
                {
                    if (host != null && host._node != null)
                    {
                        host._node.Children.Remove(j._node);
                        lock (_e._mutationLock)
                        {
                            string __tmpId3 = null;
                            var name = j._node.Tag == "#text" ? (j._node.Text ?? "") : (j._node.Attr != null && j._node.Attr.TryGetValue("id", out __tmpId3) ? "#" + __tmpId3 : j._node.Tag);
                            _e._pendingMutations.Add(new MutationRecord { Type = "childList", Added = new List<string>(), Removed = new List<string> { name } });
                        }
                        _e.RequestRepaint();
                    }
                }
                catch { }
            }
        }

        internal abstract class JsDomNodeBase
        {
            protected readonly JavaScriptEngine _e;
            internal readonly LiteElement _node;
            protected JsDomNodeBase(JavaScriptEngine e, LiteElement n) { _e = e; _node = n; }
        }

        internal sealed class JsDomText : JsDomNodeBase
        {
            public JsDomText(JavaScriptEngine e, LiteElement n) : base(e, n) { }
            public string nodeType => "text";
            public string data { get { return _node.Text ?? ""; } set { _node.Text = value ?? ""; } }
        }

        internal sealed class JsDomElement : JsDomNodeBase
        {
            public JsDomElement(JavaScriptEngine e, LiteElement n) : base(e, n) { }
            public string tagName => (_node.Tag ?? "").ToUpperInvariant();

            public string id
            {
                get
                {
                    if (_node.Attr == null) return null;
                    string v;
                    return _node.Attr.TryGetValue("id", out v) ? v : null;
                }
                set
                {
                    try { _node.SetAttribute("id", value ?? ""); } catch { }
                }
            }

            public string textContent { get { return innerText; } set { innerText = value; } }

            public string innerText
            {
                get { return CollectText(_node); }
                set
                {
                    _node.Children.Clear();
                    var textNode = new LiteElement("#text") { Text = value ?? "" };
                    _node.Children.Add(textNode);
                    try
                    {
                        lock (_e._mutationLock)
                        {
                            _e._pendingMutations.Add(new MutationRecord
                            {
                                Type = "childList",
                                Added = new List<string> { value ?? "" },
                                Removed = new List<string>()
                            });
                        }
                    }
                    catch { }
                    _e.RequestRepaint();
                }
            }

            public string innerHTML
            {
                get
                {
                    try { return SerializeChildren(_node); } catch { return innerText; }
                }
                set
                {
                    try
                    {
                        if (!_e.SandboxAllows(SandboxFeature.DomMutation, "innerHTML")) return;
                        var html = value ?? string.Empty;
                        var parser = new HtmlLiteParser(html);
                        var doc = parser.Parse();
                        LiteElement container = null;
                        try
                        {
                            container = (doc.QueryByTag("body") ?? System.Linq.Enumerable.Empty<LiteElement>()).FirstOrDefault();
                            if (container == null) container = doc;
                        }
                        catch { container = doc; }

                        _node.RemoveAllChildren();
                        foreach (var ch in container.Children)
                        {
                            var clone = CloneTree(ch);
                            _node.Append(clone);
                        }
                        if (_e.ExecuteInlineScriptsOnInnerHTML)
                        {
                            try
                            {
                                foreach (var s in _node.SelfAndDescendants())
                                {
                                    if (!string.Equals(s.Tag, "script", StringComparison.OrdinalIgnoreCase)) continue;
                                    if (s.Attr != null && s.Attr.ContainsKey("src")) continue;
                                    var code = s.CollectText();
                                    if (!string.IsNullOrWhiteSpace(code))
                                    {
                                        try { _e.RunInline(code, new JsContext { BaseUri = _e._ctx?.BaseUri }); } catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                    _e.RequestRepaint();
                }
            }

            public void setInnerHTML(string html, bool executeScripts)
            {
                bool prev = _e.ExecuteInlineScriptsOnInnerHTML;
                try
                {
                    _e.ExecuteInlineScriptsOnInnerHTML = executeScripts;
                    this.innerHTML = html ?? string.Empty;
                }
                finally { _e.ExecuteInlineScriptsOnInnerHTML = prev; }
            }

            public void insertAdjacentHTML(string position, string html)
            {
                try
                {
                    var pos = (position ?? "").Trim().ToLowerInvariant();
                    var parser = new HtmlLiteParser(html ?? string.Empty);
                    var frag = parser.Parse();
                    LiteElement container = null;
                    try
                    {
                        container = (frag.QueryByTag("body") ?? System.Linq.Enumerable.Empty<LiteElement>()).FirstOrDefault();
                        if (container == null) container = frag;
                    }
                    catch { container = frag; }

                    if (pos == "afterbegin")
                    {
                        for (int i = container.Children.Count - 1; i >= 0; i--) _node.Children.Insert(0, CloneTree(container.Children[i]));
                    }
                    else if (pos == "beforeend")
                    {
                        for (int i = 0; i < container.Children.Count; i++) _node.Children.Add(CloneTree(container.Children[i]));
                    }
                    else if (pos == "beforebegin")
                    {
                        var parent = _node.Parent; if (parent != null)
                        {
                            var idx = parent.Children.IndexOf(_node);
                            for (int i = 0; i < container.Children.Count; i++) parent.Children.Insert(idx++, CloneTree(container.Children[i]));
                        }
                    }
                    else if (pos == "afterend")
                    {
                        var parent = _node.Parent; if (parent != null)
                        {
                            var idx = parent.Children.IndexOf(_node) + 1;
                            for (int i = 0; i < container.Children.Count; i++) parent.Children.Insert(idx++, CloneTree(container.Children[i]));
                        }
                    }
                    else
                    {
                        for (int i = 0; i < container.Children.Count; i++) _node.Children.Add(CloneTree(container.Children[i]));
                    }
                    _e.RequestRepaint();
                }
                catch { }
            }

            public void setAttribute(string name, string value)
            {
                if (!_e.SandboxAllows(SandboxFeature.DomMutation, "element.setAttribute")) return;
                if (string.IsNullOrWhiteSpace(name)) return;
                _node.SetAttribute(name, value);
                try
                {
                    lock (_e._mutationLock)
                    {
                        var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [name] = value ?? ""
                        };
                        _e._pendingMutations.Add(new MutationRecord { Type = "attributes", Attrs = changed });
                    }
                }
                catch { }
                _e.RequestRepaint();
            }

            public string getAttribute(string name)
            {
                if (_node.Attr == null || string.IsNullOrWhiteSpace(name)) return null;
                string v; return _node.Attr.TryGetValue(name, out v) ? v : null;
            }

            public void appendChild(object child)
            {
                if (!_e.SandboxAllows(SandboxFeature.DomMutation, "element.appendChild")) return;
                var j = child as JsDomNodeBase; if (j == null) return;
                _node.Children.Add(j._node);
                try
                {
                    string __tmpId2 = null;
                    var name = j._node.Tag == "#text" ? (j._node.Text ?? "") : (j._node.Attr != null && j._node.Attr.TryGetValue("id", out __tmpId2) ? "#" + __tmpId2 : j._node.Tag);
                    lock (_e._mutationLock) { _e._pendingMutations.Add(new MutationRecord { Type = "childList", Added = new List<string> { name }, Removed = new List<string>() }); }
                }
                catch { }
                _e.RequestRepaint();
            }

            public string value
            {
                get
                {
                    if (_node == null) return null;
                    var tag = (_node.Tag ?? "").ToLowerInvariant();
                    if (tag == "textarea") return CollectText(_node);
                    if (_node.Attr == null) return null;
                    string v; return _node.Attr.TryGetValue("value", out v) ? v : null;
                }
                set
                {
                    if (_node == null) return;
                    var tag = (_node.Tag ?? "").ToLowerInvariant();
                    if (tag == "textarea") { innerText = value ?? ""; return; }
                    _node.SetAttribute("value", value ?? "");
                    _e.RequestRepaint();
                }
            }

            public void removeChild(object child)
            {
                if (!_e.SandboxAllows(SandboxFeature.DomMutation, "element.removeChild")) return;
                var j = child as JsDomNodeBase; if (j == null) return;
                try
                {
                    _node.Children.Remove(j._node);
                    string __tmpId4 = null;
                    var name = j._node.Tag == "#text" ? (j._node.Text ?? "") : (j._node.Attr != null && j._node.Attr.TryGetValue("id", out __tmpId4) ? "#" + __tmpId4 : j._node.Tag);
                    lock (_e._mutationLock) { _e._pendingMutations.Add(new MutationRecord { Type = "childList", Added = new List<string>(), Removed = new List<string> { name } }); }
                }
                catch { }
                _e.RequestRepaint();
            }

            public object querySelector(string sel) { return new JsDocument(_e, _node).querySelector(sel); }
            public object[] querySelectorAll(string sel) { return new JsDocument(_e, _node).querySelectorAll(sel); }

            public object getContext(string contextType)
            {
                if (string.Equals(tagName, "CANVAS", StringComparison.OrdinalIgnoreCase) && 
                    string.Equals(contextType, "2d", StringComparison.OrdinalIgnoreCase))
                {
                    return new CanvasRenderingContext2D(_node, _e);
                }
                return null;
            }

            private static string CollectText(LiteElement n)
            {
                if (n == null) return "";
                if (n.IsText) return n.Text ?? "";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < n.Children.Count; i++) sb.Append(CollectText(n.Children[i]));
                return sb.ToString();
            }
            private static LiteElement CloneTree(LiteElement n)
            {
                if (n == null) return null;
                var c = new LiteElement(n.Tag);
                c.Text = n.Text;
                try
                {
                    if (n.Attr != null)
                    {
                        c.CopyAttributesFrom(n);
                    }
                }
                catch { }
                try
                {
                    for (int i = 0; i < n.Children.Count; i++)
                    {
                        var childClone = CloneTree(n.Children[i]);
                        if (childClone != null) c.Append(childClone);
                    }
                }
                catch { }
                return c;
            }

            private static string SerializeChildren(LiteElement n)
            {
                var sb = new System.Text.StringBuilder();
                try
                {
                    for (int i = 0; i < n.Children.Count; i++) SerializeNode(n.Children[i], sb);
                }
                catch { }
                return sb.ToString();
            }

            private static void SerializeNode(LiteElement n, System.Text.StringBuilder sb)
            {
                if (n == null || sb == null) return;
                if (n.IsText) { sb.Append(EscapeHtml(n.Text ?? "")); return; }
                var tag = n.Tag ?? "";
                sb.Append('<').Append(tag);
                try
                {
                    if (n.Attr != null)
                    {
                        foreach (var kv in n.Attr)
                        {
                            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                            var original = n.GetOriginalAttributeName(kv.Key) ?? kv.Key;
                            sb.Append(' ').Append(original).Append('=').Append('"').Append(EscapeHtml(kv.Value ?? "")).Append('"');
                        }
                    }
                }
                catch { }
                if (IsVoidTag(tag)) { sb.Append('/').Append('>'); return; }
                sb.Append('>');
                if (string.Equals(tag, "script", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "style", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "textarea", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        for (int i = 0; i < n.Children.Count; i++)
                        {
                            var ch = n.Children[i];
                            if (ch != null && ch.IsText) sb.Append(ch.Text ?? "");
                            else SerializeNode(ch, sb);
                        }
                    }
                    catch { }
                    sb.Append('<').Append('/').Append(tag).Append('>');
                    return;
                }

                if (n.Children != null)
                {
                    for (int i = 0; i < n.Children.Count; i++) SerializeNode(n.Children[i], sb);
                }
                sb.Append('<').Append('/').Append(tag).Append('>');
            }

            private static bool IsVoidTag(string tag)
            {
                if (string.IsNullOrEmpty(tag)) return false;
                switch (tag.ToLowerInvariant())
                {
                    case "area": case "base": case "br": case "col": case "embed": case "hr": case "img": case "input":
                    case "link": case "meta": case "param": case "source": case "track": case "wbr": return true;
                    default: return false;
                }
            }

            private static string EscapeHtml(string s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
            }
            private static void SanitizeForScriptingEnabled(JavaScriptEngine self, LiteElement rootArg = null)
            {
                if (self == null) return;
                var root = rootArg ?? self._domRoot;
                if (root == null) return;

                Action<LiteElement> flipClass = n =>
                {
                    if (n == null) return;
                    var attrs = n.Attr;
                    if (attrs == null) return;

                    string cls;
                    if (!attrs.TryGetValue("class", out cls) || string.IsNullOrWhiteSpace(cls)) return;

                    var parts = new HashSet<string>(
                        cls.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                        StringComparer.OrdinalIgnoreCase);

                    var changed = false;
                    if (parts.Remove("no-js")) changed = true;
                    if (!parts.Contains("js")) { parts.Add("js"); changed = true; }
                    if (changed) attrs["class"] = string.Join(" ", parts.ToArray());
                };

                try
                {
                    var html = (root.QueryByTag("html") ?? Enumerable.Empty<LiteElement>()).FirstOrDefault();
                    if (html != null) flipClass(html);
                    var body = (root.QueryByTag("body") ?? Enumerable.Empty<LiteElement>()).FirstOrDefault();
                    if (body != null) flipClass(body);
                }
                catch { }

                self.RequestRepaint();
            }
            public string className
            {
                get { return getAttribute("class") ?? ""; }
                set { setAttribute("class", value); }
            }

            public JsDomTokenList classList => new JsDomTokenList(this);
        }

        internal sealed class JsDomTokenList
        {
            private readonly JsDomElement _el;
            public JsDomTokenList(JsDomElement el) { _el = el; }

            public int length => _parts.Length;
            public string item(int index) { var p = _parts; return (index >= 0 && index < p.Length) ? p[index] : null; }
            public bool contains(string token) { return _parts.Contains(token, StringComparer.Ordinal); }
            
            public void add(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return;
                var p = new HashSet<string>(_parts, StringComparer.Ordinal);
                if (p.Add(token)) _el.className = string.Join(" ", p.ToArray());
            }

            public void remove(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return;
                var p = new HashSet<string>(_parts, StringComparer.Ordinal);
                if (p.Remove(token)) _el.className = string.Join(" ", p.ToArray());
            }

            public bool toggle(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                var p = new HashSet<string>(_parts, StringComparer.Ordinal);
                bool present = p.Contains(token);
                if (present) p.Remove(token); else p.Add(token);
                _el.className = string.Join(" ", p.ToArray());
                return !present;
            }

            public override string ToString() { return _el.className; }

            private string[] _parts => (_el.className ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
