using FenBrowser.Core.Dom.V2;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Runtime.CompilerServices;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Wraps a Element to expose it to JavaScript.
    /// Provides DOM manipulation methods with permission checking.
    /// </summary>
    public partial class ElementWrapper : NodeWrapper
    {
        private readonly Element _element;
        private static readonly ConditionalWeakTable<Element, FenObject> s_iframeWindowByElement = new ConditionalWeakTable<Element, FenObject>();
        // _context is in base

        public ElementWrapper(Element element, IExecutionContext context) : base(element, context)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));
            // _context set in base
        }

        public Element Element => _element;

        public override FenValue Get(string key, IExecutionContext context = null)
        {
            _context?.CheckExecutionTimeLimit();
            
            switch (key.ToLowerInvariant())
            {
                case "innerhtml":
                    return GetInnerHTML();
                
                case "textcontent":
                    return GetTextContent();
                
                case "tagname":
                    return FenValue.FromString(_element.NodeName?.ToUpperInvariant() ?? "");
                
                case "id":
                    return FenValue.FromString(_element.Id ?? "");
                
                case "getattribute":
                    return FenValue.FromFunction(new FenFunction("getAttribute", GetAttribute));
                
                case "setattribute":
                    return FenValue.FromFunction(new FenFunction("setAttribute", SetAttribute));

                case "hasattribute":
                    return FenValue.FromFunction(new FenFunction("hasAttribute", HasAttribute));

                case "removeattribute":
                    return FenValue.FromFunction(new FenFunction("removeAttribute", RemoveAttribute));

                case "attributes":
                    return FenValue.FromObject(new NamedNodeMapWrapper(_element.Attributes, _context));

                case "getattributenode":
                    return FenValue.FromFunction(new FenFunction("getAttributeNode", GetAttributeNode));
                
                case "setattributenode":
                    return FenValue.FromFunction(new FenFunction("setAttributeNode", SetAttributeNode));
                
                case "removeattributenode":
                    return FenValue.FromFunction(new FenFunction("removeAttributeNode", RemoveAttributeNode));

                case "width":
                    return FenValue.FromNumber(GetDimension("width"));
                
                case "height":
                    return FenValue.FromNumber(GetDimension("height"));

                case "clientwidth":
                    // clientWidth - inner width without scrollbar (for viewport calculations)
                    // For documentElement, return viewport width
                    return FenValue.FromNumber(GetClientWidth());
                
                case "clientheight":
                    // clientHeight - inner height without scrollbar (for viewport calculations)
                    // For documentElement, return viewport height
                    return FenValue.FromNumber(GetClientHeight());

                case "getcontext":

                    return FenValue.FromFunction(new FenFunction("getContext", GetContext));
                
                case "removechild":
                    return FenValue.FromFunction(new FenFunction("removeChild", RemoveChild));


                case "appendchild":
                    return FenValue.FromFunction(new FenFunction("appendChild", AppendChild));

                case "style":
                    return FenValue.FromObject(new CSSStyleDeclaration(_element, _context));

                case "matches":
                    return FenValue.FromFunction(new FenFunction("matches", MatchesSelector));

                case "closest":
                    return FenValue.FromFunction(new FenFunction("closest", ClosestSelector));

                case "queryselector":
                    return FenValue.FromFunction(new FenFunction("querySelector", QuerySelector));

                case "queryselectorall":
                    return FenValue.FromFunction(new FenFunction("querySelectorAll", QuerySelectorAll));

                case "getelementsbytagname":
                    return FenValue.FromFunction(new FenFunction("getElementsByTagName", GetElementsByTagNameMethod));

                case "getelementsbytagnamens":
                    return FenValue.FromFunction(new FenFunction("getElementsByTagNameNS", GetElementsByTagNameNSMethod));

                case "getelementsbyclassname":
                    return FenValue.FromFunction(new FenFunction("getElementsByClassName", GetElementsByClassNameMethod));

                case "type":
                    return FenValue.FromString(_element.GetAttribute("type") ?? string.Empty);

                case "checked":
                    return FenValue.FromBoolean(_element.HasAttribute("checked"));

                case "disabled":
                    return FenValue.FromBoolean(_element.HasAttribute("disabled"));

                case "click":
                    return FenValue.FromFunction(new FenFunction("click", ClickMethod));

                case "classname":
                    return FenValue.FromString(_element.GetAttribute("class") ?? "");

                case "parentelement":
                case "parentnode":
                    if (_element.ParentNode != null && _element.ParentNode is Element parentEl)
                        return FenValue.FromObject(new ElementWrapper(parentEl, _context));
                    return FenValue.Null;

                case "children":
                    var childElements = _element.ChildNodes?.OfType<Element>(); // HTMLCollection is live ideally, but for now snapshot or wrap list
                    return FenValue.FromObject(new HTMLCollectionWrapper(childElements ?? Enumerable.Empty<Element>(), _context));

                case "firstelementchild":
                    var firstChild = _element.ChildNodes?.OfType<Element>().FirstOrDefault();
                    return firstChild != null ? FenValue.FromObject(new ElementWrapper(firstChild, _context)) : FenValue.Null;

                case "lastelementchild":
                    var lastChild = _element.ChildNodes?.OfType<Element>().LastOrDefault();
                    return lastChild != null ? FenValue.FromObject(new ElementWrapper(lastChild, _context)) : FenValue.Null;
                
                // DIALOG ELEMENT METHODS
                case "show":
                    return FenValue.FromFunction(new FenFunction("show", ShowDialog));
                
                case "showmodal":
                    return FenValue.FromFunction(new FenFunction("showModal", ShowModalDialog));
                
                case "close":
                    return FenValue.FromFunction(new FenFunction("close", CloseDialog));
                
                case "open":
                    // Check if dialog is open
                    if (_element.TagName?.ToUpperInvariant() == "DIALOG")
                        return FenValue.FromBoolean(_element.HasAttribute("open"));
                    return FenValue.Undefined;
                
                
                // Shadow DOM
                case "attachshadow":
                    return FenValue.FromFunction(new FenFunction("attachShadow", AttachShadow));
                
                case "shadowroot":
                    if (_element.ShadowRoot != null)
                    {
                        if (_element.ShadowRoot.Mode == ShadowRootMode.Closed) return FenValue.Null;
                        return FenValue.FromObject(new ShadowRootWrapper(_element.ShadowRoot, _context));
                        // ShadowRootWrapper needs update to V2 ShadowRoot
                    }
                    return FenValue.Null;
                
                // DOM Level 3 Events
                case "addeventlistener":
                    return FenValue.FromFunction(new FenFunction("addEventListener", AddEventListenerMethod));
                
                case "removeeventlistener":
                    return FenValue.FromFunction(new FenFunction("removeEventListener", RemoveEventListenerMethod));
                
                case "dispatchevent":
                    return FenValue.FromFunction(new FenFunction("dispatchEvent", DispatchEventMethod));

                case "focus":
                    return FenValue.FromFunction(new FenFunction("focus", FocusMethod));

                case "blur":
                    return FenValue.FromFunction(new FenFunction("blur", BlurMethod));

                case "clonenode":
                    return FenValue.FromFunction(new FenFunction("cloneNode", CloneNodeMethod));

                case "dataset":
                    return FenValue.FromObject(new DOMStringMap(_element, _context));

                case "classlist":
                    return FenValue.FromObject(new DOMTokenList(_element, "class", _context));
                
                case "getboundingclientrect":
                    return FenValue.FromFunction(new FenFunction("getBoundingClientRect", GetBoundingClientRectMethod));
                
                case "getclientrects":
                    return FenValue.FromFunction(new FenFunction("getClientRects", GetClientRectsMethod));

                case "contentwindow":
                    return GetContentWindow();

                case "contentdocument":
                    return GetContentDocument();

                default:
                    return base.Get(key, context);
            }
        }

        private FenValue GetContentWindow()
        {
            if (!string.Equals(_element.TagName, "iframe", StringComparison.OrdinalIgnoreCase))
            {
                return FenValue.Null;
            }

            if (!s_iframeWindowByElement.TryGetValue(_element, out var frameWindow))
            {
                frameWindow = new FenObject();

                var abortSignal = new FenObject();
                abortSignal.Set("timeout", FenValue.FromFunction(new FenFunction("timeout", (args, thisVal) =>
                {
                    int delayMs = 0;
                    if (args.Length > 0)
                    {
                        delayMs = (int)Math.Max(0, args[0].ToNumber());
                    }

                    var signal = new FenObject();
                    signal.Set("aborted", FenValue.FromBoolean(false));
                    signal.Set("reason", FenValue.Undefined);
                    signal.Set("onabort", FenValue.Undefined);

                    var listeners = new List<FenValue>();
                    signal.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (sigArgs, sigThis) =>
                    {
                        if (sigArgs.Length >= 2 && string.Equals(sigArgs[0].ToString(), "abort", StringComparison.OrdinalIgnoreCase))
                        {
                            listeners.Add(sigArgs[1]);
                        }
                        return FenValue.Undefined;
                    })));

                    signal.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (sigArgs, sigThis) =>
                    {
                        if (sigArgs.Length >= 2 && string.Equals(sigArgs[0].ToString(), "abort", StringComparison.OrdinalIgnoreCase))
                        {
                            listeners.RemoveAll(l => l.Equals(sigArgs[1]));
                        }
                        return FenValue.Undefined;
                    })));

                    _context?.ScheduleCallback?.Invoke(() =>
                    {
                        // If iframe was detached before timeout, this context is considered gone.
                        if (_element.ParentNode == null)
                        {
                            return;
                        }

                        if (signal.Get("aborted").ToBoolean())
                        {
                            return;
                        }

                        var reason = FenValue.FromString("TimeoutError");
                        signal.Set("aborted", FenValue.FromBoolean(true));
                        signal.Set("reason", reason);

                        var onAbort = signal.Get("onabort");
                        if (onAbort.IsFunction)
                        {
                            onAbort.AsFunction().Invoke(new[] { reason }, _context, FenValue.FromObject(signal));
                        }

                        foreach (var listener in listeners.ToList())
                        {
                            if (listener.IsFunction)
                            {
                                listener.AsFunction().Invoke(new[] { reason }, _context, FenValue.FromObject(signal));
                            }
                        }
                    }, delayMs);

                    return FenValue.FromObject(signal);
                })));

                frameWindow.Set("AbortSignal", FenValue.FromObject(abortSignal));

                var env = _context?.Environment;
                if (env != null)
                {
                    var doc = env.Get("document");
                    if (doc.IsObject)
                    {
                        frameWindow.Set("document", doc);
                    }
                }

                s_iframeWindowByElement.Add(_element, frameWindow);
            }

            return FenValue.FromObject(frameWindow);
        }

        private FenValue GetContentDocument()
        {
            if (!string.Equals(_element.TagName, "iframe", StringComparison.OrdinalIgnoreCase))
            {
                return FenValue.Null;
            }

            var frameWindow = GetContentWindow();
            if (frameWindow.IsObject)
            {
                var doc = frameWindow.AsObject().Get("document");
                if (doc.IsObject)
                {
                    return doc;
                }
            }

            var env = _context?.Environment;
            if (env == null)
            {
                return FenValue.Null;
            }

            var fallbackDoc = env.Get("document");
            return fallbackDoc.IsObject ? fallbackDoc : FenValue.Null;
        }


        public override void Set(string key, FenValue value, IExecutionContext context = null)
        {
            if (_context != null)
            {
                _context.CheckExecutionTimeLimit();
                if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, $"Set {key}"))
                    throw new FenSecurityError("DOM write permission required");
            }

            switch (key.ToLowerInvariant())
            {
                case "innerhtml":
                    SetInnerHTML(value);
                    break;

                case "textcontent":
                    SetTextContent(value);
                    break;

                case "id":
                {
                    var idValue = value.AsString(context ?? _context);
                    _element.SetAttribute("id", idValue ?? string.Empty);
                    break;
                }

                case "classname":
                {
                    var classValue = value.AsString(context ?? _context);
                    _element.SetAttribute("class", classValue ?? string.Empty);
                    break;
                }

                case "type":
                    _element.SetAttribute("type", value.ToString() ?? string.Empty);
                    break;

                case "checked":
                    if (value.ToBoolean()) _element.SetAttribute("checked", "");
                    else _element.RemoveAttribute("checked");
                    break;

                case "disabled":
                    if (value.ToBoolean()) _element.SetAttribute("disabled", "");
                    else _element.RemoveAttribute("disabled");
                    break;

                default:
                    base.Set(key, value, context);
                    break;
            }
        }

        public override bool Has(string key, IExecutionContext context = null) => !Get(key, context).IsUndefined;
        public override bool Delete(string key, IExecutionContext context = null) => false;

        public override System.Collections.Generic.IEnumerable<string> Keys(IExecutionContext context = null) 
            => new[] { "attachShadow", "shadowRoot", "innerHTML", "textContent", "tagName", "id", "className", "type", "checked", "disabled", "attributes", "getAttribute", "setAttribute", "hasAttribute", "removeAttribute", "getAttributeNode", "setAttributeNode", "removeAttributeNode", "getElementsByTagName", "getElementsByTagNameNS", "getElementsByClassName", "querySelector", "querySelectorAll", "addEventListener", "removeEventListener", "dispatchEvent", "click", "focus", "blur", "getContext", "width", "height", "clientWidth", "clientHeight" };
        
        private FenValue GetContext(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var type = args[0].ToString()?.ToLowerInvariant();
            
            // Check if element is canvas
            if (!string.Equals(_element.TagName, "canvas", StringComparison.OrdinalIgnoreCase))
                return FenValue.Null;
            
            // Get canvas dimensions
            int width = 300, height = 150; // Default canvas size per HTML spec
            // V2 Element Helper for getting int attributes
            if (int.TryParse(_element.GetAttribute("width"), out var pw)) width = pw;
            if (int.TryParse(_element.GetAttribute("height"), out var ph)) height = ph;
            
            // 2D Canvas Context
            if (type == "2d")
            {
                return FenValue.FromObject(new FenBrowser.FenEngine.Scripting.CanvasRenderingContext2D(_element, null));
            }
            
            // WebGL Context
            if (type == "webgl" || type == "experimental-webgl")
            {
                var canvasId = _element.GetAttribute("id") ?? _element.GetHashCode().ToString();
                var context = FenBrowser.FenEngine.Rendering.WebGL.WebGLContextManager.GetContext(canvasId, width, height, webgl2: false);
                if (context != null)
                {
                    // Create JavaScript wrapper object with all WebGL methods and constants
                    var wrapper = FenBrowser.FenEngine.Rendering.WebGL.WebGLJavaScriptBindings.CreateJSWrapper(context);
                    return FenValue.FromObject(new WebGLContextWrapper(context, wrapper, _element, _context));
                }
            }

            // WebGL2 Context
            if (type == "webgl2")
            {
                var canvasId = _element.GetAttribute("id") ?? _element.GetHashCode().ToString();
                var context = FenBrowser.FenEngine.Rendering.WebGL.WebGLContextManager.GetContext(canvasId, width, height, webgl2: true);
                if (context != null)
                {
                    var wrapper = FenBrowser.FenEngine.Rendering.WebGL.WebGLJavaScriptBindings.CreateJSWrapper(context);
                    return FenValue.FromObject(new WebGLContextWrapper(context, wrapper, _element, _context));
                }
            }
            
            return FenValue.Null;
        }
        
        /// <summary>
        /// Wrapper to expose WebGL context to JavaScript
        /// </summary>
        private class WebGLContextWrapper : IObject
        {
            private readonly FenBrowser.FenEngine.Rendering.WebGL.WebGLRenderingContext _context;
            private readonly Dictionary<string, object> _methods;
            private readonly FenBrowser.Core.Dom.V2.Element _canvasElement;
            private readonly IExecutionContext _execContext;
            private IObject _prototype;
            public object NativeObject { get; set; }

            public WebGLContextWrapper(FenBrowser.FenEngine.Rendering.WebGL.WebGLRenderingContext context, object methods,
                FenBrowser.Core.Dom.V2.Element canvasElement = null, IExecutionContext execContext = null)
            {
                _context = context;
                _methods = methods as Dictionary<string, object> ?? new Dictionary<string, object>();
                _canvasElement = canvasElement;
                _execContext = execContext;
            }
            
            public FenValue Get(string key, IExecutionContext context = null)
            {
                if (_methods.TryGetValue(key, out var value))
                {
                    // Convert delegates to FenFunction
                    if (value is Delegate del)
                    {
                        return FenValue.FromFunction(new FenFunction(key, (args, thisVal) =>
                        {
                            try
                            {
                                var parameters = del.Method.GetParameters();
                                var convertedArgs = new object[parameters.Length];
                                for (int i = 0; i < parameters.Length && i < args.Length; i++)
                                {
                                    convertedArgs[i] = ConvertArg(args[i], parameters[i].ParameterType);
                                }
                                var result = del.DynamicInvoke(convertedArgs);
                                return ConvertResult(result);
                            }
                            catch (Exception ex)
                            {
                                // Log error silently - FenLogger may not be available in this context
                                System.Diagnostics.Debug.WriteLine($"[WebGL] Error calling {key}: {ex.Message}");
                                return FenValue.Undefined;
                            }
                        }));
                    }
                    // Constants (uint, int, etc.)
                    if (value is uint ui) return FenValue.FromNumber(ui);
                    if (value is int i) return FenValue.FromNumber(i);
                    if (value is float f) return FenValue.FromNumber(f);
                    if (value is double d) return FenValue.FromNumber(d);
                    if (value is string s) return FenValue.FromString(s);
                    if (value is bool b) return FenValue.FromBoolean(b);
                }
                
                // Also expose properties like drawingBufferWidth, drawingBufferHeight
                if (key == "drawingBufferWidth") return FenValue.FromNumber(_context.DrawingBufferWidth);
                if (key == "drawingBufferHeight") return FenValue.FromNumber(_context.DrawingBufferHeight);
                if (key == "canvas") return _canvasElement != null
                    ? DomWrapperFactory.Wrap(_canvasElement, _execContext)
                    : FenValue.Null;
                
                return FenValue.Undefined;
            }
            
            private object ConvertArg(IValue arg, Type targetType)
            {
                if (targetType == typeof(uint)) return (uint)arg.ToNumber();
                if (targetType == typeof(int)) return (int)arg.ToNumber();
                if (targetType == typeof(float)) return (float)arg.ToNumber();
                if (targetType == typeof(double)) return arg.ToNumber();
                if (targetType == typeof(bool)) return arg.ToBoolean();
                if (targetType == typeof(string)) return arg.ToString();
                if (targetType == typeof(byte[]))
                {
                    // Convert array-like object to byte array
                    if (arg.IsObject)
                    {
                        var obj = arg.AsObject();
                        var lenVal = obj.Get("length");
                        if (lenVal != null && lenVal.IsNumber)
                        {
                            int len = (int)lenVal.ToNumber();
                            var bytes = new byte[len];
                            for (int i = 0; i < len; i++)
                            {
                                var v = obj.Get(i.ToString());
                                bytes[i] = v != null ? (byte)v.ToNumber() : (byte)0;
                            }
                            return bytes;
                        }
                    }
                    return FenValue.Null;
                }
                if (targetType == typeof(float[]))
                {
                    if (arg.IsObject)
                    {
                        var obj = arg.AsObject();
                        var lenVal = obj.Get("length");
                        if (lenVal != null && lenVal.IsNumber)
                        {
                            int len = (int)lenVal.ToNumber();
                            var floats = new float[len];
                            for (int i = 0; i < len; i++)
                            {
                                var v = obj.Get(i.ToString());
                                floats[i] = v != null ? (float)v.ToNumber() : 0f;
                            }
                            return floats;
                        }
                    }
                    return FenValue.Null;
                }
                // WebGL objects - check if it's already the correct type
                if (arg.IsObject)
                {
                    var obj = arg.AsObject();
                    if (targetType.IsAssignableFrom(obj.GetType()))
                        return obj;
                }
                return FenValue.Null;
            }
            
            private FenValue ConvertResult(object result)
            {
                if (result  == null) return FenValue.Null;
                if (result is uint ui) return FenValue.FromNumber(ui);
                if (result is int i) return FenValue.FromNumber(i);
                if (result is float f) return FenValue.FromNumber(f);
                if (result is double d) return FenValue.FromNumber(d);
                if (result is bool b) return FenValue.FromBoolean(b);
                if (result is string s) return FenValue.FromString(s);
                if (result is byte[] bytes)
                {
                    var arr = new FenObject();
                    for (int j = 0; j < bytes.Length; j++)
                        arr.Set(j.ToString(), FenValue.FromNumber(bytes[j]));
                    arr.Set("length", FenValue.FromNumber(bytes.Length));
                    return FenValue.FromObject(arr);
                }
                if (result is string[] strings)
                {
                    var arr = new FenObject();
                    for (int j = 0; j < strings.Length; j++)
                        arr.Set(j.ToString(), FenValue.FromString(strings[j]));
                    arr.Set("length", FenValue.FromNumber(strings.Length));
                    return FenValue.FromObject(arr);
                }
                // Return WebGL objects as-is (they implement IObject)
                if (result is IObject obj) return FenValue.FromObject(obj);
                // Unknown types - just convert toString
                return FenValue.FromString(result.ToString());
            }
            
            public bool Has(string key, IExecutionContext context = null) => _methods.ContainsKey(key) || key == "drawingBufferWidth" || key == "drawingBufferHeight" || key == "canvas";
            public void Set(string key, FenValue value, IExecutionContext context = null) { /* WebGL context properties are read-only */ }
            public bool Delete(string key, IExecutionContext context = null) => false;
            public IEnumerable<string> Keys(IExecutionContext context = null) => _methods.Keys;
            public IObject GetPrototype() => _prototype;
            public void SetPrototype(IObject prototype) => _prototype = prototype;
            public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;
        }

        private FenValue GetInnerHTML()
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomRead, "innerHTML"))
                throw new FenSecurityError("DOM read permission required");

            return FenValue.FromString(CollectInnerHtml(_element));
        }

        private string CollectInnerHtml(Node node)
        {
            if (node  == null) return "";
            if (!(node is Element element))
                return node.NodeType == NodeType.Text ? node.NodeValue ?? "" : "";
            
            if (element.ChildNodes == null || element.ChildNodes.Length == 0)
                return element.TextContent ?? "";

            var sb = new StringBuilder();
            foreach (var child in element.ChildNodes)
            {
                // Simple reconstruction - in real app would need proper serialization
                if (child.NodeType == NodeType.Text)
                {
                    sb.Append(child.TextContent);
                }
                else if (child is Element childEl)
                {
                    sb.Append($"<{childEl.TagName}");
                    // Reconstruct attributes
                    foreach (var attr in childEl.Attributes)
                    {
                         sb.Append($" {attr.Name}=\"{attr.Value}\"");
                    }
                    sb.Append(">");
                    sb.Append(CollectInnerHtml(childEl));
                    sb.Append($"</{childEl.TagName}>");
                }
            }
            return sb.ToString();
        }

        private void SetInnerHTML(FenValue value)
        {
            var removed = _element.ChildNodes != null ? new System.Collections.Generic.List<Node>(_element.ChildNodes) : new System.Collections.Generic.List<Node>();

            // NodeList is read-only, clear via TextContent
            _element.TextContent = "";
            var htmlString = value.ToString();
            
            var added = new System.Collections.Generic.List<Node>();

            if (!string.IsNullOrEmpty(htmlString))
            {
                try
                {
                    var tokenizer = new FenBrowser.FenEngine.HTML.HtmlTokenizer(htmlString);
                    var builder = new FenBrowser.FenEngine.HTML.HtmlTreeBuilder(tokenizer);
                    var parsed = builder.Build();
                    if (parsed?.ChildNodes != null)
                    {
                        foreach (var child in parsed.ChildNodes.ToArray()) // Copy to avoid modification of source collection during iteration if active
                        {
                            // Detach from parsed root? Or Copy?
                            // builder returns a tree. We can probably just reparent.
                            // But checking if child is Element
                            if(child is Node n) {
                                _element.AppendChild(n);
                                added.Add(n);
                            }
                        }
                    }
                }
                catch
                {
                    var textNode = new Text(htmlString);
                    _element.AppendChild(textNode);
                    added.Add(textNode);
                }
            }
            _context.RequestRender?.Invoke();

            _context.OnMutation?.Invoke(new FenBrowser.Core.Dom.V2.MutationRecord
            {
                Type = FenBrowser.Core.Dom.V2.MutationRecordType.ChildList,
                Target = _element,
                AddedNodes = added,
                RemovedNodes = removed
            });
        }

        private FenValue GetTextContent()
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomRead, "textContent"))
                throw new FenSecurityError("DOM read permission required");

            return FenValue.FromString(CollectText(_element));
        }

        private string CollectText(Node node)
        {
            if (node  == null) return "";
            if (node.NodeType == NodeType.Text) return node.TextContent ?? "";
            if (node.ChildNodes  == null) return "";
            
            var sb = new StringBuilder();
            foreach (var child in node.ChildNodes)
            {
                sb.Append(CollectText(child));
            }
            return sb.ToString();
        }

        private void SetTextContent(FenValue value)
        {
            var text = value.ToString();
            
            // Enqueue mutation (Deferred)
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.TextChange,
                InvalidationKind.Layout | InvalidationKind.Paint,
                _element,
                "textContent",
                null,
                text
            ));
        }

        private FenValue CreateElement(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var attrName = args[0].ToString();
            return _element.GetAttribute(attrName) != null
                ? FenValue.FromString(_element.GetAttribute(attrName))
                : FenValue.Null;
        }

        private FenValue GetAttribute(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var attrName = args[0].ToString();
            var val = _element.GetAttribute(attrName);
            return val != null ? FenValue.FromString(val) : FenValue.Null;
        }

        private FenValue SetAttribute(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "setAttribute"))
                throw new FenSecurityError("DOM write permission required");

            if (args.Length < 2) return FenValue.Undefined;

            var name = args[0].ToString();
            var value = args[1].ToString();
            var oldValue = _element.GetAttribute(name);

            // Apply immediately for DOM API sync semantics expected by WPT.
            _element.SetAttribute(name, value);

            // Keep mutation signal for pipeline invalidation / diagnostics.
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                name,
                oldValue,
                value
            ));

            _context.RequestRender?.Invoke();
            return FenValue.Undefined;
        }

        private FenValue HasAttribute(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.FromBoolean(false);
            return FenValue.FromBoolean(_element.HasAttribute(args[0].ToString()));
        }

        private FenValue RemoveAttribute(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "removeAttribute"))
                throw new FenSecurityError("DOM write permission required");

            if (args.Length == 0) return FenValue.Undefined;
            _element.RemoveAttribute(args[0].ToString());
            return FenValue.Undefined;
        }

        private FenValue GetAttributeNode(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var attr = _element.GetAttributeNode(args[0].ToString());
            return attr != null ? FenValue.FromObject(new AttrWrapper(attr, _context)) : FenValue.Null;
        }

        private FenValue SetAttributeNode(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "setAttributeNode"))
                throw new FenSecurityError("DOM write permission required");

             if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;
             
             var wrapper = args[0].AsObject() as AttrWrapper;
             if (wrapper  == null) throw new FenSecurityError("Argument must be an Attr node");

             try
             {
                 var old = _element.SetAttributeNode(wrapper.Attr);
                 return old != null ? FenValue.FromObject(new AttrWrapper(old, _context)) : FenValue.Null;
             }
             catch(Exception ex)
             {
                 throw new FenSecurityError(ex.Message);
             }
        }

        private FenValue RemoveAttributeNode(FenValue[] args, FenValue thisVal)
        {
             if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "removeAttributeNode"))
                throw new FenSecurityError("DOM write permission required");

             if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;
             
             var wrapper = args[0].AsObject() as AttrWrapper;
             if (wrapper  == null) throw new FenSecurityError("Argument must be an Attr node");

             try
             {
                 var removed = _element.RemoveAttributeNode(wrapper.Attr);
                 return removed != null ? FenValue.FromObject(new AttrWrapper(removed, _context)) : FenValue.Null;
             }
             catch(Exception ex)
             {
                 throw new FenSecurityError(ex.Message);
             }
        }
        private double GetDimension(string attrName)
        {
            var val = _element.GetAttribute(attrName);
            if (val != null)
            {
                if (double.TryParse(val, out var d)) return d;
            }
            return 0;
        }

        private double GetClientWidth()
        {
            // For <html> element (documentElement), return viewport width
            if (string.Equals(_element.TagName, "html", StringComparison.OrdinalIgnoreCase))
            {
                // Return viewport width (typical desktop width)
                return 1920;
            }
            // For other elements, use width attribute or return 0
            return GetDimension("width");
        }

        private double GetClientHeight()
        {
            // For <html> element (documentElement), return viewport height
            if (string.Equals(_element.TagName, "html", StringComparison.OrdinalIgnoreCase))
            {
                // Return viewport height (typical desktop height)
                return 1080;
            }
            // For other elements, use height attribute or return 0
            return GetDimension("height");
        }

        private FenValue AppendChild(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "appendChild"))
                throw new FenSecurityError("DOM write permission required");

            if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;

            var argObj = args[0].AsObject();
            if (argObj is AttrWrapper)
            {
                throw new DomException("HierarchyRequestError", "Attributes cannot be inserted into the child node list.");
            }

            var childNode = (argObj as ElementWrapper)?.Element ?? (argObj as NodeWrapper)?.Node;

            if (childNode != null)
            {
                // Apply immediately for DOM-observable semantics, then notify invalidation pipeline.
                _element.AppendChild(childNode);

                DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                    MutationType.NodeInsert,
                    InvalidationKind.Layout | InvalidationKind.Paint,
                    _element,
                    null,
                    null,
                    childNode
                ));

                return args[0];
            }

            return FenValue.Null;
        }

        private FenValue RemoveChild(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "removeChild"))
                throw new FenSecurityError("DOM write permission required");

            if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;

            var argObj = args[0].AsObject();
            if (argObj is AttrWrapper)
            {
                throw new DomException("HierarchyRequestError", "Attributes cannot be inserted into the child node list.");
            }

            var childNode = (argObj as ElementWrapper)?.Element ?? (argObj as NodeWrapper)?.Node;

            if (childNode != null)
            {
                // Apply immediately for DOM-observable semantics, then notify invalidation pipeline.
                _element.RemoveChild(childNode);

                DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                    MutationType.NodeRemove,
                    InvalidationKind.Layout | InvalidationKind.Paint,
                    _element,
                    null,
                    childNode,
                    null
                ));

                return args[0];
            }

            return FenValue.Null;
        }

        /// <summary>
        /// Implements element.matches(selector) - checks if element matches a CSS selector
        /// </summary>
        private FenValue MatchesSelector(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsString) return FenValue.FromBoolean(false);
            
            try
            {
                var selector = args[0].ToString();
                var result = Rendering.CssLoader.MatchesSelector(_element, selector);
                return FenValue.FromBoolean(result);
            }
            catch
            {
                return FenValue.FromBoolean(false);
            }
        }

        /// <summary>
        /// Implements element.closest(selector) - finds nearest ancestor matching selector
        /// </summary>
        private FenValue ClosestSelector(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsString) return FenValue.Null;
            
            try
            {
                var selector = args[0].ToString();
                var current = _element;
                
                while (current != null)
                {
                    if (Rendering.CssLoader.MatchesSelector(current, selector))
                    {
                        return FenValue.FromObject(new ElementWrapper(current, _context));
                    }
                    current = current.ParentNode as Element;
                }
                
                return FenValue.Null;
            }
            catch
            {
                return FenValue.Null;
            }
        }

        private FenValue QuerySelector(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var selector = args[0].ToString();
            
            // Should probably use the DocumentWrapper implementation or a static helper
            // For now simplified recursive search
            var result = FindFirstDescendant(_element, selector);
            return result != null ? FenValue.FromObject(new ElementWrapper(result, _context)) : FenValue.Null;
        }
        
        private FenValue QuerySelectorAll(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return CreateEmptyArray();
            var selector = args[0].ToString();
            var results = new List<FenValue>();
            FindAllDescendants(_element, selector, results);
            return CreateArrayFromResults(results);
        }

        private Element FindFirstDescendant(Element parent, string selector)
        {
             if (parent.ChildNodes == null) return null;
             
             foreach (var child in parent.ChildNodes.OfType<Element>())
             {
                 if (Rendering.CssLoader.MatchesSelector(child, selector)) return child;
                 var f = FindFirstDescendant(child, selector);
                 if (f != null) return f;
             }
             return null;
        }
        
        private void FindAllDescendants(Element parent, string selector, List<FenValue> results)
        {
             if (parent.ChildNodes == null) return;
             
             foreach (var child in parent.ChildNodes.OfType<Element>())
             {
                 if (Rendering.CssLoader.MatchesSelector(child, selector)) results.Add(FenValue.FromObject(new ElementWrapper(child, _context)));
                 FindAllDescendants(child, selector, results);
             }
        }

        private FenValue GetElementsByTagNameMethod(FenValue[] args, FenValue thisVal)
        {
            var tag = args.Length > 0 ? args[0].ToString() : "*";
            var collection = _element.GetElementsByTagName(tag);
            return FenValue.FromObject(new HTMLCollectionWrapper(collection, _context));
        }

        private FenValue GetElementsByTagNameNSMethod(FenValue[] args, FenValue thisVal)
        {
            // Namespace-aware matching is not yet implemented in the core DOM; delegate by local name.
            var localName = args.Length > 1 ? args[1].ToString() : "*";
            var collection = _element.GetElementsByTagName(localName);
            return FenValue.FromObject(new HTMLCollectionWrapper(collection, _context));
        }

        private FenValue GetElementsByClassNameMethod(FenValue[] args, FenValue thisVal)
        {
            var classNames = args.Length > 0 ? args[0].ToString() : string.Empty;
            var collection = _element.GetElementsByClassName(classNames);
            return FenValue.FromObject(new HTMLCollectionWrapper(collection, _context));
        }
        private FenValue ShowDialog(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "show"))
                throw new FenSecurityError("DOM write permission required");
                
            _element.SetAttribute("open", "");
             // Enqueue mutation (Deferred)
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                "open",
                null,
                ""
            ));
            return FenValue.Undefined;
        }
        
        private FenValue ShowModalDialog(FenValue[] args, FenValue thisVal)
        {
             if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "showModal"))
                throw new FenSecurityError("DOM write permission required");

            _element.SetAttribute("open", "");
             // Enqueue mutation (Deferred)
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                "open",
                null,
                ""
            ));
            // Mark as top-layer modal so UA CSS can apply position:fixed + z-index overlay styling
            _element.SetAttribute("data-top-layer", "modal");
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                "data-top-layer",
                null,
                "modal"
            ));
            return FenValue.Undefined;
        }

        private FenValue CloseDialog(FenValue[] args, FenValue thisVal)
        {
             if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "close"))
                throw new FenSecurityError("DOM write permission required");

            _element.RemoveAttribute("open");
            _element.RemoveAttribute("data-top-layer");
             // Enqueue mutation (Deferred)
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                "open",
                "",
                null
            ));
            return FenValue.Undefined;
        }

        private FenValue AttachShadow(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsObject) return FenValue.Null; // Needs {mode: 'open'|'closed'}
            
            var options = args[0].AsObject() as FenObject;
            var modeVal = options?.Get("mode");
            var mode = modeVal?.ToString() ?? "closed";
            
            var init = new ShadowRootInit
            {
                Mode = mode == "open" ? ShadowRootMode.Open : ShadowRootMode.Closed,
                DelegatesFocus = false,
                SlotAssignment = SlotAssignmentMode.Named
            };
            
            if (options != null)
            {
                var delegatesFocus = options.Get("delegatesFocus");
                if (delegatesFocus != null && delegatesFocus.IsBoolean)
                    init.DelegatesFocus = delegatesFocus.ToBoolean();
                    
                var slotAssignment = options.Get("slotAssignment");
                if (slotAssignment != null && slotAssignment.ToString() == "manual")
                    init.SlotAssignment = SlotAssignmentMode.Manual;
            }

            var shadow = _element.AttachShadow(init);
            return FenValue.FromObject(new ShadowRootWrapper(shadow, _context)); 
        }

        // --- Event Listeners Reuse ---
        private FenValue AddEventListenerMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length < 2) return FenValue.Undefined;

            var type = args[0].ToString();
            var callback = args[1];

            bool capture = false;
            bool once = false;
            bool passive = false;
            IObject signalObj = null;
            if (args.Length >= 3)
            {
                if (!args[2].IsObject || args[2].IsNull)
                {
                    capture = args[2].ToBoolean();
                }
                else
                {
                    var opts = args[2].AsObject();
                    if (opts != null)
                    {
                        var captureVal = opts.Get("capture", _context);
                        if (captureVal.IsUndefined)
                        {
                            var captureGetter = opts.Get("__get_capture", _context);
                            if (captureGetter.IsFunction)
                            {
                                captureVal = captureGetter.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(opts));
                            }
                        }
                        capture = captureVal.ToBoolean();

                        var onceVal = opts.Get("once", _context);
                        if (onceVal.IsUndefined)
                        {
                            var onceGetter = opts.Get("__get_once", _context);
                            if (onceGetter.IsFunction)
                            {
                                onceVal = onceGetter.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(opts));
                            }
                        }
                        once = onceVal.ToBoolean();

                        var passiveVal = opts.Get("passive", _context);
                        if (passiveVal.IsUndefined)
                        {
                            var passiveGetter = opts.Get("__get_passive", _context);
                            if (passiveGetter.IsFunction)
                            {
                                passiveVal = passiveGetter.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(opts));
                            }
                        }
                        passive = passiveVal.ToBoolean();

                        var sVal = opts.Get("signal", _context);
                        if (sVal.IsObject) signalObj = sVal.AsObject();
                    }
                }
            }

            var callbackIsValid = callback.IsFunction || callback.IsObject;
            if (type == null || callbackIsValid == false)
                return FenValue.Undefined;
            if (callback.IsNull || callback.IsUndefined)
                return FenValue.Undefined;

            // If signal is already aborted, do not add the listener (per spec)
            if (signalObj != null && signalObj.Get("aborted", _context).ToBoolean())
                return FenValue.Undefined;

            EventTarget.Registry.Add(_element, type, callback, capture, once, passive);

            // Wire AbortSignal: when signal fires "abort", auto-remove this listener
            if (signalObj != null)
            {
                var capturedElement = _element;
                var capturedType = type;
                var capturedCallback = callback;
                var capturedCapture = capture;
                var addAbortListener = signalObj.Get("addEventListener", _context);
                if (addAbortListener.IsFunction)
                {
                    addAbortListener.AsFunction()?.Invoke(new FenValue[]
                    {
                        FenValue.FromString("abort"),
                        FenValue.FromFunction(new FenBrowser.FenEngine.Core.FenFunction("_signalAbortRemove",
                            (abortArgs, abortThis) =>
                            {
                                EventTarget.Registry.Remove(capturedElement, capturedType,
                                    capturedCallback, capturedCapture);
                                return FenValue.Undefined;
                            }))
                    }, _context);
                }
            }

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
                    var opts = args[2].AsObject();
                    if (opts != null)
                    {
                        var captureVal = opts.Get("capture", _context);
                        if (captureVal.IsUndefined)
                        {
                            var captureGetter = opts.Get("__get_capture", _context);
                            if (captureGetter.IsFunction)
                            {
                                captureVal = captureGetter.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(opts));
                            }
                        }
                        capture = captureVal.ToBoolean();
                    }
                }
            }

            EventTarget.Registry.Remove(_element, type, callback, capture);
            return FenValue.Undefined;
        }

        private FenValue DispatchEventMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length == 0 || !args[0].IsObject)
            {
                throw new Exception("TypeError: Failed to execute 'dispatchEvent': parameter 1 is not of type 'Event'.");
            }

            var originalEventValue = args[0];
            var eventObj = args[0].AsObject() as DomEvent;
            if (eventObj == null)
            {
                var evtLike = args[0].AsObject() as FenObject;
                if (evtLike == null) return FenValue.FromBoolean(false);

                var typeVal = evtLike.Get("type");
                var type = !typeVal.IsUndefined ? typeVal.ToString() : string.Empty;
                var bubbles = evtLike.Get("bubbles").ToBoolean();
                var cancelable = evtLike.Get("cancelable").ToBoolean();
                var composed = evtLike.Get("composed").ToBoolean();
                eventObj = new DomEvent(type, bubbles, cancelable, composed, _context);
            }

            var shouldRunMouseActivation =
                string.Equals(eventObj.Type, "click", StringComparison.OrdinalIgnoreCase) &&
                IsMouseClickEvent(originalEventValue);

            var hadCheckablePreActivation = false;
            var previouslyChecked = false;
            if (shouldRunMouseActivation && TryGetCheckableInputType(_element, out var checkableType))
            {
                previouslyChecked = _element.HasAttribute("checked");
                ApplyCheckablePreActivation(_element, checkableType);
                hadCheckablePreActivation = _element.HasAttribute("checked") != previouslyChecked;
            }

            var notPrevented = EventTarget.DispatchEvent(_element, eventObj, _context);

            // Legacy fallback for runtimes that do not wire EventTarget top-level dispatch.
            if (eventObj.Bubbles && EventTarget.ExternalListenerInvoker == null)
            {
                var windowVal = _context?.Environment?.Get("window") ?? FenValue.Undefined;
                if (windowVal.IsObject)
                {
                    var windowDispatch = windowVal.AsObject().Get("dispatchEvent");
                    if (windowDispatch.IsFunction)
                    {
                        var windowResult = windowDispatch.AsFunction().Invoke(
                            new[] { FenValue.FromObject(eventObj) },
                            _context,
                            windowVal);
                        if (windowResult.IsBoolean)
                        {
                            notPrevented = notPrevented && windowResult.ToBoolean();
                        }
                    }
                }
            }

            if (shouldRunMouseActivation)
            {
                if (!notPrevented && hadCheckablePreActivation)
                {
                    RestoreCheckableState(_element, previouslyChecked);
                }
                else
                {
                    if (hadCheckablePreActivation && _element.IsConnected)
                    {
                        DispatchInputAndChangeEvents(_element);
                    }

                    if (notPrevented)
                    {
                        RunSubmitActivation(_element);
                    }
                }
            }

            return FenValue.FromBoolean(notPrevented);
        }
        private FenValue FocusMethod(FenValue[] args, FenValue thisVal)
        {
            if (!IsPotentiallyFocusable(_element))
            {
                return FenValue.Undefined;
            }

            var ownerDocument = _element.OwnerDocument;
            if (ownerDocument != null)
            {
                var previous = ownerDocument.ActiveElement;
                if (previous != null && !ReferenceEquals(previous, _element))
                {
                    var blurEvent = new DomEvent("blur", bubbles: false, cancelable: false, composed: true, context: _context);
                    EventTarget.DispatchEvent(previous, blurEvent, _context);
                }

                ownerDocument.ActiveElement = _element;
            }

            var focusEvent = new DomEvent("focus", bubbles: false, cancelable: false, composed: true, context: _context);
            EventTarget.DispatchEvent(_element, focusEvent, _context);
            return FenValue.Undefined;
        }

        private FenValue BlurMethod(FenValue[] args, FenValue thisVal)
        {
            var ownerDocument = _element.OwnerDocument;
            if (ownerDocument != null && ReferenceEquals(ownerDocument.ActiveElement, _element))
            {
                ownerDocument.ActiveElement = null;
            }

            var blurEvent = new DomEvent("blur", bubbles: false, cancelable: false, composed: true, context: _context);
            EventTarget.DispatchEvent(_element, blurEvent, _context);
            return FenValue.Undefined;
        }

        private FenValue ClickMethod(FenValue[] args, FenValue thisVal)
        {
            if (_element == null)
            {
                return FenValue.Undefined;
            }

            if (IsUserInteractionDisabledControl(_element))
            {
                return FenValue.Undefined;
            }

            var hadCheckablePreActivation = false;
            var previouslyChecked = false;
            if (TryGetCheckableInputType(_element, out var checkableType))
            {
                previouslyChecked = _element.HasAttribute("checked");
                ApplyCheckablePreActivation(_element, checkableType);
                hadCheckablePreActivation = _element.HasAttribute("checked") != previouslyChecked;
            }

            var clickEvent = new DomEvent("click", bubbles: true, cancelable: true, composed: true, context: _context);
            var notPrevented = EventTarget.DispatchEvent(_element, clickEvent, _context);

            if (!notPrevented)
            {
                if (hadCheckablePreActivation)
                {
                    RestoreCheckableState(_element, previouslyChecked);
                }
                return FenValue.Undefined;
            }

            if (hadCheckablePreActivation && _element.IsConnected)
            {
                DispatchInputAndChangeEvents(_element);
            }

            RunSubmitActivation(_element);
            return FenValue.Undefined;
        }

        private bool IsMouseClickEvent(FenValue eventValue)
        {
            if (!eventValue.IsObject)
            {
                return false;
            }

            var current = eventValue.AsObject();
            while (current != null)
            {
                var ctorVal = current.Get("constructor", _context);
                if (ctorVal.IsFunction && ctorVal.AsFunction() is FenFunction ctorFn)
                {
                    var ctorName = ctorFn.Name ?? string.Empty;
                    if (string.Equals(ctorName, "MouseEvent", StringComparison.Ordinal) ||
                        string.Equals(ctorName, "PointerEvent", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (string.Equals(ctorName, "Event", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                current = current.GetPrototype();
            }

            return false;
        }

        private static bool TryGetCheckableInputType(Element element, out string type)
        {
            type = string.Empty;
            if (!string.Equals(element?.TagName, "input", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var inputType = (element.GetAttribute("type") ?? string.Empty).ToLowerInvariant();
            if (inputType != "checkbox" && inputType != "radio")
            {
                return false;
            }

            type = inputType;
            return true;
        }

        private static void ApplyCheckablePreActivation(Element element, string inputType)
        {
            var wasChecked = element.HasAttribute("checked");
            if (inputType == "checkbox")
            {
                if (wasChecked) element.RemoveAttribute("checked");
                else element.SetAttribute("checked", "");
            }
            else if (inputType == "radio" && !wasChecked)
            {
                element.SetAttribute("checked", "");
            }
        }

        private static void RestoreCheckableState(Element element, bool shouldBeChecked)
        {
            if (shouldBeChecked) element.SetAttribute("checked", "");
            else element.RemoveAttribute("checked");
        }

        private void DispatchInputAndChangeEvents(Element element)
        {
            var inputEvt = new DomEvent("input", bubbles: true, cancelable: false, composed: true, context: _context);
            EventTarget.DispatchEvent(element, inputEvt, _context);

            var changeEvt = new DomEvent("change", bubbles: true, cancelable: false, composed: false, context: _context);
            EventTarget.DispatchEvent(element, changeEvt, _context);
        }

        private static bool IsUserInteractionDisabledControl(Element element)
        {
            if (element == null || !element.HasAttribute("disabled"))
            {
                return false;
            }

            var tag = element.TagName?.ToLowerInvariant() ?? string.Empty;
            return tag == "input" || tag == "button" || tag == "select" || tag == "textarea" || tag == "option" || tag == "optgroup";
        }

        private void RunSubmitActivation(Element control)
        {
            if (control == null || !control.IsConnected || IsUserInteractionDisabledControl(control))
            {
                return;
            }

            var tag = control.TagName?.ToLowerInvariant() ?? string.Empty;
            var type = (control.GetAttribute("type") ?? string.Empty).ToLowerInvariant();

            var isSubmit = false;
            if (tag == "button")
            {
                isSubmit = string.IsNullOrEmpty(type) || type == "submit";
            }
            else if (tag == "input")
            {
                isSubmit = type == "submit" || type == "image";
            }

            if (!isSubmit)
            {
                return;
            }

            var current = control.ParentNode;
            while (current != null)
            {
                if (current is Element formElement && string.Equals(formElement.TagName, "form", StringComparison.OrdinalIgnoreCase))
                {
                    if (!formElement.IsConnected)
                    {
                        return;
                    }

                    var submitEvent = new DomEvent("submit", bubbles: true, cancelable: true, composed: false, context: _context);
                    EventTarget.DispatchEvent(formElement, submitEvent, _context);
                    return;
                }

                current = current.ParentNode;
            }
        }
        private static bool IsPotentiallyFocusable(Element element)
        {
            if (element == null) return false;
            if (element.HasAttribute("disabled")) return false;
            if (element.GetAttribute("tabindex") != null) return true;

            var tag = element.TagName?.ToLowerInvariant() ?? string.Empty;
            switch (tag)
            {
                case "input":
                case "button":
                case "select":
                case "textarea":
                    return true;
                case "a":
                    return element.GetAttribute("href") != null;
                default:
                    return false;
            }
        }

        private FenValue CloneNodeMethod(FenValue[] args, FenValue thisVal)
        {
            bool deep = false;
            if (args.Length > 0 && args[0].IsBoolean) deep = args[0].ToBoolean();
            
            var clone = _element.CloneNode(deep) as Element;
            return FenValue.FromObject(new ElementWrapper(clone, _context));
        }
        
        /// <summary>
        /// Create an array-like FenObject from a list of FenValue results
        /// </summary>
        private FenValue CreateArrayFromResults(List<FenValue> results)
        {
            var arr = new FenObject();
            for (int i = 0; i < results.Count; i++)
            {
                arr.Set(i.ToString(), results[i]);
            }
            arr.Set("length", FenValue.FromNumber(results.Count));
            return FenValue.FromObject(arr);
        }

        /// <summary>
        /// Create an empty array-like FenObject
        /// </summary>
        private FenValue CreateEmptyArray()
        {
            var arr = new FenObject();
            arr.Set("length", FenValue.FromNumber(0));
            return FenValue.FromObject(arr);
        }

        private FenValue GetBoundingClientRectMethod(FenValue[] args, FenValue thisVal)
        {
            if (_context == null)
            {
                return FenValue.FromObject(new DOMRectReadOnly(0, 0, 0, 0));
            }

            var engine = _context.GetLayoutEngine();
            if (engine != null)
            {
                var box = engine.GetBoxForNode(_element);
                if (box.HasValue)
                {
                    return FenValue.FromObject(new DOMRectReadOnly(box.Value.Left, box.Value.Top, box.Value.Width, box.Value.Height));
                }
            }
            
            return FenValue.FromObject(new DOMRectReadOnly(0, 0, 0, 0));
        }

        private FenValue GetClientRectsMethod(FenValue[] args, FenValue thisVal)
        {
            var arr = new FenObject();
            var rect = GetBoundingClientRectMethod(args, thisVal);
            
            // For now, getClientRects just returns an array containing the single bounding rect.
            // Inline elements that wrap across lines might have multiple rects in reality.
            var rectObj = rect.AsObject();
            if (rectObj != null && rectObj.Get("width").ToNumber() > 0)
            {
                arr.Set("0", rect);
                arr.Set("length", FenValue.FromNumber(1));
            }
            else
            {
                arr.Set("length", FenValue.FromNumber(0));
            }
            
            return FenValue.FromObject(arr);
        }
    }

    /// <summary>
    /// Implements DOMRectReadOnly interface for getBoundingClientRect
    /// </summary>
    public class DOMRectReadOnly : IObject
    {
        private readonly double _x;
        private readonly double _y;
        private readonly double _width;
        private readonly double _height;
        private IObject _prototype;

        public object NativeObject { get; set; }

        public DOMRectReadOnly(double x, double y, double width, double height)
        {
            _x = x;
            _y = y;
            _width = width;
            _height = height;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            switch (key.ToLowerInvariant())
            {
                case "x": return FenValue.FromNumber(_x);
                case "y": return FenValue.FromNumber(_y);
                case "width": return FenValue.FromNumber(_width);
                case "height": return FenValue.FromNumber(_height);
                case "top": return FenValue.FromNumber(_y);
                case "right": return FenValue.FromNumber(_x + _width);
                case "bottom": return FenValue.FromNumber(_y + _height);
                case "left": return FenValue.FromNumber(_x);
                case "tojson": return FenValue.FromFunction(new FenFunction("toJSON", ToJSONMethod));
                default: return FenValue.Undefined;
            }
        }

        private FenValue ToJSONMethod(FenValue[] args, FenValue thisVal)
        {
            var obj = new FenObject();
            obj.Set("x", FenValue.FromNumber(_x));
            obj.Set("y", FenValue.FromNumber(_y));
            obj.Set("width", FenValue.FromNumber(_width));
            obj.Set("height", FenValue.FromNumber(_height));
            obj.Set("top", FenValue.FromNumber(_y));
            obj.Set("right", FenValue.FromNumber(_x + _width));
            obj.Set("bottom", FenValue.FromNumber(_y + _height));
            obj.Set("left", FenValue.FromNumber(_x));
            return FenValue.FromObject(obj);
        }

        public void Set(string key, FenValue value, IExecutionContext context = null) { } // ReadOnly
        public bool Has(string key, IExecutionContext context = null) => !Get(key, context).IsUndefined;
        public bool Delete(string key, IExecutionContext context = null) => false;
        public IEnumerable<string> Keys(IExecutionContext context = null) => new[] { "x", "y", "width", "height", "top", "right", "bottom", "left", "toJSON" };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;
    }

    public class DOMTokenList : IObject
    {
        private readonly Element _element;
        private readonly string _attrName;
        private readonly IExecutionContext _context;
        private IObject _prototype;
        public object NativeObject { get; set; }

        public DOMTokenList(Element element, string attrName, IExecutionContext context)
        {
            _element = element;
            _attrName = attrName;
            _context = context;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            var val = _element.GetAttribute(_attrName) ?? "";
            var tokens = new List<string>(val.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

            switch (key.ToLowerInvariant())
            {
                case "add": return FenValue.FromFunction(new FenFunction("add", (args, _) => Modify(tokens, args, (t, list) => { if (!list.Contains(t)) list.Add(t); })));
                case "remove": return FenValue.FromFunction(new FenFunction("remove", (args, _) => Modify(tokens, args, (t, list) => list.Remove(t))));
                case "toggle": return FenValue.FromFunction(new FenFunction("toggle", (args, _) => Toggle(tokens, args)));
                case "contains": return FenValue.FromFunction(new FenFunction("contains", (args, _) => FenValue.FromBoolean(args.Length > 0 && tokens.Contains(args[0].ToString()))));
                case "length": return FenValue.FromNumber(tokens.Count);
                default: 
                    if (int.TryParse(key, out int index) && index >= 0 && index < tokens.Count)
                        return FenValue.FromString(tokens[index]);
                    return FenValue.Undefined;
            }
        }

        private FenValue Modify(List<string> tokens, FenValue[] args, Action<string, List<string>> action)
        {
            foreach (var arg in args) action(arg.ToString(), tokens);
            Update(tokens);
            return FenValue.Undefined;
        }

        private FenValue Toggle(List<string> tokens, FenValue[] args)
        {
             if (args.Length == 0) return FenValue.FromBoolean(false);
             var token = args[0].ToString();
             bool has = tokens.Contains(token);
             if (has) tokens.Remove(token);
             else tokens.Add(token);
             Update(tokens);
             return FenValue.FromBoolean(!has);
        }

        private void Update(List<string> tokens)
        {
            _element.SetAttribute(_attrName, string.Join(" ", tokens));
            _context?.RequestRender?.Invoke();
        }

        public void Set(string key, FenValue value, IExecutionContext context = null) { }
        public bool Has(string key, IExecutionContext context = null) => !Get(key, context).IsUndefined;
        public bool Delete(string key, IExecutionContext context = null) => false;
        public IEnumerable<string> Keys(IExecutionContext context = null) => new string[0];
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;
    }

    public class DOMStringMap : IObject
    {
        private readonly Element _element;
        private readonly IExecutionContext _context;
        private IObject _prototype;
        public object NativeObject { get; set; }

        public DOMStringMap(Element element, IExecutionContext context)
        {
            _element = element;
            _context = context;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            var attrName = "data-" + PropertyNameToAttributeSuffix(key);
            var val = _element.GetAttribute(attrName);
            if (val != null)
                return FenValue.FromString(val);
            return FenValue.Undefined;
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            var attrName = "data-" + PropertyNameToAttributeSuffix(key);
            _element.SetAttribute(attrName, value.ToString());
            _context?.RequestRender?.Invoke();
        }

        public bool Has(string key, IExecutionContext context = null)
        {
            return EnumerateSupportedPropertyNames().Contains(key, StringComparer.Ordinal);
        }

        public bool Delete(string key, IExecutionContext context = null)
        {
            var attrName = "data-" + PropertyNameToAttributeSuffix(key);
            if (_element.HasAttribute(attrName))
            {
                _element.RemoveAttribute(attrName);
                _context?.RequestRender?.Invoke();
            }
            return true;
        }

        public IEnumerable<string> Keys(IExecutionContext context = null) => EnumerateSupportedPropertyNames();

        public IEnumerable<string> GetOwnPropertyNames(IExecutionContext context = null) => EnumerateSupportedPropertyNames();

        public PropertyDescriptor? GetOwnPropertyDescriptor(string key)
        {
            if (!Has(key))
            {
                return null;
            }

            return new PropertyDescriptor
            {
                Value = Get(key),
                Writable = true,
                Enumerable = true,
                Configurable = true,
                Getter = null,
                Setter = null
            };
        }

        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;

        private IEnumerable<string> EnumerateSupportedPropertyNames()
        {
            var yielded = new HashSet<string>(StringComparer.Ordinal);
            var attrs = _element.Attributes;
            for (int i = 0; i < attrs.Length; i++)
            {
                var attr = attrs[i];
                if (attr == null || string.IsNullOrEmpty(attr.Name))
                {
                    continue;
                }

                if (!attr.Name.StartsWith("data-", StringComparison.Ordinal))
                {
                    continue;
                }

                var suffix = attr.Name.Substring(5);
                var propName = AttributeSuffixToPropertyName(suffix);
                if (yielded.Add(propName))
                {
                    yield return propName;
                }
            }
        }

        private static string AttributeSuffixToPropertyName(string suffix)
        {
            if (suffix == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(suffix.Length);
            for (int i = 0; i < suffix.Length; i++)
            {
                var c = suffix[i];
                if (c == '-' && i + 1 < suffix.Length && suffix[i + 1] >= 'a' && suffix[i + 1] <= 'z')
                {
                    sb.Append(char.ToUpperInvariant(suffix[i + 1]));
                    i++;
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static string PropertyNameToAttributeSuffix(string propertyName)
        {
            if (propertyName == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(propertyName.Length * 2);
            for (int i = 0; i < propertyName.Length; i++)
            {
                var c = propertyName[i];
                if (c >= 'A' && c <= 'Z')
                {
                    sb.Append('-');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }
    public class CSSStyleDeclaration : IObject
    {
        private readonly Element _element;
        private readonly IExecutionContext _context;
        private IObject _prototype;
        public object NativeObject { get; set; }

        public CSSStyleDeclaration(Element element, IExecutionContext context)
        {
            _element = element;
            _context = context;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            // Get style property from element attributes (style="key:value")
            // Simplified: parsing style attribute every time is slow but works for now
            var styleStr = _element.GetAttribute("style") ?? "";
            var styles = ParseStyle(styleStr);
            
            if (string.Equals(key, "setProperty", StringComparison.OrdinalIgnoreCase))
                return FenValue.FromFunction(new FenFunction("setProperty", SetProperty));
            if (string.Equals(key, "getPropertyValue", StringComparison.OrdinalIgnoreCase))
                return FenValue.FromFunction(new FenFunction("getPropertyValue", GetPropertyValue));
            if (string.Equals(key, "removeProperty", StringComparison.OrdinalIgnoreCase))
                return FenValue.FromFunction(new FenFunction("removeProperty", RemoveProperty));
                
            var cssKey = CamelToKebab(key);
            return styles.ContainsKey(cssKey) ? FenValue.FromString(styles[cssKey]) : FenValue.Undefined;
        }

        private string CamelToKebab(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // Handle --custom-properties as-is
            if (s.StartsWith("--")) return s;
            return System.Text.RegularExpressions.Regex.Replace(s, "(?<!^)([A-Z])", "-$1").ToLower();
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
             if (_context != null)
            {
                _context.CheckExecutionTimeLimit();
            }

            var styleStr = _element.GetAttribute("style") ?? "";
            var styles = ParseStyle(styleStr);
            var cssKey = CamelToKebab(key);
            styles[cssKey] = value.ToString();
            
            // Rebuild style string
            var sb = new StringBuilder();
            foreach (var kvp in styles)
            {
                sb.Append($"{kvp.Key}:{kvp.Value};");
            }
            
            if (_element != null)
            {
                _element.SetAttribute("style", sb.ToString());
            }

            FenLogger.Debug($"[CSS] Set style {key}={value}", LogCategory.CSS);
            _context.RequestRender?.Invoke();
        }

        private FenValue SetProperty(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2) return FenValue.Undefined;
            Set(args[0].ToString(), args[1]);
            return FenValue.Undefined;
        }

        private FenValue GetPropertyValue(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.FromString("");
            var val = Get(args[0].ToString());
            return val.IsUndefined ? FenValue.FromString("") : val;
        }

        private FenValue RemoveProperty(FenValue[] args, FenValue thisVal)
        {
             if (args.Length == 0) return FenValue.FromString("");
             var key = args[0].ToString();
             
            var styleStr = _element.GetAttribute("style") ?? "";
            var styles = ParseStyle(styleStr);
            if (styles.ContainsKey(key))
            {
                var val = styles[key];
                styles.Remove(key);
                
                // Rebuild
                var sb = new StringBuilder();
                foreach (var kvp in styles) sb.Append($"{kvp.Key}:{kvp.Value};");
                if (_element != null) _element.SetAttribute("style", sb.ToString());
                
                 _context?.RequestRender?.Invoke();
                 return FenValue.FromString(val);
            }
            return FenValue.FromString("");
        }

        public bool Has(string key, IExecutionContext context = null) => !Get(key, context).IsUndefined;
        public bool Delete(string key, IExecutionContext context = null) => false;
        public IEnumerable<string> Keys(IExecutionContext context = null)
        {
            var styleStr = _element.GetAttribute("style") ?? "";
            var styles = ParseStyle(styleStr);
            return styles.Keys;
        }
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;

        private Dictionary<string, string> ParseStyle(string style)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(style)) return dict;

            foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (kv.Length == 2)
                {
                    dict[kv[0].Trim()] = kv[1].Trim();
                }
            }
            return dict;
        }
    }
}





























