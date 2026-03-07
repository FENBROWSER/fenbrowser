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
        private readonly Dictionary<string, FenValue> _expando = new Dictionary<string, FenValue>(StringComparer.Ordinal);
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

                case "createelementns":
                    return FenValue.FromFunction(new FenFunction("createElementNS", CreateElementNS));
                
                case "createdocumentfragment":
                    return FenValue.FromFunction(new FenFunction("createDocumentFragment", CreateDocumentFragment));
                
                case "createtextnode":
                    return FenValue.FromFunction(new FenFunction("createTextNode", CreateTextNode));
                
                case "createcomment":
                    return FenValue.FromFunction(new FenFunction("createComment", CreateComment));

                case "createprocessinginstruction":
                    return FenValue.FromFunction(new FenFunction("createProcessingInstruction", CreateProcessingInstruction));

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

                case "getelementsbytagnamens":
                    return FenValue.FromFunction(new FenFunction("getElementsByTagNameNS", GetElementsByTagNameNS));

                case "body":
                    var body = FindElementByTag(_root, "body");
                    return body != null ? DomWrapperFactory.Wrap(body, _context) : FenValue.Null;

                case "head":
                    var head = FindElementByTag(_root, "head");
                    return head != null ? DomWrapperFactory.Wrap(head, _context) : FenValue.Null;

                case "title":
                    // ... (existing title logic)
                    var titleEl = FindElementByTag(_root, "title");
                    return FenValue.FromString(titleEl?.TextContent ?? "");

                case "documentelement":
                    var htmlEl = FindElementByTag(_root, "html");
                    if (htmlEl != null)
                        return DomWrapperFactory.Wrap(htmlEl, _context);
                    if (_root is Element rootEl)
                        return DomWrapperFactory.Wrap(rootEl, _context);
                    return FenValue.Null;

                case "activeelement":
                    var active = _root.OwnerDocument?.ActiveElement; // Access via OwnerDocument or _root if it tracks it? Document tracks it.
                    // If _root is just an element, we might need access to Document object.
                    // Assuming _root IS the document element, or we act as document. 
                    // Document logic for ActiveElement should be on Document class.
                    // V2 Document has ActiveElement.
                    if (active == null) active = FindElementByTag(_root, "body");
                    return active != null ? DomWrapperFactory.Wrap(active, _context) : FenValue.Null;

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
                        var twRootEl = currentNode as Element ?? FindElementByTag(rootNode, "html");
                        tw.Set("currentNode", twRootEl != null ? DomWrapperFactory.Wrap(twRootEl, _context) : FenValue.Null);
                        tw.Set("root", args.Length > 0 ? args[0] : (twRootEl != null ? DomWrapperFactory.Wrap(twRootEl, _context) : FenValue.Null));
                        tw.Set("whatToShow", FenValue.FromNumber(whatToShow));
                        var allNodes = new List<Node>();
                        CollectNodes(rootNode, allNodes);
                        int idx = 0;
                        tw.Set("nextNode", FenValue.FromFunction(new FenFunction("nextNode", (nArgs, nThis) => {
                            idx++;
                            if (idx < allNodes.Count)
                            {
                                currentNode = allNodes[idx];
                                if (currentNode is Element twEl)
                                    return DomWrapperFactory.Wrap(twEl, _context);
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
                                if (currentNode is Element twEl)
                                    return DomWrapperFactory.Wrap(twEl, _context);
                                return FenValue.Null;
                            }
                            return FenValue.Null;
                        })));
                        tw.Set("firstChild", FenValue.FromFunction(new FenFunction("firstChild", (nArgs, nThis) => {
                            if (currentNode != null && currentNode.ChildNodes.Length > 0)
                            {
                                var child = currentNode.ChildNodes[0];
                                if (child is Element twEl)
                                    return DomWrapperFactory.Wrap(twEl, _context);
                            }
                            return FenValue.Null;
                        })));
                        tw.Set("parentNode", FenValue.FromFunction(new FenFunction("parentNode", (nArgs, nThis) => {
                            if (currentNode?.ParentElement != null)
                                return DomWrapperFactory.Wrap(currentNode.ParentElement, _context);
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
                                childArr.Set(ci.ToString(), DomWrapperFactory.Wrap(childEl, _context));
                                ci++;
                            }
                        }
                    }
                    childArr.Set("length", FenValue.FromNumber(ci));
                    return FenValue.FromObject(childArr);

                default:
                    if (_expando.TryGetValue(key, out var extra))
                    {
                        return extra;
                    }
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
            _expando[key] = value;
        }

        public bool Has(string key, IExecutionContext context = null) => !Get(key, context).IsUndefined;
        public bool Delete(string key, IExecutionContext context = null) => _expando.Remove(key);
        public IEnumerable<string> Keys(IExecutionContext context = null)
            => new[] { "getElementById", "querySelector", "querySelectorAll", "createElement", "createDocumentFragment", "createTextNode", "createComment", "createProcessingInstruction", "createEvent", "getElementsByClassName", "getElementsByTagName", "body", "head", "title", "documentElement", "readyState", "addEventListener", "removeEventListener", "dispatchEvent", "cookie", "domain", "implementation" }
                .Concat(_expando.Keys)
                .Distinct();
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        private FenValue CreateElement(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var tagName = args[0].ToString();
            
            var element = new Element(tagName);
            return DomWrapperFactory.Wrap(element, _context);
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
                return DomWrapperFactory.Wrap(element, _context);
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
                ? DomWrapperFactory.Wrap(element, _context)
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
            EventTarget.DispatchEvent(_root as Element, evt, _context);
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

        private FenValue CreateProcessingInstruction(FenValue[] args, FenValue thisVal)
        {
            // ProcessingInstruction is not a first-class node type in the core; use Comment as backing storage.
            // The JS wrapper participates in event dispatch via NodeWrapper, which is sufficient for WPT tests.
            var data = args.Length > 1 ? args[1].ToString() : "";
            var doc = _root as Document ?? _root.OwnerDocument;
            var node = doc != null ? doc.CreateComment(data) : new Comment(data);
            return DomWrapperFactory.Wrap(node, _context);
        }

        private FenValue CreateRange(FenValue[] args, FenValue thisVal)
        {
            var doc = _root as Document ?? _root.OwnerDocument;
            return FenValue.FromObject(new RangeWrapper(new Range(doc ?? new Document()), _context)); // Guard null doc
        }

        private FenValue CreateEvent(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0)
            {
                throw new FenTypeError("TypeError: Failed to execute 'createEvent': 1 argument required, but only 0 present.");
            }

            var interfaceName = args[0].ToString().Trim();
            switch (interfaceName.ToLowerInvariant())
            {
                case "event":
                case "events":
                case "htmlevents":
                    return FenValue.FromObject(new DomEvent("", false, false, false, _context, initialized: false));

                case "customevent":
                    return FenValue.FromObject(new CustomEvent("", false, false, FenValue.Null, _context, initialized: false));

                case "uievent":
                case "uievents":
                    return FenValue.FromObject(new LegacyUIEvent("", false, false, _context, initialized: false));

                case "mouseevent":
                case "mouseevents":
                    return FenValue.FromObject(new LegacyMouseEvent("", false, false, _context, initialized: false));

                case "keyboardevent":
                case "keyboardevents":
                    return FenValue.FromObject(new LegacyKeyboardEvent("", false, false, _context, initialized: false));

                case "compositionevent":
                case "compositionevents":
                    return FenValue.FromObject(new LegacyCompositionEvent("", false, false, _context, initialized: false));

                default:
                    throw new InvalidOperationException(
                        $"NotSupportedError: Failed to execute 'createEvent': The provided event type ('{interfaceName}') is not supported.");
            }
        }

        private FenValue QuerySelectorAll(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var selector = args[0].ToString();
            var results = new List<Element>();
            RecursiveQuerySelector(_root, selector, results);
            
            var list = new FenObject();
            for (int i = 0; i < results.Count; i++) list.Set(i.ToString(), DomWrapperFactory.Wrap(results[i], _context));
            list.Set("length", FenValue.FromNumber(results.Count));
            return FenValue.FromObject(list);
        }

        private FenValue GetElementsByClassName(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var classNames = args[0].ToString().Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
            return FenValue.FromObject(new HTMLCollectionWrapper(() =>
            {
                var results = new List<Element>();
                RecursiveClassName(_root, classNames, results);
                return results;
            }, _context));
        }

        private FenValue GetElementsByTagName(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var tagName = args[0].ToString();
            return FenValue.FromObject(new HTMLCollectionWrapper(() =>
            {
                var results = new List<Element>();
                RecursiveTagName(_root, tagName, results);
                return results;
            }, _context));
        }

        private FenValue GetElementsByTagNameNS(FenValue[] args, FenValue thisVal)
        {
            var namespaceUri = args.Length > 0 ? args[0].ToString() : "*";
            var localName = args.Length > 1 ? args[1].ToString() : "*";
            return FenValue.FromObject(new HTMLCollectionWrapper(() =>
            {
                var results = new List<Element>();
                RecursiveTagNameNs(_root, namespaceUri, localName, results);
                return results;
            }, _context));
        }

        private FenValue CreateElementNS(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2)
            {
                return FenValue.Null;
            }

            var namespaceUri = args[0].IsNull ? null : args[0].ToString();
            var qualifiedName = args[1].ToString();
            var document = (_root as Document) ?? _root.OwnerDocument;
            if (document == null)
            {
                return FenValue.Null;
            }

            var created = document.CreateElementNS(namespaceUri, qualifiedName);
            return DomWrapperFactory.Wrap(created, _context);
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

        private void RecursiveTagNameNs(Node node, string namespaceUri, string localName, List<Element> results)
        {
            if (node is Element el)
            {
                var namespaceMatch = namespaceUri == "*" ||
                                     string.Equals(el.NamespaceUri ?? string.Empty, namespaceUri ?? string.Empty, StringComparison.Ordinal);
                var localNameMatch = localName == "*" ||
                                     string.Equals(el.LocalName ?? el.NodeName, localName, StringComparison.OrdinalIgnoreCase);
                if (namespaceMatch && localNameMatch)
                {
                    results.Add(el);
                }
            }

            if (node.ChildNodes != null)
            {
                foreach (var c in node.ChildNodes)
                {
                    RecursiveTagNameNs(c, namespaceUri, localName, results);
                }
            }
        }

        private Element FindElementById(Node node, string id)
        {
            if (node  == null) return null;

            // Check if this element has the ID
            if (node is Element el && string.Equals(el.GetAttribute("id"), id, StringComparison.Ordinal))
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
            var callbackIsValid = callback.IsFunction || (callback.IsObject && !callback.IsNull);
            if (string.IsNullOrEmpty(type) || !callbackIsValid || callback.IsUndefined || callback.IsNull)
                return FenValue.Undefined;

            FenLogger.Debug($"[DocumentWrapper] addEventListener called for '{type}'", FenBrowser.Core.Logging.LogCategory.JavaScript);

            if ((type == "DOMContentLoaded" || type == "load") && (_readyState == "complete" || _readyState == "interactive"))
            {
                try
                {
                    var evt = new DomEvent(type);
                    FenFunction callbackFn = null;
                    var callbackThis = FenValue.FromObject(this);
                    if (callback.IsFunction)
                    {
                        callbackFn = callback.AsFunction() as FenFunction;
                    }
                    else if (callback.IsObject)
                    {
                        var handleEvent = callback.AsObject()?.Get("handleEvent", _context) ?? FenValue.Undefined;
                        if (handleEvent.IsFunction)
                        {
                            callbackFn = handleEvent.AsFunction() as FenFunction;
                            callbackThis = callback;
                        }
                    }

                    callbackFn?.Invoke(new[] { FenValue.FromObject(evt) }, _context, callbackThis);
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[DocumentWrapper] Error executing immediate {type}: {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript);
                }
            }

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
                        capture = cVal.IsBoolean && cVal.ToBoolean();
                        var oVal = opts.Get("once");
                        once = oVal.IsBoolean && oVal.ToBoolean();
                        var pVal = opts.Get("passive");
                        passive = pVal.IsBoolean && pVal.ToBoolean();
                    }
                }
            }

            AddToDocumentListenerStore(type, callback, capture, once, passive);
            return FenValue.Undefined;
        }

        private FenValue RemoveEventListenerMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length < 2) return FenValue.Undefined;
            var type = args[0].ToString();
            var callback = args[1];
            bool capture = false;
            if (args.Length >= 3 && args[2].IsBoolean) capture = args[2].ToBoolean();

            RemoveFromDocumentListenerStore(type, callback, capture);
            return FenValue.Undefined;
        }

        private FenValue DispatchEventMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length == 0 || !args[0].IsObject) return FenValue.FromBoolean(false);

            var eventObj = args[0].AsObject() as DomEvent;
            if (eventObj == null)
            {
                var obj = args[0].AsObject() as FenObject;
                if (obj == null) return FenValue.FromBoolean(false);
                var typeVal = obj.Get("type");
                var type = !typeVal.IsUndefined ? typeVal.ToString() : "";
                eventObj = new DomEvent(type);
            }

            FenLogger.Debug($"[DocumentWrapper] dispatchEvent '{eventObj.Type}'", FenBrowser.Core.Logging.LogCategory.JavaScript);

            var notPrevented = false;
            var rootElement = GetDocumentEventTargetElement();
            if (rootElement != null)
            {
                notPrevented = EventTarget.DispatchEvent(rootElement, eventObj, _context);
            }

            return FenValue.FromBoolean(notPrevented);
        }

        private void AddToDocumentListenerStore(string type, FenValue callback, bool capture, bool once, bool passive)
        {
            var listenersVal = Get("__fen_listeners__");
            var listenersObj = listenersVal.IsObject ? listenersVal.AsObject() as FenObject : null;
            if (listenersObj == null)
            {
                listenersObj = new FenObject();
                Set("__fen_listeners__", FenValue.FromObject(listenersObj));
            }

            var arrVal = listenersObj.Get(type);
            var arr = arrVal.IsObject ? arrVal.AsObject() as FenObject : null;
            if (arr == null)
            {
                arr = FenObject.CreateArray();
                listenersObj.Set(type, FenValue.FromObject(arr));
            }

            int len = (int)arr.Get("length").ToNumber();
            for (int i = 0; i < len; i++)
            {
                var existing = arr.Get(i.ToString());
                if (!existing.IsObject) continue;
                var entry = existing.AsObject();
                var existingCallback = entry.Get("callback");
                var existingCapture = entry.Get("capture").ToBoolean();
                if (existingCallback.Equals(callback) && existingCapture == capture)
                {
                    return;
                }
            }

            var newEntry = new FenObject();
            newEntry.Set("callback", callback);
            newEntry.Set("capture", FenValue.FromBoolean(capture));
            newEntry.Set("once", FenValue.FromBoolean(once));
            newEntry.Set("passive", FenValue.FromBoolean(passive));
            arr.Set(len.ToString(), FenValue.FromObject(newEntry));
            arr.Set("length", FenValue.FromNumber(len + 1));
        }

        private void RemoveFromDocumentListenerStore(string type, FenValue callback, bool capture)
        {
            var listenersVal = Get("__fen_listeners__");
            if (!listenersVal.IsObject) return;

            var listenersObj = listenersVal.AsObject() as FenObject;
            var arrVal = listenersObj?.Get(type) ?? FenValue.Undefined;
            var arr = arrVal.IsObject ? arrVal.AsObject() as FenObject : null;
            if (arr == null) return;

            int len = (int)arr.Get("length").ToNumber();
            var kept = FenObject.CreateArray();
            int k = 0;
            for (int i = 0; i < len; i++)
            {
                var item = arr.Get(i.ToString());
                bool remove = false;
                if (item.IsObject)
                {
                    var itemObj = item.AsObject();
                    var itemCallback = itemObj.Get("callback");
                    var itemCapture = itemObj.Get("capture").ToBoolean();
                    remove = itemCallback.Equals(callback) && itemCapture == capture;
                }

                if (!remove)
                {
                    kept.Set(k.ToString(), item);
                    k++;
                }
            }
            kept.Set("length", FenValue.FromNumber(k));
            listenersObj.Set(type, FenValue.FromObject(kept));
        }
        private Element GetDocumentEventTargetElement()
        {
            if (_root is Element e)
            {
                return e;
            }

            var html = FindElementByTag(_root, "html");
            if (html != null)
            {
                return html;
            }

            var body = FindElementByTag(_root, "body");
            if (body != null)
            {
                return body;
            }

            var nodes = new List<Node>();
            CollectNodes(_root, nodes);
            return nodes.OfType<Element>().FirstOrDefault();
        }        // --- Cookie Implementation ---
        public static Func<Uri, string> CookieReadBridge { get; set; }
        public static Action<Uri, string> CookieWriteBridge { get; set; }

        // In-memory cookie store for the fallback path (when no CookieBridge configured)
        private readonly InMemoryCookieStore _cookieStore = new InMemoryCookieStore();

        private string GetCookieString()
        {
            if (_baseUri != null && CookieReadBridge != null)
            {
                try
                {
                    var bridged = CookieReadBridge(_baseUri);
                    if (bridged != null) return bridged;
                }
                catch (Exception ex) { FenBrowser.Core.FenLogger.Warn($"[DocumentWrapper] Cookie read bridge failed: {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript); }
            }

            // fromScript: true â€” HttpOnly cookies must not be exposed to JavaScript
            return _cookieStore.GetCookieString(_baseUri, fromScript: true);
        }

        private void SetCookie(string cookieStr)
        {
            if (string.IsNullOrWhiteSpace(cookieStr)) return;

            if (_baseUri != null && CookieWriteBridge != null)
            {
                try { CookieWriteBridge(_baseUri, cookieStr); return; }
                catch (Exception ex) { FenBrowser.Core.FenLogger.Warn($"[DocumentWrapper] Cookie write bridge failed: {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript); }
            }

            _cookieStore.SetCookie(cookieStr, _baseUri);
            FenLogger.Debug($"[DocumentWrapper] Cookie set: {cookieStr.Split(';')[0]}", FenBrowser.Core.Logging.LogCategory.JavaScript);
        }
        public bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            if (string.IsNullOrWhiteSpace(key) || desc.IsAccessor)
            {
                return false;
            }

            var builtIn = Get(key, _context);
            if (!builtIn.IsUndefined && !_expando.ContainsKey(key))
            {
                return false;
            }

            _expando[key] = desc.Value ?? FenValue.Undefined;
            return true;
        }
    }
}





