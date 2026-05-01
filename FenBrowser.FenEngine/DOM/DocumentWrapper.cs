// SpecRef: CSSOM, Document.styleSheets and StyleSheetList exposure
// CapabilityId: CSSOM-STYLESHEET-LIST-01
// Determinism: strict
// FallbackPolicy: spec-defined
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
using System.Runtime.CompilerServices;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Represents the document object in JavaScript.
    /// Provides methods to query and manipulate the DOM.
    /// </summary>
    public class DocumentWrapper : IObject
    {
        private static readonly ConditionalWeakTable<Document, FenObject> s_defaultViewByDocument = new ConditionalWeakTable<Document, FenObject>();
        private static readonly ConditionalWeakTable<Document, Element> s_browsingContextHostByDocument = new ConditionalWeakTable<Document, Element>();
        private static readonly ConditionalWeakTable<Element, FenObject> s_linkedStyleSheetByElement = new ConditionalWeakTable<Element, FenObject>();
        private readonly Node _root;
        private readonly IExecutionContext _context;
        private readonly Uri _baseUri;
        private IObject _prototype;
        private IObject _styleSheetList;
        private string _readyState = "loading"; // Spec compliant default
        private readonly Dictionary<string, FenValue> _expando = new Dictionary<string, FenValue>(StringComparer.Ordinal);
        private FenObject _fonts;
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

                case "nodetype":
                    return FenValue.FromNumber((int)_root.NodeType);

                case "nodename":
                    return FenValue.FromString(_root.NodeName);

                case "nodevalue":
                    return FenValue.FromString(_root.NodeValue);

                case "childnodes":
                    return FenValue.FromObject(new NodeListWrapper(_root.ChildNodes, _context));

                case "firstchild":
                    return DomWrapperFactory.Wrap(_root.FirstChild, _context);

                case "lastchild":
                    return DomWrapperFactory.Wrap(_root.LastChild, _context);

                case "previoussibling":
                    return DomWrapperFactory.Wrap(_root.PreviousSibling, _context);

                case "nextsibling":
                    return DomWrapperFactory.Wrap(_root.NextSibling, _context);

                case "ownerdocument":
                    return DomWrapperFactory.Wrap(_root.OwnerDocument, _context);

                case "textcontent":
                    return FenValue.FromString(_root.TextContent);

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

                case "createattribute":
                    return FenValue.FromFunction(new FenFunction("createAttribute", CreateAttribute));

                case "createattributens":
                    return FenValue.FromFunction(new FenFunction("createAttributeNS", CreateAttributeNS));

                case "createprocessinginstruction":
                    return FenValue.FromFunction(new FenFunction("createProcessingInstruction", CreateProcessingInstruction));

                case "createevent":
                    return FenValue.FromFunction(new FenFunction("createEvent", CreateEvent));

                case "write":
                    return FenValue.FromFunction(new FenFunction("write", Write));

                case "writeln":
                    return FenValue.FromFunction(new FenFunction("writeln", WriteLine));

                case "createrange":
                    return FenValue.FromFunction(new FenFunction("createRange", CreateRange));

                case "appendchild":
                    return FenValue.FromFunction(new FenFunction("appendChild", AppendChild));

                case "append":
                    return FenValue.FromFunction(new FenFunction("append", Append));

                case "prepend":
                    return FenValue.FromFunction(new FenFunction("prepend", Prepend));

                case "importnode":
                    return FenValue.FromFunction(new FenFunction("importNode", ImportNode));

                case "removechild":
                    return FenValue.FromFunction(new FenFunction("removeChild", RemoveChild));

                case "replacechild":
                    return FenValue.FromFunction(new FenFunction("replaceChild", ReplaceChild));

                case "replacechildren":
                    return FenValue.FromFunction(new FenFunction("replaceChildren", ReplaceChildren));

                case "insertbefore":
                    return FenValue.FromFunction(new FenFunction("insertBefore", InsertBefore));

                case "clonenode":
                    return FenValue.FromFunction(new FenFunction("cloneNode", CloneNode));

                case "haschildnodes":
                    return FenValue.FromFunction(new FenFunction("hasChildNodes", (args, thisVal) => FenValue.FromBoolean(_root.HasChildNodes)));

                case "getrootnode":
                    return FenValue.FromFunction(new FenFunction("getRootNode", GetRootNode));

                case "evaluate":
                    return FenValue.FromFunction(new FenFunction("evaluate", Evaluate));

                case "queryselectorall":
                    return FenValue.FromFunction(new FenFunction("querySelectorAll", QuerySelectorAll));

                case "getelementsbyclassname":
                    return FenValue.FromFunction(new FenFunction("getElementsByClassName", GetElementsByClassName));

                case "getelementsbytagname":
                    return FenValue.FromFunction(new FenFunction("getElementsByTagName", GetElementsByTagName));

                case "getelementsbytagnamens":
                    return FenValue.FromFunction(new FenFunction("getElementsByTagNameNS", GetElementsByTagNameNS));

                case "getelementsbyname":
                    return FenValue.FromFunction(new FenFunction("getElementsByName", GetElementsByName));

                case "body":
                    var body = (_root as Document)?.Body ?? FindElementByTag(_root, "body");
                    return body != null ? DomWrapperFactory.Wrap(body, _context) : FenValue.Null;

                case "head":
                    var head = (_root as Document)?.Head ?? FindElementByTag(_root, "head");
                    return head != null ? DomWrapperFactory.Wrap(head, _context) : FenValue.Null;

                case "links":
                    return FenValue.FromObject(new HTMLCollectionWrapper(() => GetDocumentLinks(), _context));

                case "stylesheets":
                    return FenValue.FromObject(BuildStyleSheetListObject());

                case "all":
                    return FenValue.FromObject(new HTMLCollectionWrapper(() => EnumerateDocumentElements(), _context));

                case "images":
                    return FenValue.FromObject(new HTMLCollectionWrapper(() => GetElementsByTagNames("img"), _context));

                case "forms":
                    return FenValue.FromObject(new HTMLCollectionWrapper(() => GetElementsByTagNames("form"), _context));

                case "scripts":
                    return FenValue.FromObject(new HTMLCollectionWrapper(() => GetElementsByTagNames("script"), _context));

                case "embeds":
                    return FenValue.FromObject(new HTMLCollectionWrapper(() => GetElementsByTagNames("embed"), _context));

                case "plugins":
                    // HTML defines plugins as a legacy alias of embeds.
                    return FenValue.FromObject(new HTMLCollectionWrapper(() => GetElementsByTagNames("embed"), _context));

                case "applets":
                    return FenValue.FromObject(new HTMLCollectionWrapper(() => GetElementsByTagNames("applet"), _context));

                case "anchors":
                    return FenValue.FromObject(new HTMLCollectionWrapper(() => GetDocumentAnchors(), _context));

                case "title":
                    var documentTitle = (_root as Document)?.Title;
                    if (documentTitle != null)
                    {
                        return FenValue.FromString(documentTitle);
                    }

                    var titleEl = FindElementByTag(_root, "title");
                    return FenValue.FromString(titleEl?.TextContent ?? "");

                case "documentelement":
                    var htmlEl = (_root as Document)?.DocumentElement ?? FindElementByTag(_root, "html");
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

                case "currentscript":
                    if (_expando.TryGetValue("currentScript", out var currentScript))
                    {
                        return currentScript;
                    }
                    return FenValue.Null;

                case "element_node":
                    return FenValue.FromNumber(1);

                case "attribute_node":
                    return FenValue.FromNumber(2);

                case "text_node":
                    return FenValue.FromNumber(3);

                case "comment_node":
                    return FenValue.FromNumber(8);

                case "document_node":
                    return FenValue.FromNumber(9);

                case "document_type_node":
                    return FenValue.FromNumber(10);

                case "document_fragment_node":
                    return FenValue.FromNumber(11);

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

                case "fonts":
                    return _fonts != null ? FenValue.FromObject(_fonts) : FenValue.Undefined;

                case "implementation":
                    // DOMImplementation interface
                    var impl = new FenObject();
                    impl.Set("hasFeature", FenValue.FromFunction(new FenFunction("hasFeature", (args, _) => FenValue.FromBoolean(true))));
                    impl.Set("createDocument", FenValue.FromFunction(new FenFunction("createDocument", (args, _) =>
                    {
                        var namespaceUri = args.Length > 0 && !args[0].IsNull && !args[0].IsUndefined ? args[0].ToString() : null;
                        var qualifiedName = args.Length > 1 && !args[1].IsNull && !args[1].IsUndefined ? args[1].ToString() : null;
                        var created = Document.CreateXmlDocument(namespaceUri, qualifiedName);
                        return WrapDocument(created);
                    })));
                    impl.Set("createDocumentType", FenValue.FromFunction(new FenFunction("createDocumentType", (args, _) =>
                    {
                        var name = args.Length > 0 ? args[0].ToString() : string.Empty;
                        var publicId = args.Length > 1 ? args[1].ToString() : string.Empty;
                        var systemId = args.Length > 2 ? args[2].ToString() : string.Empty;
                        if (string.IsNullOrEmpty(name) || name.Contains(":", StringComparison.Ordinal))
                        {
                            throw new DomException("NamespaceError", "Document type name must not contain a namespace prefix");
                        }
                        var owner = _root as Document ?? _root.OwnerDocument ?? Document.CreateHtmlDocument();
                        return DomWrapperFactory.Wrap(owner.CreateDocumentType(name, publicId, systemId), _context);
                    })));
                    impl.Set("createHTMLDocument", FenValue.FromFunction(new FenFunction("createHTMLDocument", (args, _) => {
                         var title = args.Length > 0 ? args[0].ToString() : "";
                         return WrapDocument(Document.CreateHtmlDocument(title));
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

                case "defaultview":
                    if (_root is Document document && s_defaultViewByDocument.TryGetValue(document, out var defaultView))
                    {
                        return FenValue.FromObject(defaultView);
                    }

                    var window = _context?.Environment?.Get("window") ?? FenValue.Undefined;
                    return window.IsObject ? window : FenValue.Null;

                case "url":
                    return FenValue.FromString(GetDocumentUrl());

                case "documenturi":
                    return FenValue.FromString(GetDocumentUrl());

                case "baseuri":
                    return FenValue.FromString(GetDocumentBaseUri());

                case "referrer":
                    return FenValue.FromString("");

                case "designmode":
                    return FenValue.FromString("off");

                case "dir":
                    return FenValue.FromString("ltr");

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
            => new[] { "getElementById", "querySelector", "querySelectorAll", "createElement", "createAttribute", "createAttributeNS", "createDocumentFragment", "createTextNode", "createComment", "createProcessingInstruction", "createEvent", "write", "writeln", "evaluate", "getElementsByClassName", "getElementsByTagName", "getElementsByName", "body", "head", "title", "documentElement", "readyState", "currentScript", "styleSheets", "links", "images", "forms", "scripts", "embeds", "plugins", "applets", "anchors", "all", "addEventListener", "removeEventListener", "dispatchEvent", "cookie", "domain", "fonts", "implementation", "importNode", "prepend", "replaceChildren" }
                .Concat(_expando.Keys)
                .Distinct();
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        private FenValue CreateElement(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var tagName = args[0].ToString();

            var document = _root as Document ?? _root.OwnerDocument;
            var element = document != null ? document.CreateElement(tagName) : new Element(tagName);
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
            
            EngineLogCompat.Debug($"[DocumentWrapper] readyState -> {state}", FenBrowser.Core.Logging.LogCategory.JavaScript);
            
            // Dispatch readystatechange
            DispatchEventInternal(new DomEvent("readystatechange", false, false, false, _context));
        }

        internal static void BindDefaultView(Document document, FenObject window)
        {
            if (document == null || window == null)
            {
                return;
            }

            s_defaultViewByDocument.Remove(document);
            s_defaultViewByDocument.Add(document, window);
        }

        internal static void BindBrowsingContextHost(Document document, Element hostElement)
        {
            if (document == null || hostElement == null)
            {
                return;
            }

            s_browsingContextHostByDocument.Remove(document);
            s_browsingContextHostByDocument.Add(document, hostElement);
        }

        internal static bool TryGetBrowsingContextHost(Document document, out Element hostElement)
        {
            hostElement = null;
            return document != null && s_browsingContextHostByDocument.TryGetValue(document, out hostElement);
        }

        private FenValue Write(FenValue[] args, FenValue thisVal)
        {
            return WriteCore(args, appendNewLine: false);
        }

        private FenValue WriteLine(FenValue[] args, FenValue thisVal)
        {
            return WriteCore(args, appendNewLine: true);
        }

        private FenValue WriteCore(FenValue[] args, bool appendNewLine)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "document.write"))
                throw new FenSecurityError("DOM mutation permission required");

            var html = string.Concat(args.Select(arg => arg.ToString()));
            if (appendNewLine)
            {
                html += Environment.NewLine;
            }

            if (string.IsNullOrEmpty(html))
            {
                return FenValue.Undefined;
            }

            var document = _root as Document ?? _root.OwnerDocument;
            if (document == null)
            {
                return FenValue.Undefined;
            }

            var fragment = ParseWriteFragment(document, html);
            if (fragment.ChildNodes == null || fragment.ChildNodes.Length == 0)
            {
                return FenValue.Undefined;
            }

            var insertionParent = ResolveWriteInsertionParent(document, out var referenceNode);
            if (insertionParent == null)
            {
                return FenValue.Undefined;
            }

            var previousSibling = referenceNode?.PreviousSibling;
            var insertedNodes = new List<Node>();
            foreach (var child in fragment.ChildNodes.ToArray())
            {
                insertedNodes.Add(child);
                if (referenceNode != null)
                {
                    insertionParent.InsertBefore(child, referenceNode);
                }
                else
                {
                    insertionParent.AppendChild(child);
                }
            }

            insertionParent.MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);
            _context.RequestRender?.Invoke();
            _context.OnMutation?.Invoke(new MutationRecord
            {
                Type = MutationRecordType.ChildList,
                Target = insertionParent,
                AddedNodes = insertedNodes,
                PreviousSibling = previousSibling,
                NextSibling = referenceNode
            });

            return FenValue.Undefined;
        }

        private ContainerNode ResolveWriteInsertionParent(Document document, out Node referenceNode)
        {
            referenceNode = null;

            var currentScriptValue = Get("currentScript", _context);
            var currentScript = currentScriptValue.IsObject ? currentScriptValue.AsObject() as ElementWrapper : null;
            var scriptElement = currentScript?.Element;
            if (scriptElement?.ParentNode is ContainerNode scriptParent)
            {
                referenceNode = scriptElement.NextSibling;
                return scriptParent;
            }

            if (document.Body is ContainerNode body)
            {
                return body;
            }

            if (document.DocumentElement is ContainerNode documentElement)
            {
                return documentElement;
            }

            return document;
        }

        private DocumentFragment ParseWriteFragment(Document ownerDocument, string html)
        {
            var contextElement = ownerDocument.Body ?? ownerDocument.DocumentElement;
            return FenBrowser.Core.Parsing.HtmlParser.ParseFragment(
                contextElement,
                html,
                new FenBrowser.Core.Parsing.HtmlParserOptions { BaseUri = _baseUri },
                out _);
        }

        internal void AttachFonts(FenObject fonts)
        {
            _fonts = fonts;
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

        private FenValue CreateAttribute(FenValue[] args, FenValue thisVal)
        {
            var name = args.Length > 0 ? args[0].ToString() : string.Empty;
            return WrapAttribute(new Attr(name, string.Empty));
        }

        private FenValue CreateAttributeNS(FenValue[] args, FenValue thisVal)
        {
            var namespaceUri = args.Length > 0 && !args[0].IsNull && !args[0].IsUndefined ? args[0].ToString() : null;
            var qualifiedName = args.Length > 1 ? args[1].ToString() : string.Empty;
            return WrapAttribute(new Attr(namespaceUri, qualifiedName, string.Empty));
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

        private FenValue ImportNode(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsObject || args[0].IsNull)
            {
                 throw new FenTypeError("TypeError: Failed to execute 'importNode': parameter 1 is not of type 'Node'.");
            }

            var nodeToImport = UnwrapNode(args[0].AsObject());
            if (nodeToImport == null)
            {
                 throw new FenTypeError("TypeError: Failed to execute 'importNode': parameter 1 is not of type 'Node'.");
            }
            if (nodeToImport is Document)
            {
                 throw new DomException("NotSupportedError", "Document nodes cannot be imported.");
            }

            var deep = args.Length > 1 && args[1].ToBoolean();
            var clone = nodeToImport.CloneNode(deep);
            return DomWrapperFactory.Wrap(clone, _context);
        }

        private Node[] ParseNodeArgs(FenValue[] args)
        {
            var nodes = new List<Node>();
            foreach (var arg in args)
            {
                if (arg.IsObject && !arg.IsNull && !arg.IsUndefined)
                {
                    var node = UnwrapNode(arg.AsObject());
                    if (node != null) nodes.Add(node);
                }
                else
                {
                    var doc = _root as Document ?? _root.OwnerDocument ?? Document.CreateHtmlDocument();
                    nodes.Add(doc.CreateTextNode(arg.ToString()));
                }
            }
            return nodes.ToArray();
        }

        private FenValue Prepend(FenValue[] args, FenValue thisVal)
        {
            if (_root is IParentNode parentNode)
            {
                parentNode.Prepend(ParseNodeArgs(args));
            }
            return FenValue.Undefined;
        }

        private FenValue ReplaceChildren(FenValue[] args, FenValue thisVal)
        {
            if (_root is IParentNode parentNode)
            {
                parentNode.ReplaceChildren(ParseNodeArgs(args));
            }
            return FenValue.Undefined;
        }

        private FenValue AppendChild(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsObject || args[0].IsNull)
            {
                return FenValue.Null;
            }

            var child = UnwrapNode(args[0].AsObject());
            if (child == null)
            {
                return FenValue.Null;
            }

            if (_root is ContainerNode container)
            {
                container.AppendChild(child);
                return args[0];
            }

            throw new DomException("HierarchyRequestError", "This node type cannot have children.");
        }

        private FenValue Append(FenValue[] args, FenValue thisVal)
        {
            if (_root is not ContainerNode container)
            {
                return FenValue.Undefined;
            }

            foreach (var arg in args)
            {
                Node child;
                if (arg.IsObject)
                {
                    child = UnwrapNode(arg.AsObject());
                }
                else
                {
                    var doc = _root as Document ?? _root.OwnerDocument ?? Document.CreateHtmlDocument();
                    child = doc.CreateTextNode(arg.ToString());
                }

                if (child != null)
                {
                    container.AppendChild(child);
                }
            }

            return FenValue.Undefined;
        }

        private FenValue RemoveChild(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsObject || args[0].IsNull)
            {
                return FenValue.Null;
            }

            var child = UnwrapNode(args[0].AsObject());
            if (_root is ContainerNode container)
            {
                container.RemoveChild(child);
                return args[0];
            }

            throw new DomException("HierarchyRequestError", "This node type cannot have children.");
        }

        private FenValue ReplaceChild(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2 || !args[0].IsObject || !args[1].IsObject)
            {
                return FenValue.Null;
            }

            var newNode = UnwrapNode(args[0].AsObject());
            var oldNode = UnwrapNode(args[1].AsObject());
            if (_root is ContainerNode container)
            {
                container.ReplaceChild(newNode, oldNode);
                return args[1];
            }

            throw new DomException("HierarchyRequestError", "This node type cannot have children.");
        }

        private FenValue InsertBefore(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsObject || args[0].IsNull)
            {
                return FenValue.Null;
            }

            var newNode = UnwrapNode(args[0].AsObject());
            Node referenceNode = null;
            if (args.Length > 1 && args[1].IsObject && !args[1].IsNull)
            {
                referenceNode = UnwrapNode(args[1].AsObject());
            }

            if (_root is ContainerNode container)
            {
                container.InsertBefore(newNode, referenceNode);
                return args[0];
            }

            throw new DomException("HierarchyRequestError", "This node type cannot have children.");
        }

        private FenValue CloneNode(FenValue[] args, FenValue thisVal)
        {
            var deep = args.Length > 0 && args[0].ToBoolean();
            return DomWrapperFactory.Wrap(_root.CloneNode(deep), _context);
        }

        private FenValue GetRootNode(FenValue[] args, FenValue thisVal)
        {
            var options = default(GetRootNodeOptions);
            if (args.Length > 0 && args[0].IsObject && !args[0].IsNull)
            {
                var optionsObject = args[0].AsObject();
                var composed = optionsObject.Get("composed", _context);
                options.Composed = composed.IsBoolean && composed.ToBoolean();
            }

            return DomWrapperFactory.Wrap(_root.GetRootNode(options), _context);
        }

        private FenValue CreateEvent(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0)
            {
                throw new FenTypeError("TypeError: Failed to execute 'createEvent': 1 argument required, but only 0 present.");
            }

            var requestedName = args[0].ToString().Trim();
            var interfaceName = NormalizeCreateEventInterface(requestedName);
            DomEvent evt = interfaceName switch
            {
                "Event" => new DomEvent("", false, false, false, _context, initialized: false),
                "CustomEvent" => new CustomEvent("", false, false, FenValue.Null, _context, initialized: false),
                "UIEvent" => new LegacyUIEvent("", false, false, _context, initialized: false),
                "MouseEvent" => new LegacyMouseEvent("", false, false, _context, initialized: false),
                "KeyboardEvent" => new LegacyKeyboardEvent("", false, false, _context, initialized: false),
                "CompositionEvent" => new LegacyCompositionEvent("", false, false, _context, initialized: false),
                "FocusEvent" => new LegacyUIEvent("", false, false, _context, initialized: false),
                "DragEvent" => new LegacyMouseEvent("", false, false, _context, initialized: false),
                "TextEvent" => new LegacyUIEvent("", false, false, _context, initialized: false),
                "BeforeUnloadEvent" => new DomEvent("", false, false, false, _context, initialized: false),
                "DeviceMotionEvent" => new DomEvent("", false, false, false, _context, initialized: false),
                "DeviceOrientationEvent" => new DomEvent("", false, false, false, _context, initialized: false),
                "HashChangeEvent" => new DomEvent("", false, false, false, _context, initialized: false),
                "MessageEvent" => new DomEvent("", false, false, false, _context, initialized: false),
                "StorageEvent" => new DomEvent("", false, false, false, _context, initialized: false),
                _ => null
            };

            if (evt == null)
            {
                throw new InvalidOperationException(
                    $"NotSupportedError: Failed to execute 'createEvent': The provided event type ('{requestedName}') is not supported.");
            }

            return ApplyEventPrototype(evt, interfaceName);
        }

        private FenValue Evaluate(FenValue[] args, FenValue thisVal)
        {
            var expression = args.Length > 0 ? args[0].ToString() : string.Empty;
            var contextNode = args.Length > 1 && args[1].IsObject && !args[1].IsNull
                ? UnwrapNode(args[1].AsObject()) ?? _root
                : _root;
            var resultType = args.Length > 3 ? (int)args[3].ToNumber() : 0;

            var result = new FenObject();
            result.Set("resultType", FenValue.FromNumber(resultType));

            FenValue singleNodeValue = FenValue.Null;
            if (expression.StartsWith("//", StringComparison.Ordinal))
            {
                var localName = expression.Substring(2);
                if (!string.IsNullOrWhiteSpace(localName))
                {
                    var found = FindElementByTag(contextNode, localName);
                    singleNodeValue = found != null ? DomWrapperFactory.Wrap(found, _context) : FenValue.Null;
                }
            }

            result.Set("singleNodeValue", singleNodeValue);
            return FenValue.FromObject(result);
        }

        private FenValue QuerySelectorAll(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0)
            {
                return FenValue.FromObject(new NodeListWrapper(Array.Empty<Node>(), _context));
            }

            var selector = args[0].ToString();
            var results = new List<Element>();
            RecursiveQuerySelector(_root, selector, results);

            return FenValue.FromObject(new NodeListWrapper(results.Cast<Node>(), _context));
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

        private FenValue GetElementsByName(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0)
            {
                return FenValue.FromObject(new NodeListWrapper(Array.Empty<Node>(), _context));
            }

            var requestedName = args[0].ToString() ?? string.Empty;
            var matchedNodes = EnumerateDocumentElements()
                .Where(element => string.Equals(element.GetAttribute("name"), requestedName, StringComparison.Ordinal))
                .Cast<Node>()
                .ToList();

            return FenValue.FromObject(new NodeListWrapper(matchedNodes, _context));
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
                if (MatchesSelectorForDomQueries(el, selector)) return el;
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
            if (node is Element el && MatchesSelectorForDomQueries(el, selector)) results.Add(el);
            if (node.ChildNodes != null) foreach(var c in node.ChildNodes) RecursiveQuerySelector(c, selector, results); // Recurse on all nodes
        }

        internal static bool MatchesSelectorForDomQueries(Element element, string selector)
        {
            if (element == null || string.IsNullOrWhiteSpace(selector))
            {
                return false;
            }

            if (CssLoader.MatchesSelector(element, selector))
            {
                return true;
            }

            var selectorParts = selector
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToArray();

            if (selectorParts.Length < 2)
            {
                return false;
            }

            if (!CssLoader.MatchesSelector(element, selectorParts[selectorParts.Length - 1]))
            {
                return false;
            }

            var currentAncestor = element.ParentElement;
            for (int i = selectorParts.Length - 2; i >= 0; i--)
            {
                while (currentAncestor != null && !CssLoader.MatchesSelector(currentAncestor, selectorParts[i]))
                {
                    currentAncestor = currentAncestor.ParentElement;
                }

                if (currentAncestor == null)
                {
                    return false;
                }

                currentAncestor = currentAncestor.ParentElement;
            }

            return true;
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

        private IEnumerable<Element> GetDocumentLinks()
        {
            return EnumerateDocumentElements().Where(IsHyperlinkElement);
        }

        private IObject BuildStyleSheetListObject()
        {
            _styleSheetList ??= new StyleSheetListObject(CollectStyleSheetValues);
            return _styleSheetList;
        }

        private sealed class StyleSheetListObject : IObject
        {
            private readonly Func<List<FenValue>> _sourceProvider;
            private readonly Dictionary<string, FenValue> _expandos = new(StringComparer.Ordinal);
            private IObject _prototype;

            public StyleSheetListObject(Func<List<FenValue>> sourceProvider)
            {
                _sourceProvider = sourceProvider ?? (() => new List<FenValue>());
            }

            public object NativeObject { get; set; }

            public FenValue Get(string key, IExecutionContext context = null)
            {
                if (TryParseArrayIndex(key, out var index))
                {
                    var snapshot = _sourceProvider();
                    if (index < (uint)snapshot.Count)
                    {
                        return snapshot[(int)index];
                    }

                    return FenValue.Undefined;
                }

                if (_expandos.TryGetValue(key, out var expandoValue))
                {
                    return expandoValue;
                }

                switch (key)
                {
                    case "length":
                        return FenValue.FromNumber(_sourceProvider().Count);
                    case "item":
                        return FenValue.FromFunction(new FenFunction("item", (args, thisVal) =>
                        {
                            if (args.Length == 0)
                            {
                                return FenValue.Null;
                            }

                            var number = args[0].ToNumber();
                            if (double.IsNaN(number) || double.IsInfinity(number))
                            {
                                return FenValue.Null;
                            }

                            var idx = (int)number;
                            var snapshot = _sourceProvider();
                            if (idx < 0 || idx >= snapshot.Count)
                            {
                                return FenValue.Null;
                            }

                            return snapshot[idx];
                        }));
                    default:
                        return FenValue.Undefined;
                }
            }

            public void Set(string key, FenValue value, IExecutionContext context = null)
            {
                if (string.Equals(key, "length", StringComparison.Ordinal) || string.Equals(key, "item", StringComparison.Ordinal))
                {
                    return;
                }

                if (TryParseArrayIndex(key, out _))
                {
                    return;
                }

                _expandos[key] = value;
            }

            public bool Has(string key, IExecutionContext context = null)
            {
                if (string.Equals(key, "length", StringComparison.Ordinal) || string.Equals(key, "item", StringComparison.Ordinal))
                {
                    return true;
                }

                if (_expandos.ContainsKey(key))
                {
                    return true;
                }

                if (TryParseArrayIndex(key, out var index))
                {
                    return index < (uint)_sourceProvider().Count;
                }

                return false;
            }

            public bool Delete(string key, IExecutionContext context = null)
            {
                if (string.Equals(key, "length", StringComparison.Ordinal) || string.Equals(key, "item", StringComparison.Ordinal))
                {
                    return false;
                }

                if (TryParseArrayIndex(key, out var index))
                {
                    return index >= (uint)_sourceProvider().Count;
                }

                return _expandos.Remove(key) || !_expandos.ContainsKey(key);
            }

            public IEnumerable<string> Keys(IExecutionContext context = null)
            {
                var snapshot = _sourceProvider();
                for (var i = 0; i < snapshot.Count; i++)
                {
                    yield return i.ToString();
                }

                yield return "length";
                yield return "item";

                foreach (var key in _expandos.Keys)
                {
                    yield return key;
                }
            }

            public IObject GetPrototype() => _prototype;

            public void SetPrototype(IObject prototype) => _prototype = prototype;

            public bool DefineOwnProperty(string key, PropertyDescriptor desc)
            {
                if (string.Equals(key, "length", StringComparison.Ordinal) || string.Equals(key, "item", StringComparison.Ordinal))
                {
                    return false;
                }

                if (TryParseArrayIndex(key, out var index))
                {
                    return index >= (uint)_sourceProvider().Count;
                }

                if (desc.IsAccessor)
                {
                    return false;
                }

                _expandos[key] = desc.Value ?? FenValue.Undefined;
                return true;
            }

            private static bool TryParseArrayIndex(string key, out uint index)
            {
                index = 0;
                if (string.IsNullOrWhiteSpace(key))
                {
                    return false;
                }

                return uint.TryParse(key, out index) && index != uint.MaxValue;
            }
        }

        private List<FenValue> CollectStyleSheetValues()
        {
            var results = new List<FenValue>();
            foreach (var element in EnumerateDocumentElements())
            {
                if (!HtmlElementInterfaceCatalog.IsHtmlNamespace(element.NamespaceUri))
                {
                    continue;
                }

                if (string.Equals(element.TagName, "style", StringComparison.OrdinalIgnoreCase))
                {
                    var inlineSheet = ResolveInlineStyleSheetValue(element);
                    if (!inlineSheet.IsNull && !inlineSheet.IsUndefined)
                    {
                        results.Add(inlineSheet);
                    }

                    continue;
                }

                if (IsStyleLinkElement(element))
                {
                    results.Add(CreateLinkedStyleSheetValue(element));
                }
            }

            return results;
        }

        private FenValue ResolveInlineStyleSheetValue(Element styleElement)
        {
            var wrapped = DomWrapperFactory.Wrap(styleElement, _context);
            if (!wrapped.IsObject)
            {
                return FenValue.Null;
            }

            var sheet = wrapped.AsObject().Get("sheet", _context);
            if (!sheet.IsUndefined)
            {
                return sheet;
            }

            var stylesheet = wrapped.AsObject().Get("stylesheet", _context);
            return stylesheet.IsUndefined ? FenValue.Null : stylesheet;
        }

        private FenValue CreateLinkedStyleSheetValue(Element linkElement)
        {
            if (!s_linkedStyleSheetByElement.TryGetValue(linkElement, out var sheet))
            {
                sheet = new FenObject();
                s_linkedStyleSheetByElement.Add(linkElement, sheet);
            }

            sheet.Set("ownerNode", DomWrapperFactory.Wrap(linkElement, _context));
            sheet.Set("href", FenValue.FromString(ResolveLinkHref(linkElement)));
            sheet.Set("media", FenValue.FromString(linkElement.GetAttribute("media") ?? string.Empty));
            sheet.Set("type", FenValue.FromString(linkElement.GetAttribute("type") ?? "text/css"));
            sheet.Set("disabled", FenValue.FromBoolean(linkElement.HasAttribute("disabled")));

            var rules = FenObject.CreateArray();
            rules.Set("length", FenValue.FromNumber(0));
            sheet.Set("cssRules", FenValue.FromObject(rules));

            return FenValue.FromObject(sheet);
        }

        private string ResolveLinkHref(Element linkElement)
        {
            var href = linkElement?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                return string.Empty;
            }

            return ResourceUrlResolver.Resolve(href, GetDocumentBaseUri()) ?? href;
        }

        private static bool IsStyleLinkElement(Element element)
        {
            if (element == null ||
                !string.Equals(element.TagName, "link", StringComparison.OrdinalIgnoreCase) ||
                !HtmlElementInterfaceCatalog.IsHtmlNamespace(element.NamespaceUri))
            {
                return false;
            }

            var rel = element.GetAttribute("rel");
            if (string.IsNullOrWhiteSpace(rel))
            {
                return false;
            }

            var relTokens = rel.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < relTokens.Length; i++)
            {
                if (string.Equals(relTokens[i], "stylesheet", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private IEnumerable<Element> GetElementsByTagNames(params string[] tagNames)
        {
            if (tagNames == null || tagNames.Length == 0)
            {
                return Enumerable.Empty<Element>();
            }

            var tagSet = new HashSet<string>(
                tagNames.Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            return EnumerateDocumentElements()
                .Where(element =>
                    HtmlElementInterfaceCatalog.IsHtmlNamespace(element.NamespaceUri) &&
                    tagSet.Contains(element.TagName ?? string.Empty));
        }

        private IEnumerable<Element> GetDocumentAnchors()
        {
            return EnumerateDocumentElements()
                .Where(element =>
                    HtmlElementInterfaceCatalog.IsHtmlNamespace(element.NamespaceUri) &&
                    string.Equals(element.TagName, "a", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(element.GetAttribute("name")));
        }

        private IEnumerable<Element> EnumerateDocumentElements()
        {
            var nodes = new List<Node>();
            CollectNodes(_root, nodes);
            return nodes.OfType<Element>();
        }

        private static bool IsHyperlinkElement(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (!string.Equals(element.NamespaceUri, Namespaces.Html, StringComparison.Ordinal))
            {
                return false;
            }

            var tag = element.TagName?.ToLowerInvariant();
            if (tag != "a" && tag != "area")
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(element.GetAttribute("href"));
        }

        private string GetDocumentUrl()
        {
            var document = _root as Document ?? _root.OwnerDocument;
            if (!string.IsNullOrWhiteSpace(document?.URL))
            {
                return document.URL;
            }

            return _baseUri?.AbsoluteUri ?? "about:blank";
        }

        private string GetDocumentBaseUri()
        {
            var document = _root as Document ?? _root.OwnerDocument;
            if (!string.IsNullOrWhiteSpace(document?.BaseURI))
            {
                return document.BaseURI;
            }

            if (!string.IsNullOrWhiteSpace(document?.URL))
            {
                return document.URL;
            }

            return _baseUri?.AbsoluteUri ?? "about:blank";
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

        private static Node UnwrapNode(IObject wrapper)
        {
            if (wrapper is AttrWrapper)
            {
                throw new DomException("HierarchyRequestError", "Attributes cannot be inserted into the child node list.");
            }

            return (wrapper as NodeWrapper)?.Node ?? (wrapper as ElementWrapper)?.Element ?? (wrapper as DocumentWrapper)?.Node;
        }

        // --- Event Listener Implementation ---

        private FenValue AddEventListenerMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length < 2) return FenValue.Undefined;

            var type = args[0].ToString();
            var callback = args[1];

            bool capture = false;
            bool once = false;
            bool passive = false;
            if (args.Length >= 3)
            {
                if (!args[2].IsObject || args[2].IsNull) capture = args[2].ToBoolean();
                else if (args[2].IsObject)
                {
                    var opts = args[2].AsObject() as FenObject;
                    if (opts != null)
                    {
                        var cVal = opts.Get("capture");
                        capture = cVal.ToBoolean();
                        var oVal = opts.Get("once");
                        once = oVal.ToBoolean();
                        var pVal = opts.Get("passive");
                        passive = pVal.ToBoolean();
                    }
                }
            }

            var callbackIsValid = callback.IsFunction || (callback.IsObject && !callback.IsNull);
            if (string.IsNullOrEmpty(type) || !callbackIsValid || callback.IsUndefined || callback.IsNull)
                return FenValue.Undefined;

            EngineLogCompat.Debug($"[DocumentWrapper] addEventListener called for '{type}'", FenBrowser.Core.Logging.LogCategory.JavaScript);

            AddToDocumentListenerStore(type, callback, capture, once, passive);
            return FenValue.Undefined;
        }

        private FenValue RemoveEventListenerMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length < 2) return FenValue.Undefined;
            var type = args[0].ToString();
            var callback = args[1];
            bool capture = false;
            if (args.Length >= 3)
            {
                if (!args[2].IsObject || args[2].IsNull)
                {
                    capture = args[2].ToBoolean();
                }
                else
                {
                    var opts = args[2].AsObject() as FenObject;
                    if (opts != null)
                    {
                        capture = opts.Get("capture").ToBoolean();
                    }
                }
            }

            RemoveFromDocumentListenerStore(type, callback, capture);
            return FenValue.Undefined;
        }

        private FenValue DispatchEventMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length == 0 || !args[0].IsObject || args[0].IsNull || args[0].IsUndefined)
            {
                throw new FenTypeError("TypeError: Failed to execute 'dispatchEvent': parameter 1 is not of type 'Event'.");
            }

            var eventObj = args[0].AsObject() as DomEvent;
            if (eventObj == null)
            {
                var obj = args[0].AsObject() as FenObject;
                if (obj == null) return FenValue.FromBoolean(false);
                var typeVal = obj.Get("type");
                var type = !typeVal.IsUndefined ? typeVal.ToString() : "";
                eventObj = new DomEvent(type);
            }

            EngineLogCompat.Debug($"[DocumentWrapper] dispatchEvent '{eventObj.Type}'", FenBrowser.Core.Logging.LogCategory.JavaScript);

            var notPrevented = false;
            var rootElement = GetDocumentEventTargetElement();
            if (rootElement != null)
            {
                notPrevented = EventTarget.DispatchEvent(rootElement, eventObj, _context);
            }

            return FenValue.FromBoolean(notPrevented);
        }

        private static string NormalizeCreateEventInterface(string interfaceName)
        {
            return interfaceName.ToLowerInvariant() switch
            {
                "event" => "Event",
                "events" => "Event",
                "htmlevents" => "Event",
                "svgevents" => "Event",
                "customevent" => "CustomEvent",
                "uievent" => "UIEvent",
                "uievents" => "UIEvent",
                "mouseevent" => "MouseEvent",
                "mouseevents" => "MouseEvent",
                "keyboardevent" => "KeyboardEvent",
                "compositionevent" => "CompositionEvent",
                "beforeunloadevent" => "BeforeUnloadEvent",
                "devicemotionevent" => "DeviceMotionEvent",
                "deviceorientationevent" => "DeviceOrientationEvent",
                "dragevent" => "DragEvent",
                "focusevent" => "FocusEvent",
                "hashchangeevent" => "HashChangeEvent",
                "messageevent" => "MessageEvent",
                "storageevent" => "StorageEvent",
                "textevent" => "TextEvent",
                _ => interfaceName
            };
        }

        private FenValue ApplyEventPrototype(DomEvent evt, string interfaceName)
        {
            var ctor = _context?.Environment?.Get(interfaceName) ?? FenValue.Undefined;
            if (ctor.IsFunction)
            {
                var prototype = ctor.AsFunction()?.Get("prototype", _context) ?? FenValue.Undefined;
                if (prototype.IsObject)
                {
                    evt.SetPrototype(prototype.AsObject());
                }
            }

            return FenValue.FromObject(evt);
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
        }

        private FenValue WrapAttribute(Attr attr)
        {
            var wrapper = new AttrWrapper(attr, _context);
            var attrCtor = _context?.Environment?.Get("Attr") ?? FenValue.Undefined;
            if (attrCtor.IsFunction)
            {
                var prototype = attrCtor.AsFunction()?.Get("prototype", _context) ?? FenValue.Undefined;
                if (prototype.IsObject)
                {
                    wrapper.SetPrototype(prototype.AsObject());
                }
            }

            return FenValue.FromObject(wrapper);
        }

        private FenValue WrapDocument(Document document)
        {
            return DomWrapperFactory.Wrap(document, _context);
        }

        // --- Cookie Implementation ---
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
                catch (Exception ex) { FenBrowser.Core.EngineLogCompat.Warn($"[DocumentWrapper] Cookie read bridge failed: {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript); }
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
                catch (Exception ex) { FenBrowser.Core.EngineLogCompat.Warn($"[DocumentWrapper] Cookie write bridge failed: {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript); }
            }

            _cookieStore.SetCookie(cookieStr, _baseUri);
            EngineLogCompat.Debug($"[DocumentWrapper] Cookie set: {cookieStr.Split(';')[0]}", FenBrowser.Core.Logging.LogCategory.JavaScript);
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





