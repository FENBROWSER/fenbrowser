using FenBrowser.Core.Dom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting
{
    public sealed partial class JavaScriptEngine
    {
        // --------------------------- Document / Element bridge (minimal) ---------------------------
        internal sealed class JsDocument : IObject
        {
            private readonly JavaScriptEngine _e;
            private Element _root;
            private IObject _prototype = null;
            private JsDomElement _bodyCache = null;
            public object NativeObject { get; set; }
            public JsDocument(JavaScriptEngine e, Element root) { _e = e; _root = root; }

            public object getElementById(string id)
            {
                if (string.IsNullOrEmpty(id) || _root == null) return null;
                foreach (var n in _root.Descendants())
                {
                    if (n.Attr != null)
                    {
                        string v; if (n.Attr.TryGetValue("id", out v) && string.Equals(v, id, StringComparison.Ordinal) && n is Element el) return new JsDomElement(_e, el);
                    }
                }
                return null;
            }

            public object[] getElementsByTagName(string tag)
            {
                if (string.IsNullOrEmpty(tag) || _root == null) return new object[0];
                var list = new List<object>();
                foreach (var n in _root.Descendants().OfType<Element>())
                    if (!n.IsText && string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase))
                        list.Add(new JsDomElement(_e, n));
                return list.ToArray();
            }

            public object[] getElementsByClassName(string className)
            {
                if (string.IsNullOrWhiteSpace(className) || _root == null) return new object[0];
                var targetClasses = className.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (targetClasses.Length == 0) return new object[0];

                var list = new List<object>();
                foreach (var n in _root.Descendants().OfType<Element>())
                {
                    if (n.Attr != null && n.Attr.TryGetValue("class", out var cls) && !string.IsNullOrWhiteSpace(cls))
                    {
                        var elementClasses = cls.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        // Check if all target classes are present in element classes
                        bool match = true;
                        foreach (var target in targetClasses)
                        {
                            if (!elementClasses.Contains(target, StringComparer.Ordinal))
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match) list.Add(new JsDomElement(_e, n));
                    }
                }
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
                    foreach (var n in _root.Descendants().OfType<Element>())
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
                        var current = new List<Element> { _root };
                        foreach (var p in parts)
                        {
                            var next = new List<Element>();
                            foreach (var c in current)
                                foreach (var d in c.Descendants().OfType<Element>())
                                    if (MatchesSimpleSelector(d, p)) next.Add(d);
                            current = next;
                        }
                        foreach (var it in current) list.Add(new JsDomElement(_e, it));
                    }
                    else
                    {
                        foreach (var n in _root.Descendants().OfType<Element>()) if (MatchesSimpleSelector(n, sel)) list.Add(new JsDomElement(_e, n));
                    }
                }
                return list.ToArray();
            }

            internal static bool MatchesSimpleSelector(Element n, string sel)
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
                        var parent = n.Parent as Element;
                        if (parent == null) return false;
                        var siblings = parent.Children;
                        int idx = siblings.IndexOf(n); // Note: siblings is List<Node>, n is Element. Valid check.
                        if (idx <= 0) return false;
                        if (op == "+")
                        {
                            var prev = siblings[idx - 1] as Element;
                            return prev != null && MatchesSimpleSelector(prev, a);
                        }
                        else
                        {
                            for (int i = 0; i < idx; i++) if (siblings[i] is Element el && MatchesSimpleSelector(el, a)) return true;
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
                var el = new Element(tag.ToLowerInvariant());
                return new JsDomElement(_e, el);
            }

            public JsDomText createTextNode(string text)
            {
                var t = new Element("#text");
                t.Text = text ?? "";
                return new JsDomText(_e, t);
            }

            public JsDomElement body
            {
                get
                {
                    foreach (var n in _root.Children.OfType<Element>()) if (n.Tag == "body") return new JsDomElement(_e, n);
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
                        _e._pendingMutations.Add(new MutationRecord { Type = "childList", AddedNodes = new System.Collections.Generic.List<Node> { j._node }, RemovedNodes = new System.Collections.Generic.List<Node>() });
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
                            _e._pendingMutations.Add(new MutationRecord { Type = "childList", AddedNodes = new System.Collections.Generic.List<Node>(), RemovedNodes = new System.Collections.Generic.List<Node> { j._node } });
                        }
                        _e.RequestRepaint();
                    }
                }
                catch { }
            }

            // IObject implementation for interpreter property access
            // IObject implementation for interpreter property access
            public IValue Get(string key, IExecutionContext context = null)
            {
                switch (key)
                {
                    case "addEventListener": 
                        try { FenLogger.Debug("[JsDocument] Get addEventListener", LogCategory.JavaScript); } catch { }
                        return FenValue.FromFunction(new FenFunction("addEventListener", _e.AddEventListenerNative));
                    case "body":
                        if (_bodyCache == null)
                        {
                            foreach (var n in _root.Children.OfType<Element>())
                            {
                                if (n.Tag == "body") { _bodyCache = new JsDomElement(_e, n); break; }
                            }
                            if (_bodyCache == null) _bodyCache = new JsDomElement(_e, _root);
                        }
                        return FenValue.FromObject(_bodyCache);
                    case "getElementById": return FenValue.FromFunction(new FenFunction("getElementById", (args, _) => {
                        var result = args.Length > 0 ? getElementById(args[0].ToString()) : null;
                        return result is JsDomElement el ? FenValue.FromObject(el) : FenValue.Null;
                    }));
                    case "getElementsByTagName": return FenValue.FromFunction(new FenFunction("getElementsByTagName", (args, _) => {
                        var results = args.Length > 0 ? getElementsByTagName(args[0].ToString()) : new object[0];
                        var arr = new FenObject();
                        arr.Set("length", FenValue.FromNumber(results.Length));
                        for (int i = 0; i < results.Length; i++)
                        {
                            if (results[i] is JsDomElement el) arr.Set(i.ToString(), FenValue.FromObject(el));
                        }
                        return FenValue.FromObject(arr);
                    }));
                    case "querySelector": return FenValue.FromFunction(new FenFunction("querySelector", (args, _) => {
                        var result = args.Length > 0 ? querySelector(args[0].ToString()) : null;
                        return result is JsDomElement el ? FenValue.FromObject(el) : FenValue.Null;
                    }));
                    case "querySelectorAll": return FenValue.FromFunction(new FenFunction("querySelectorAll", (args, _) => {
                        var results = args.Length > 0 ? querySelectorAll(args[0].ToString()) : new object[0];
                        var arr = new FenObject();
                        arr.Set("length", FenValue.FromNumber(results.Length));
                        for (int i = 0; i < results.Length; i++)
                        {
                            if (results[i] is JsDomElement el) arr.Set(i.ToString(), FenValue.FromObject(el));
                        }
                        return FenValue.FromObject(arr);
                    }));
                    case "createElement": return FenValue.FromFunction(new FenFunction("createElement", (args, _) => {
                        var el = args.Length > 0 ? createElement(args[0].ToString()) : null;
                        return el != null ? FenValue.FromObject(el) : FenValue.Null;
                    }));
                    // createTextNode commented out - JsDomText doesn't implement IObject yet
                    // case "createTextNode": return FenValue.FromFunction(new FenFunction("createTextNode", (args, _) => FenValue.Null));
                    default:
                        return FenValue.Undefined;
                }
            }

            public void Set(string key, IValue value, IExecutionContext context = null) { /* Most document properties are read-only */ }
            public bool Has(string key, IExecutionContext context = null) => key == "body" || key == "getElementById" || key == "querySelector" || key == "querySelectorAll" || key == "addEventListener";
            public bool Delete(string key, IExecutionContext context = null) => false;
            public IEnumerable<string> Keys(IExecutionContext context = null) => new[] { "body", "getElementById", "getElementsByTagName", "querySelector", "querySelectorAll", "createElement", "createTextNode", "addEventListener" };
            public IObject GetPrototype() => _prototype;
            public void SetPrototype(IObject prototype) { _prototype = prototype; }
        }

        internal abstract class JsDomNodeBase
        {
            internal readonly JavaScriptEngine _e;
            internal readonly Element _node;
            protected JsDomNodeBase(JavaScriptEngine e, Element n) { _e = e; _node = n; }
        }

        internal sealed class JsDomText : JsDomNodeBase
        {
            public JsDomText(JavaScriptEngine e, Element n) : base(e, n) { }
            public string nodeType => "text";
            public string data { get { return _node.Text ?? ""; } set { _node.Text = value ?? ""; } }
        }

        internal sealed class JsDomElement : JsDomNodeBase, IObject
        {
            private IObject _prototype = null;
            private JsCssDeclaration _styleCache = null;
            public object NativeObject { get; set; }
            public JsDomElement(JavaScriptEngine e, Element n) : base(e, n) { }
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
                    var textNode = new Element("#text") { Text = value ?? "" };
                    _node.Children.Add(textNode);
                    try
                    {
                        lock (_e._mutationLock)
                        {
                            _e._pendingMutations.Add(new MutationRecord
                            {
                                Type = "childList",
                                AddedNodes = new System.Collections.Generic.List<Node> { textNode },
                                RemovedNodes = new System.Collections.Generic.List<Node>()
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
                        Element container = null;
                        try
                        {
                            container = (doc.QueryByTag("body") ?? System.Linq.Enumerable.Empty<Element>()).FirstOrDefault();
                            if (container == null) container = doc;
                        }
                        catch { container = doc; }

                        _node.RemoveAllChildren();
                        foreach (var ch in container.Children)
                        {
                            if (ch is Element chElement)
                            {
                                var clone = CloneTree(chElement);
                                _node.Append(clone);
                            }
                        }
                        if (_e.ExecuteInlineScriptsOnInnerHTML)
                        {
                            try
                            {
                                foreach (var s in _node.SelfAndDescendants().OfType<Element>())
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
                    Element container = null;
                    try
                    {
                        container = (frag.QueryByTag("body") ?? System.Linq.Enumerable.Empty<Element>()).FirstOrDefault();
                        if (container == null) container = frag;
                    }
                    catch { container = frag; }

                    if (pos == "afterbegin")
                    {
                        for (int i = container.Children.Count - 1; i >= 0; i--) if (container.Children[i] is Element el) _node.Children.Insert(0, CloneTree(el));
                    }
                    else if (pos == "beforeend")
                    {
                        for (int i = 0; i < container.Children.Count; i++) if (container.Children[i] is Element el) _node.Children.Add(CloneTree(el));
                    }
                    else if (pos == "beforebegin")
                    {
                        var parent = _node.Parent; if (parent != null)
                        {
                            var idx = parent.Children.IndexOf(_node);
                            for (int i = 0; i < container.Children.Count; i++) if (container.Children[i] is Element el) parent.Children.Insert(idx++, CloneTree(el));
                        }
                    }
                    else if (pos == "afterend")
                    {
                        var parent = _node.Parent; if (parent != null)
                        {
                            var idx = parent.Children.IndexOf(_node) + 1;
                            for (int i = 0; i < container.Children.Count; i++) if (container.Children[i] is Element el) parent.Children.Insert(idx++, CloneTree(el));
                        }
                    }
                    else
                    {
                        for (int i = 0; i < container.Children.Count; i++) if (container.Children[i] is Element el) _node.Children.Add(CloneTree(el));
                    }
                    _e.RequestRepaint();
                }
                catch { }
            }

            public void setAttribute(string name, string value)
            {
                if (!_e.SandboxAllows(SandboxFeature.DomMutation, "element.setAttribute")) return;
                if (string.IsNullOrWhiteSpace(name)) return;
                try { FenLogger.Debug($"[JsDomElement] setAttribute {name}='{value}' on {_node.Tag}", LogCategory.DOM); } catch {}
                _node.SetAttribute(name, value);
                try
                {
                    lock (_e._mutationLock)
                    {
                        _e._pendingMutations.Add(new MutationRecord { Type = "attributes", AttributeName = name, Target = _node });
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
                    lock (_e._mutationLock) { _e._pendingMutations.Add(new MutationRecord { Type = "childList", AddedNodes = new System.Collections.Generic.List<Node> { j._node }, RemovedNodes = new System.Collections.Generic.List<Node>() }); }
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
                    lock (_e._mutationLock) { _e._pendingMutations.Add(new MutationRecord { Type = "childList", AddedNodes = new System.Collections.Generic.List<Node>(), RemovedNodes = new System.Collections.Generic.List<Node> { j._node } }); }
                }
                catch { }
                _e.RequestRepaint();
            }

            public object querySelector(string sel) { return new JsDocument(_e, _node).querySelector(sel); }
            public object[] querySelectorAll(string sel) { return new JsDocument(_e, _node).querySelectorAll(sel); }
            public object[] getElementsByClassName(string className) { return new JsDocument(_e, _node).getElementsByClassName(className); }

            public void scrollIntoView()
            {
                if (_e._host != null)
                {
                    _e._host.ScrollToElement(_node);
                }
            }

            public object getContext(string contextType)
            {
                if (string.Equals(tagName, "CANVAS", StringComparison.OrdinalIgnoreCase) && 
                    string.Equals(contextType, "2d", StringComparison.OrdinalIgnoreCase))
                {
                    return new CanvasRenderingContext2D(_node, _e);
                }
                return null;
            }

            private static string CollectText(Element n)
            {
                if (n == null) return "";
                if (n.IsText) return n.Text ?? "";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < n.Children.Count; i++) if (n.Children[i] is Element el) sb.Append(CollectText(el));
                return sb.ToString();
            }
            private static Element CloneTree(Element n)
            {
                if (n == null) return null;
                var c = new Element(n.Tag);
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
                        var childClone = CloneTree(n.Children[i] as Element);
                        if (childClone != null) c.Append(childClone);
                    }
                }
                catch { }
                return c;
            }

            private static string SerializeChildren(Element n)
            {
                var sb = new System.Text.StringBuilder();
                try
                {
                    for (int i = 0; i < n.Children.Count; i++) if (n.Children[i] is Element el) SerializeNode(el, sb);
                }
                catch { }
                return sb.ToString();
            }

            private static void SerializeNode(Element n, System.Text.StringBuilder sb)
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
                            else if (ch is Element el) SerializeNode(el, sb);
                        }
                    }
                    catch { }
                    sb.Append('<').Append('/').Append(tag).Append('>');
                    return;
                }

                if (n.Children != null)
                {
                    for (int i = 0; i < n.Children.Count; i++) if (n.Children[i] is Element el) SerializeNode(el, sb);
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
            private static void SanitizeForScriptingEnabled(JavaScriptEngine self, Element rootArg = null)
            {
                if (self == null) return;
                var root = rootArg ?? self._domRoot;
                if (root == null) return;

                Action<Element> flipClass = n =>
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
                    var html = (root.QueryByTag("html") ?? Enumerable.Empty<Element>()).FirstOrDefault();
                    if (html != null) flipClass(html);
                    var body = (root.QueryByTag("body") ?? Enumerable.Empty<Element>()).FirstOrDefault();
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

            public JsCssDeclaration style => new JsCssDeclaration(this);

            public JsDomTokenList classList => new JsDomTokenList(this);

            // IObject implementation for interpreter property access
            public IValue Get(string key, IExecutionContext context = null)
            {
                switch (key)
                {
                    case "style":
                        if (_styleCache == null) _styleCache = new JsCssDeclaration(this);
                        return FenValue.FromObject(_styleCache);
                    case "tagName": return FenValue.FromString(tagName);
                    case "id": return FenValue.FromString(id ?? "");
                    case "className": return FenValue.FromString(className);
                    case "innerHTML": return FenValue.FromString(innerHTML);
                    case "innerText": return FenValue.FromString(innerText);
                    case "textContent": return FenValue.FromString(textContent);
                    // classList removed - JsDomTokenList doesn't implement IObject
                    case "getAttribute": return FenValue.FromFunction(new FenFunction("getAttribute", (args, _) => 
                        FenValue.FromString(args.Length > 0 ? getAttribute(args[0].ToString()) : "")));
                    case "setAttribute": return FenValue.FromFunction(new FenFunction("setAttribute", (args, _) => { 
                        if (args.Length >= 2) setAttribute(args[0].ToString(), args[1].ToString()); 
                        return FenValue.Undefined; 
                    }));
                    case "appendChild": return FenValue.FromFunction(new FenFunction("appendChild", (args, _) => { 
                        if (args.Length > 0) appendChild(args[0]); 
                        return FenValue.Undefined; 
                    }));
                    case "removeChild": return FenValue.FromFunction(new FenFunction("removeChild", (args, _) => { 
                        if (args.Length > 0) removeChild(args[0]); 
                        return FenValue.Undefined; 
                    }));
                    case "addEventListener": 
                        try { FenLogger.Debug("[JsDomElement] Get addEventListener", LogCategory.JavaScript); } catch { }
                        return FenValue.FromFunction(new FenFunction("addEventListener", _e.AddEventListenerNative));
                    // addEventListener/removeEventListener handled at engine level
                    case "querySelector": return FenValue.FromFunction(new FenFunction("querySelector", (args, _) => {
                        var result = args.Length > 0 ? querySelector(args[0].ToString()) : null;
                        return result is JsDomElement el ? FenValue.FromObject(el) : FenValue.Null;
                    }));
                    case "querySelectorAll": return FenValue.FromFunction(new FenFunction("querySelectorAll", (args, _) => {
                        var results = args.Length > 0 ? querySelectorAll(args[0].ToString()) : new object[0];
                        var arr = new FenObject();
                        arr.Set("length", FenValue.FromNumber(results.Length));
                        for (int i = 0; i < results.Length; i++)
                        {
                            if (results[i] is JsDomElement el) arr.Set(i.ToString(), FenValue.FromObject(el));
                        }
                        return FenValue.FromObject(arr);
                    }));
                    default:
                        // Try to get as attribute
                        var attr = getAttribute(key);
                        if (!string.IsNullOrEmpty(attr)) return FenValue.FromString(attr);
                        return FenValue.Undefined;
                }
            }

            public void Set(string key, IValue value, IExecutionContext context = null)
            {
                switch (key)
                {
                    case "id": id = value?.ToString() ?? ""; break;
                    case "className": className = value?.ToString() ?? ""; break;
                    case "innerHTML": innerHTML = value?.ToString() ?? ""; break;
                    case "innerText": innerText = value?.ToString() ?? ""; break;
                    case "textContent": textContent = value?.ToString() ?? ""; break;
                    default:
                        // Set as attribute
                        setAttribute(key, value?.ToString() ?? "");
                        break;
                }
            }

            public bool Has(string key, IExecutionContext context = null) => true;
            public bool Delete(string key, IExecutionContext context = null) => false;
            public IEnumerable<string> Keys(IExecutionContext context = null) => new[] { "style", "tagName", "id", "className", "innerHTML", "innerText", "textContent", "classList" };
            public IObject GetPrototype() => _prototype;
            public void SetPrototype(IObject prototype) { _prototype = prototype; }
        }

        internal sealed class JsCssDeclaration : IObject
        {
            private readonly JsDomElement _el;
            private IObject _prototype = null;
            public object NativeObject { get; set; }
            public JsCssDeclaration(JsDomElement el) { _el = el; }

            public string cssText
            {
                get { return _el.getAttribute("style") ?? ""; }
                set { _el.setAttribute("style", value ?? ""); }
            }

            public void setProperty(string name, string value)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                try { FenLogger.Debug($"[JsCssDeclaration] setProperty {name}='{value}'", LogCategory.DOM); } catch {}
                var current = cssText;
                var styles = ParseStyles(current);
                styles[name] = value ?? "";
                cssText = SerializeStyles(styles);
            }

            public string getPropertyValue(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return "";
                var current = cssText;
                var styles = ParseStyles(current);
                return styles.TryGetValue(name, out var val) ? val : "";
            }

            public string removeProperty(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return "";
                var current = cssText;
                var styles = ParseStyles(current);
                if (styles.TryGetValue(name, out var val))
                {
                    styles.Remove(name);
                    cssText = SerializeStyles(styles);
                    return val;
                }
                return "";
            }

            // IObject implementation for interpreter property access
            // IObject implementation for interpreter property access
            public IValue Get(string key, IExecutionContext context = null)
            {
                // Handle method calls
                if (key == "setProperty") return FenValue.FromFunction(new FenFunction("setProperty", (args, _) => { 
                    if (args.Length >= 2) setProperty(args[0].ToString(), args[1].ToString()); 
                    return FenValue.Undefined; 
                }));
                if (key == "getPropertyValue") return FenValue.FromFunction(new FenFunction("getPropertyValue", (args, _) => { 
                    return FenValue.FromString(args.Length > 0 ? getPropertyValue(args[0].ToString()) : ""); 
                }));
                if (key == "removeProperty") return FenValue.FromFunction(new FenFunction("removeProperty", (args, _) => { 
                    return FenValue.FromString(args.Length > 0 ? removeProperty(args[0].ToString()) : ""); 
                }));
                if (key == "cssText") return FenValue.FromString(cssText);
                
                // Handle common style properties, converted from camelCase or kebab-case
                var cssKey = ConvertToCssProperty(key);
                var value = getPropertyValue(cssKey);
                return FenValue.FromString(value);
            }

            public void Set(string key, IValue value, IExecutionContext context = null)
            {
                if (key == "cssText") { cssText = value?.ToString() ?? ""; return; }
                var cssKey = ConvertToCssProperty(key);
                setProperty(cssKey, value?.ToString() ?? "");
                _el._e.RequestRepaint();
            }

            public bool Has(string key, IExecutionContext context = null) => true; // All CSS properties are valid
            public bool Delete(string key, IExecutionContext context = null) => false; // CSS properties can't be deleted this way
            public IEnumerable<string> Keys(IExecutionContext context = null) => new[] { "cssText", "display", "width", "height", "background", "color", "margin", "padding", "border", "position", "top", "left", "opacity", "transform" };
            public IObject GetPrototype() => _prototype;
            public void SetPrototype(IObject prototype) { _prototype = prototype; }

            // Convert camelCase (backgroundColor) to kebab-case (background-color)
            private static string ConvertToCssProperty(string name)
            {
                if (string.IsNullOrEmpty(name)) return name;
                var sb = new StringBuilder();
                foreach (var c in name)
                {
                    if (char.IsUpper(c))
                    {
                        if (sb.Length > 0) sb.Append('-');
                        sb.Append(char.ToLower(c));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }

            // Common properties for direct access (e.g., element.style.display = 'none')
            public string display { get { return getPropertyValue("display"); } set { setProperty("display", value); } }
            public string width { get { return getPropertyValue("width"); } set { setProperty("width", value); } }
            public string height { get { return getPropertyValue("height"); } set { setProperty("height", value); } }
            public string top { get { return getPropertyValue("top"); } set { setProperty("top", value); } }
            public string left { get { return getPropertyValue("left"); } set { setProperty("left", value); } }
            public string position { get { return getPropertyValue("position"); } set { setProperty("position", value); } }
            public string opacity { get { return getPropertyValue("opacity"); } set { setProperty("opacity", value); } }
            public string transform { get { return getPropertyValue("transform"); } set { setProperty("transform", value); } }
            public string backgroundColor { get { return getPropertyValue("background-color"); } set { setProperty("background-color", value); } }
            public string color { get { return getPropertyValue("color"); } set { setProperty("color", value); } }
            public string background { get { return getPropertyValue("background"); } set { setProperty("background", value); } }
            public string margin { get { return getPropertyValue("margin"); } set { setProperty("margin", value); } }
            public string padding { get { return getPropertyValue("padding"); } set { setProperty("padding", value); } }
            public string border { get { return getPropertyValue("border"); } set { setProperty("border", value); } }

            private static Dictionary<string, string> ParseStyles(string styleAttr)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(styleAttr)) return dict;
                var parts = styleAttr.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    var idx = p.IndexOf(':');
                    if (idx > 0)
                    {
                        var key = p.Substring(0, idx).Trim();
                        var val = p.Substring(idx + 1).Trim();
                        if (!string.IsNullOrEmpty(key)) dict[key] = val;
                    }
                }
                return dict;
            }

            private static string SerializeStyles(Dictionary<string, string> styles)
            {
                if (styles == null || styles.Count == 0) return "";
                var sb = new StringBuilder();
                foreach (var kv in styles)
                {
                    sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("; ");
                }
                return sb.ToString().Trim();
            }
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

