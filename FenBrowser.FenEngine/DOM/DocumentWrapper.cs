using FenBrowser.Core.Dom.V2;
using System;
using System.Linq;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Errors;
using Range = FenBrowser.Core.Dom.V2.Range;

using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Represents the document object in JavaScript.
    /// Provides methods to query and manipulate the DOM.
    /// </summary>
    public class DocumentWrapper : IObject
    {
        private readonly Node _root;
        private readonly IExecutionContext _context;
        private readonly Uri _baseUri;
        private IObject _prototype;
        private string _readyState = "loading"; // Spec compliant default
        public object NativeObject { get; set; }

        public DocumentWrapper(Node root, IExecutionContext context, Uri baseUri = null)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _baseUri = baseUri;
        }

        internal Node Node => _root;

        public FenValue Get(string key, IExecutionContext context = null)
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

                case "createrange":
                    return FenValue.FromFunction(new FenFunction("createRange", CreateRange));

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
                    return FenValue.FromString(titleEl?.TextContent ?? "");

                case "documentelement":
                    var htmlEl = FindElementByTag(_root, "html");
                    if (htmlEl != null)
                        return FenValue.FromObject(new ElementWrapper(htmlEl, _context));
                    if (_root is Element rootEl)
                        return FenValue.FromObject(new ElementWrapper(rootEl, _context));
                    return FenValue.Null;

                case "activeelement":
                    var active = _root.OwnerDocument?.ActiveElement; // Access via OwnerDocument or _root if it tracks it? Document tracks it.
                    // If _root is just an element, we might need access to Document object.
                    // Assuming _root IS the document element, or we act as document. 
                    // Document logic for ActiveElement should be on Document class.
                    // V2 Document has ActiveElement.
                    if (active == null) active = FindElementByTag(_root, "body");
                    return active != null ? FenValue.FromObject(new ElementWrapper(active, _context)) : FenValue.Null;

                case "readystate":
                    return FenValue.FromString(_readyState);

                // DOM Level 3 Events
                case "addeventlistener":
                    return FenValue.FromFunction(new FenFunction("addEventListener", AddEventListenerMethod));

                case "removeeventlistener":
                    return FenValue.FromFunction(new FenFunction("removeEventListener", RemoveEventListenerMethod));

                case "dispatchevent":
                    return FenValue.FromFunction(new FenFunction("dispatchEvent", DispatchEventMethod));

                case "cookie":
                    return FenValue.FromString(GetCookieString()); 

                case "domain":
                    return FenValue.FromString(_baseUri?.Host ?? "");

                case "implementation":
                    // DOMImplementation interface
                    var impl = new FenObject();
                    impl.Set("hasFeature", FenValue.FromFunction(new FenFunction("hasFeature", (args, _) => FenValue.FromBoolean(true))));
                    impl.Set("createHTMLDocument", FenValue.FromFunction(new FenFunction("createHTMLDocument", (args, _) => {
                         var title = args.Length > 0 ? args[0].ToString() : "";
                         var newLite = new Element("html"); 
                         // Minimal structure
                         var head = new Element("head");
                         if (!string.IsNullOrEmpty(title)) { var t = new Element("title") { TextContent = title }; head.AppendChild(t); }
                         newLite.AppendChild(head);
                         newLite.AppendChild(new Element("body"));
                         return FenValue.FromObject(new DocumentWrapper(newLite, _context));
                    })));
                    return FenValue.FromObject(impl);

                case "createtreewalker":
                    return FenValue.FromFunction(new FenFunction("createTreeWalker", (args, _) => {
                        // TreeWalker: walk DOM nodes with an optional filter
                        var rootNode = args.Length > 0 && args[0].IsObject
                            ? (Node)(args[0].AsObject() as ElementWrapper)?.Element ?? _root
                            : _root;
                        int whatToShow = args.Length > 1 ? (int)args[1].ToNumber() : 0xFFFFFF; // NodeFilter.SHOW_ALL
                        var tw = new FenObject();
                        Node currentNode = rootNode;
                        tw.Set("currentNode", FenValue.FromObject(new ElementWrapper(currentNode as Element ?? FindElementByTag(rootNode, "html"), _context)));
                        tw.Set("root", args.Length > 0 ? args[0] : FenValue.FromObject(new ElementWrapper(rootNode as Element, _context)));
                        tw.Set("whatToShow", FenValue.FromNumber(whatToShow));
                        var allNodes = new List<Node>();
                        CollectNodes(rootNode, allNodes);
                        int idx = 0;
                        tw.Set("nextNode", FenValue.FromFunction(new FenFunction("nextNode", (nArgs, nThis) => {
                            idx++;
                            if (idx < allNodes.Count)
                            {
                                currentNode = allNodes[idx];
                                if (currentNode is Element el)
                                    return FenValue.FromObject(new ElementWrapper(el, _context));
                                // Text node
                                var textObj = new FenObject();
                                textObj.Set("nodeType", FenValue.FromNumber(3));
                                textObj.Set("textContent", FenValue.FromString(currentNode.TextContent ?? ""));
                                textObj.Set("data", FenValue.FromString(currentNode.TextContent ?? ""));
                                return FenValue.FromObject(textObj);
                            }
                            return FenValue.Null;
                        })));
                        tw.Set("previousNode", FenValue.FromFunction(new FenFunction("previousNode", (nArgs, nThis) => {
                            idx--;
                            if (idx >= 0 && idx < allNodes.Count)
                            {
                                currentNode = allNodes[idx];
                                if (currentNode is Element el)
                                    return FenValue.FromObject(new ElementWrapper(el, _context));
                                return FenValue.Null;
                            }
                            return FenValue.Null;
                        })));
                        tw.Set("firstChild", FenValue.FromFunction(new FenFunction("firstChild", (nArgs, nThis) => {
                            if (currentNode != null && currentNode.ChildNodes.Length > 0)
                            {
                                var child = currentNode.ChildNodes[0];
                                if (child is Element el)
                                    return FenValue.FromObject(new ElementWrapper(el, _context));
                            }
                            return FenValue.Null;
                        })));
                        tw.Set("parentNode", FenValue.FromFunction(new FenFunction("parentNode", (nArgs, nThis) => {
                            if (currentNode?.ParentElement != null)
                                return FenValue.FromObject(new ElementWrapper(currentNode.ParentElement, _context));
                            return FenValue.Null;
                        })));
                        return FenValue.FromObject(tw);
                    }));

                case "creatensresolver":
                    return FenValue.FromFunction(new FenFunction("createNSResolver", (args, _) => FenValue.Null));

                case "hidden":
                    return FenValue.FromBoolean(false); // document is not hidden

                case "visibilitystate":
                    return FenValue.FromString("visible");

                case "characterset":
                case "charset":
                    return FenValue.FromString("UTF-8");

                case "contenttype":
                    return FenValue.FromString("text/html");

                case "compatmode":
                    return FenValue.FromString("CSS1Compat");

                case "location":
                    // Return the window.location equivalent
                    return FenValue.Undefined; // Will be set by runtime

                case "url":
                case "documenturi":
                    return FenValue.FromString(_baseUri?.AbsoluteUri ?? "about:blank");

                case "referrer":
                    return FenValue.FromString("");

                case "designmode":
                    return FenValue.FromString("off");

                case "dir":
                    return FenValue.FromString("ltr");

                case "nodetype":
                    return FenValue.FromNumber(9); // DOCUMENT_NODE

                case "nodevalue":
                    return FenValue.Null;

                case "childnodes":
                case "children":
                    var childArr = new FenObject();
                    int ci = 0;
                    if (_root is Element rootElement)
                    {
                        for (int cj = 0; cj < rootElement.ChildNodes.Length; cj++)
                        {
                            var child = rootElement.ChildNodes[cj];
                            if (child is Element childEl)
                            {
                                childArr.Set(ci.ToString(), FenValue.FromObject(new ElementWrapper(childEl, _context)));
                                ci++;
                            }
                        }
                    }
                    childArr.Set("length", FenValue.FromNumber(ci));
                    return FenValue.FromObject(childArr);

                default:
                    return FenValue.Undefined;
            }
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            if (key.ToLowerInvariant() == "cookie")
            {
                SetCookie(value.ToString());
                return;
            }
            // document properties are mostly read-only
            // Could add support for document.title setter
        }

        public bool Has(string key, IExecutionContext context = null) => !Get(key, context).IsUndefined;
        public bool Delete(string key, IExecutionContext context = null) => false;
        public IEnumerable<string> Keys(IExecutionContext context = null) 
            => new[] { "getElementById", "querySelector", "querySelectorAll", "createElement", "createDocumentFragment", "createTextNode", "createComment", "createEvent", "getElementsByClassName", "getElementsByTagName", "body", "head", "title", "documentElement", "readyState", "addEventListener", "removeEventListener", "dispatchEvent", "cookie", "domain", "implementation" };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        private FenValue CreateElement(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var tagName = args[0].ToString();
            
            var element = new Element(tagName);
            return FenValue.FromObject(new ElementWrapper(element, _context));
        }

        private FenValue GetElementById(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomRead, "getElementById"))
                throw new FenSecurityError("DOM read permission required");

            if (args.Length == 0) return FenValue.Null;

            var id = args[0].ToString();
            /* [PERF-REMOVED] */
            var element = FindElementById(_root, id);

            if (element != null)
            {
                /* [PERF-REMOVED] */
                return FenValue.FromObject(new ElementWrapper(element, _context));
            }
            else
            {
                /* [PERF-REMOVED] */
                return FenValue.Null;
            }
        }

        private FenValue QuerySelector(FenValue[] args, FenValue thisVal)
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
            if (_readyState == state) return;
            _readyState = state;
            
            FenLogger.Debug($"[DocumentWrapper] readyState -> {state}", FenBrowser.Core.Logging.LogCategory.JavaScript);
            
            // Dispatch readystatechange
            DispatchEventInternal(new DomEvent("readystatechange", false, false, false, _context));
        }

        private void DispatchEventInternal(DomEvent evt)
        {
            var listeners = ElementWrapper.EventRegistry.Get(_root, evt.Type, false);
            foreach (var l in listeners)
            {
                try
                {
                    l.Callback.AsFunction().Invoke(new FenValue[] { FenValue.FromObject(evt) }, _context);
                    if (l.Once) ElementWrapper.EventRegistry.Remove(_root, evt.Type, l.Callback, l.Capture);
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[DocumentWrapper] Error dispatching internal event '{evt.Type}': {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript);
                }
            }
        }

        private FenValue CreateDocumentFragment(FenValue[] args, FenValue thisVal)
        {
            var doc = _root as Document ?? _root.OwnerDocument;
            var frag = doc != null ? doc.CreateDocumentFragment() : new DocumentFragment();
            return DomWrapperFactory.Wrap(frag, _context);
        }

        private FenValue CreateTextNode(FenValue[] args, FenValue thisVal)
        {
            var text = args.Length > 0 ? args[0].ToString() : "";
            var doc = _root as Document ?? _root.OwnerDocument;
            var node = doc != null ? doc.CreateTextNode(text) : new Text(text);
            return DomWrapperFactory.Wrap(node, _context);
        }

        private FenValue CreateComment(FenValue[] args, FenValue thisVal)
        {
            var text = args.Length > 0 ? args[0].ToString() : "";
            var doc = _root as Document ?? _root.OwnerDocument;
            var node = doc != null ? doc.CreateComment(text) : new Comment(text);
            return DomWrapperFactory.Wrap(node, _context);
        }

        private FenValue CreateRange(FenValue[] args, FenValue thisVal)
        {
            var doc = _root as Document ?? _root.OwnerDocument;
            return FenValue.FromObject(new RangeWrapper(new Range(doc ?? new Document()), _context)); // Guard null doc
        }

        private FenValue CreateEvent(FenValue[] args, FenValue thisVal)
        {
            var type = args.Length > 0 ? args[0].ToString() : "";
            // Pass context to event so it can wrap nodes in composedPath
            return FenValue.FromObject(new DomEvent(type, false, false, false, _context));
        }

        private FenValue QuerySelectorAll(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var selector = args[0].ToString();
            var results = new List<Element>();
            RecursiveQuerySelector(_root, selector, results);
            
            var list = new FenObject();
            for(int i=0; i<results.Count; i++) list.Set(i.ToString(), FenValue.FromObject(new ElementWrapper(results[i], _context)));
            list.Set("length", FenValue.FromNumber(results.Count));
            return FenValue.FromObject(list);
        }

        private FenValue GetElementsByClassName(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var classNames = args[0].ToString().Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
            var results = new List<Element>();
            RecursiveClassName(_root, classNames, results);
            
            var list = new FenObject();
            for(int i=0; i<results.Count; i++) list.Set(i.ToString(), FenValue.FromObject(new ElementWrapper(results[i], _context)));
            list.Set("length", FenValue.FromNumber(results.Count));
            return FenValue.FromObject(list);
        }

        private FenValue GetElementsByTagName(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var tagName = args[0].ToString();
            var results = new List<Element>();
            RecursiveTagName(_root, tagName, results);
            
            var list = new FenObject();
            for(int i=0; i<results.Count; i++) list.Set(i.ToString(), FenValue.FromObject(new ElementWrapper(results[i], _context)));
            list.Set("length", FenValue.FromNumber(results.Count));
            return FenValue.FromObject(list);
        }

        private Element FindFirstSelector(Node node, string selector)
        {
            if (node is Element el)
            {
                if (CssLoader.MatchesSelector(el, selector)) return el;
            }
            if (node.ChildNodes != null) {
                foreach(var c in node.ChildNodes.OfType<Element>()) {
                    var f = FindFirstSelector(c, selector);
                    if (f != null) return f;
                }
            }
            return null;
        }

        private void RecursiveQuerySelector(Node node, string selector, List<Element> results)
        {
            if (node is Element el && CssLoader.MatchesSelector(el, selector)) results.Add(el);
            if (node.ChildNodes != null) foreach(var c in node.ChildNodes) RecursiveQuerySelector(c, selector, results); // Recurse on all nodes
        }

        private void RecursiveClassName(Node node, string[] classes, List<Element> results)
        {
            if (node is Element el)
            {
                var elClass = el.GetAttribute("class") ?? "";
                var elClasses = elClass.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                bool match = true;
                foreach (var cls in classes) {
                    if (!elClasses.Contains(cls, StringComparer.Ordinal)) { match = false; break; }
                }
                if (match && classes.Length > 0) results.Add(el);
            }
            if (node.ChildNodes != null) foreach(var c in node.ChildNodes) RecursiveClassName(c, classes, results);
        }

        private void RecursiveTagName(Node node, string tagName, List<Element> results)
        {
            if (node is Element el)
            {
                if (string.Equals(el.NodeName, tagName, StringComparison.OrdinalIgnoreCase) || tagName == "*") results.Add(el);
            }
            if (node.ChildNodes != null) foreach(var c in node.ChildNodes) RecursiveTagName(c, tagName, results);
        }

        private Element FindElementById(Node node, string id)
        {
            if (node  == null) return null;

            // Check if this element has the ID
            if (node is Element el && string.Equals(el.GetAttribute("id"), id, StringComparison.OrdinalIgnoreCase))
            {
                return el;
            }

            // Search children recursively
            if (node.ChildNodes != null)
            {
                foreach (var child in node.ChildNodes)
                {
                    var found = FindElementById(child, id);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private static void CollectNodes(Node node, List<Node> result)
        {
            if (node == null) return;
            result.Add(node);
            if (node.ChildNodes != null)
            {
                foreach (var child in node.ChildNodes)
                    CollectNodes(child, result);
            }
        }

        private Element FindElementByTag(Node node, string tagName)
        {
            if (node == null) return null;

            // If node is an element, check if it matches
            if (node is Element element)
            {
                if (string.Equals(element.NodeName, tagName, StringComparison.OrdinalIgnoreCase))
                {
                    return element;
                }
            }

            // Search children recursively
            if (node.ChildNodes != null)
            {
                foreach (var child in node.ChildNodes.OfType<Element>())
                {
                    var found = FindElementByTag(child, tagName);
                    if (found != null) return found;
                }
            }

            return null;
        }

        // --- Event Listener Implementation ---

        private FenValue AddEventListenerMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length < 2) return FenValue.Undefined;

            var type = args[0].ToString();
            var callback = args[1];

            if (string.IsNullOrEmpty(type) || callback  == null || !callback.IsFunction)
                return FenValue.Undefined;
            
            FenLogger.Debug($"[DocumentWrapper] addEventListener called for '{type}'", FenBrowser.Core.Logging.LogCategory.JavaScript);

            // Handle immediate execution for load events if ready
            if ((type == "DOMContentLoaded" || type == "load") && (_readyState == "complete" || _readyState == "interactive"))
            {
                FenLogger.Debug($"[DocumentWrapper] Immediate execution of {type}", FenBrowser.Core.Logging.LogCategory.JavaScript);
                try {
                    var evt = new DomEvent(type);
                    callback.AsFunction().Invoke(new FenValue[] { FenValue.FromObject(evt) }, _context);
                } catch (Exception ex) {
                    FenLogger.Error($"[DocumentWrapper] Error executing immediate {type}: {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript);
                }
            }

            // Use ElementWrapper's registry to store listeners on the root element
            // This is a simplification; ideally Document has its own registry or we use a virtual element.
            // Using _root means document listeners are effective on the <html> element.
            bool capture = false;
            bool once = false;
            bool passive = false;

            if (args.Length >= 3)
            {
                if (args[2].IsBoolean) capture = args[2].ToBoolean();
                else if (args[2].IsObject)
                {
                    var opts = args[2].AsObject() as FenObject;
                    if (opts != null)
                    {
                        var cVal = opts.Get("capture");
                        capture = cVal.IsBoolean ? cVal.ToBoolean() : false;
                        
                        var oVal = opts.Get("once");
                        once = oVal.IsBoolean ? oVal.ToBoolean() : false;
                        
                        var pVal = opts.Get("passive");
                        passive = pVal.IsBoolean ? pVal.ToBoolean() : false;
                    }
                }
            }

            ElementWrapper.EventRegistry.Add(_root, type, callback, capture, once, passive);
            return FenValue.Undefined;
        }

        private FenValue RemoveEventListenerMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length < 2) return FenValue.Undefined;
            var type = args[0].ToString();
            var callback = args[1];
            bool capture = false;
            if (args.Length >= 3 && args[2].IsBoolean) capture = args[2].ToBoolean();
            
            ElementWrapper.EventRegistry.Remove(_root, type, callback, capture);
            return FenValue.Undefined;
        }

        private FenValue DispatchEventMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length == 0 || !args[0].IsObject) return FenValue.FromBoolean(false);
            
            var eventObj = args[0].AsObject() as DomEvent;
            // Create proper event object if needed
            if (eventObj  == null)
            {
                var obj = args[0].AsObject() as FenObject;
                if (obj  == null) return FenValue.FromBoolean(false);
                var typeVal = obj.Get("type");
                var type = !typeVal.IsUndefined ? typeVal.ToString() : "";
                eventObj = new DomEvent(type);
            }

            FenLogger.Debug($"[DocumentWrapper] dispatchEvent '{eventObj.Type}'", FenBrowser.Core.Logging.LogCategory.JavaScript);

            // Dispatch on _root using ElementWrapper's logic (if we could access DispatchEventInternal there)
            // Since we can't easily access private method, we might need to duplicate dispatch logic 
            // OR just support basic listeners for now.
            // For detection, just returning true often works.
            // But let's try to execute listeners if possible.
            
            var listeners = ElementWrapper.EventRegistry.Get(_root, eventObj.Type, false);
            foreach(var l in listeners) 
            {
                 try { l.Callback.AsFunction().Invoke(new FenValue[] { FenValue.FromObject(eventObj) }, _context); } catch {}
            }
            
            return FenValue.FromBoolean(true);
        }

        // --- Cookie Implementation ---
        private readonly Dictionary<string, string> _cookies = new Dictionary<string, string>();

        private string GetCookieString()
        {
            // Format: key=value; key2=value2
            return string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        private void SetCookie(string cookieStr)
        {
            if (string.IsNullOrWhiteSpace(cookieStr)) return;

            // Basic parsing: split by ';' to ignore attributes for now (path, domain, expires)
            // Real implementation would parse attributes and checking matching logic
            var parts = cookieStr.Split(';');
            if (parts.Length > 0)
            {
                var kv = parts[0].Trim();
                var eq = kv.IndexOf('=');
                if (eq > 0)
                {
                    var key = kv.Substring(0, eq).Trim();
                    var val = kv.Substring(eq + 1).Trim();
                    _cookies[key] = val;
                }
                else
                {
                    // "key" without value?
                    _cookies[kv] = "";
                }
                
                FenLogger.Debug($"[DocumentWrapper] Cookie set: {parts[0]}", FenBrowser.Core.Logging.LogCategory.JavaScript);
            }
        }
        public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;
    }
}

