using System;
using System.Linq;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Errors;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Represents the document object in JavaScript.
    /// Provides methods to query and manipulate the DOM.
    /// </summary>
    public class DocumentWrapper : IObject
    {
        private readonly LiteElement _root;
        private readonly IExecutionContext _context;
        private IObject _prototype;
        private string _readyState = "complete"; // Default, managed by SetReadyState

        public DocumentWrapper(LiteElement root, IExecutionContext context)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public IValue Get(string key)
        {
            _context?.CheckExecutionTimeLimit();

            switch (key.ToLowerInvariant())
            {
                case "getelementbyid":
                    return FenValue.FromFunction(new FenFunction("getElementById", GetElementById));

                case "queryselector":
                    return FenValue.FromFunction(new FenFunction("querySelector", QuerySelector));

                case "createelement":
                    return FenValue.FromFunction(new FenFunction("createElement", CreateElement));
                
                case "createdocumentfragment":
                    return FenValue.FromFunction(new FenFunction("createDocumentFragment", CreateDocumentFragment));
                
                case "createtextnode":
                    return FenValue.FromFunction(new FenFunction("createTextNode", CreateTextNode));
                
                case "createcomment":
                    return FenValue.FromFunction(new FenFunction("createComment", CreateComment));
                
                case "createevent":
                    return FenValue.FromFunction(new FenFunction("createEvent", CreateEvent));

                case "queryselectorall":
                    return FenValue.FromFunction(new FenFunction("querySelectorAll", QuerySelectorAll));
                
                case "getelementsbyclassname":
                    return FenValue.FromFunction(new FenFunction("getElementsByClassName", GetElementsByClassName));

                case "getelementsbytagname":
                    return FenValue.FromFunction(new FenFunction("getElementsByTagName", GetElementsByTagName));

                case "body":
                    var body = FindElementByTag(_root, "body");
                    return body != null ? FenValue.FromObject(new ElementWrapper(body, _context)) : FenValue.Null;

                case "head":
                    var head = FindElementByTag(_root, "head");
                    return head != null ? FenValue.FromObject(new ElementWrapper(head, _context)) : FenValue.Null;

                case "title":
                    // ... (existing title logic)
                    var titleEl = FindElementByTag(_root, "title");
                    return FenValue.FromString(titleEl?.Text ?? "");

                case "documentelement":
                    var htmlEl = FindElementByTag(_root, "html");
                    return htmlEl != null 
                        ? FenValue.FromObject(new ElementWrapper(htmlEl, _context))
                        : FenValue.FromObject(new ElementWrapper(_root, _context));

                case "activeelement":
                    var active = _root.ActiveElement;
                     // Default to body if no focus
                    if (active == null) active = FindElementByTag(_root, "body");
                    return active != null ? FenValue.FromObject(new ElementWrapper(active, _context)) : FenValue.Null;

                case "readystate":
                    return FenValue.FromString(_readyState);

                default:
                    return FenValue.Undefined;
            }
        }

        public void Set(string key, IValue value)
        {
            // document properties are mostly read-only
            // Could add support for document.title setter
        }

        public bool Has(string key) => !Get(key).IsUndefined;
        public bool Delete(string key) => false;
        public IEnumerable<string> Keys() 
            => new[] { "getElementById", "querySelector", "querySelectorAll", "createElement", "createDocumentFragment", "createTextNode", "createComment", "createEvent", "getElementsByClassName", "getElementsByTagName", "body", "head", "title", "documentElement", "readyState" };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        private IValue CreateElement(IValue[] args, IValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var tagName = args[0].ToString();
            
            var element = new LiteElement(tagName);
            return FenValue.FromObject(new ElementWrapper(element, _context));
        }

        private IValue GetElementById(IValue[] args, IValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomRead, "getElementById"))
                throw new FenSecurityError("DOM read permission required");

            if (args.Length == 0) return FenValue.Null;

            var id = args[0].ToString();
            try { System.IO.File.AppendAllText("debug_log.txt", $"[DocumentWrapper] getElementById searching for: {id}\r\n"); } catch { }
            var element = FindElementById(_root, id);

            if (element != null)
            {
                try { System.IO.File.AppendAllText("debug_log.txt", $"[DocumentWrapper] Found element: {element.Tag} (id={id})\r\n"); } catch { }
                return FenValue.FromObject(new ElementWrapper(element, _context));
            }
            else
            {
                try { System.IO.File.AppendAllText("debug_log.txt", $"[DocumentWrapper] Element NOT found: {id}\r\n"); } catch { }
                return FenValue.Null;
            }
        }

        private IValue QuerySelector(IValue[] args, IValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomRead, "querySelector"))
                throw new FenSecurityError("DOM read permission required");

            if (args.Length == 0) return FenValue.Null;

            var selector = args[0].ToString();

            var element = FindFirstSelector(_root, selector);
            return element != null 
                ? FenValue.FromObject(new ElementWrapper(element, _context))
                : FenValue.Null;
        }

        public void SetReadyState(string state)
        {
            _readyState = state;
        }

        private IValue CreateDocumentFragment(IValue[] args, IValue thisVal)
        {
            var frag = new LiteElement("#document-fragment");
            return FenValue.FromObject(new ElementWrapper(frag, _context));
        }

        private IValue CreateTextNode(IValue[] args, IValue thisVal)
        {
            var text = args.Length > 0 ? args[0].ToString() : "";
            var node = new LiteElement("#text") { Text = text };
            return FenValue.FromObject(new ElementWrapper(node, _context));
        }

        private IValue CreateComment(IValue[] args, IValue thisVal)
        {
            var text = args.Length > 0 ? args[0].ToString() : "";
            var node = new LiteElement("#comment") { Text = text };
            return FenValue.FromObject(new ElementWrapper(node, _context));
        }

        private IValue CreateEvent(IValue[] args, IValue thisVal)
        {
            var type = args.Length > 0 ? args[0].ToString() : "";
            return FenValue.FromObject(new DomEvent(type));
        }

        private IValue QuerySelectorAll(IValue[] args, IValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var selector = args[0].ToString();
            var results = new List<LiteElement>();
            RecursiveQuerySelector(_root, selector, results);
            
            var list = new FenObject();
            for(int i=0; i<results.Count; i++) list.Set(i.ToString(), FenValue.FromObject(new ElementWrapper(results[i], _context)));
            list.Set("length", FenValue.FromNumber(results.Count));
            return FenValue.FromObject(list);
        }

        private IValue GetElementsByClassName(IValue[] args, IValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var classNames = args[0].ToString().Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
            var results = new List<LiteElement>();
            RecursiveClassName(_root, classNames, results);
            
            var list = new FenObject();
            for(int i=0; i<results.Count; i++) list.Set(i.ToString(), FenValue.FromObject(new ElementWrapper(results[i], _context)));
            list.Set("length", FenValue.FromNumber(results.Count));
            return FenValue.FromObject(list);
        }

        private IValue GetElementsByTagName(IValue[] args, IValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var tagName = args[0].ToString();
            var results = new List<LiteElement>();
            RecursiveTagName(_root, tagName, results);
            
            var list = new FenObject();
            for(int i=0; i<results.Count; i++) list.Set(i.ToString(), FenValue.FromObject(new ElementWrapper(results[i], _context)));
            list.Set("length", FenValue.FromNumber(results.Count));
            return FenValue.FromObject(list);
        }

        private LiteElement FindFirstSelector(LiteElement el, string selector)
        {
            if (CssLoader.MatchesSelector(el, selector)) return el;
            if (el.Children != null) {
                foreach(var c in el.Children) {
                    var f = FindFirstSelector(c, selector);
                    if (f != null) return f;
                }
            }
            return null;
        }

        private void RecursiveQuerySelector(LiteElement el, string selector, List<LiteElement> results)
        {
            if (CssLoader.MatchesSelector(el, selector)) results.Add(el);
            if (el.Children != null) foreach(var c in el.Children) RecursiveQuerySelector(c, selector, results);
        }

        private void RecursiveClassName(LiteElement el, string[] classes, List<LiteElement> results)
        {
            var elClasses = el.Classes;
            bool match = true;
            foreach (var cls in classes) {
                if (!elClasses.Contains(cls, StringComparer.Ordinal)) { match = false; break; }
            }
            if (match && classes.Length > 0) results.Add(el);
            
            if (el.Children != null) foreach(var c in el.Children) RecursiveClassName(c, classes, results);
        }

        private void RecursiveTagName(LiteElement el, string tagName, List<LiteElement> results)
        {
            if (string.Equals(el.Tag, tagName, StringComparison.OrdinalIgnoreCase) || tagName == "*") results.Add(el);
            if (el.Children != null) foreach(var c in el.Children) RecursiveTagName(c, tagName, results);
        }

        private LiteElement FindElementById(LiteElement element, string id)
        {
            if (element == null) return null;

            // Check if this element has the ID
            if (element.Attr != null && 
                element.Attr.TryGetValue("id", out var eleId) && 
                string.Equals(eleId, id, StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }

            // Search children recursively
            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    var found = FindElementById(child, id);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private LiteElement FindElementByTag(LiteElement element, string tagName)
        {
            if (element == null) return null;

            // Check if this element matches
            if (string.Equals(element.Tag, tagName, StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }

            // Search children recursively
            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    var found = FindElementByTag(child, tagName);
                    if (found != null) return found;
                }
            }

            return null;
        }
    }
}
