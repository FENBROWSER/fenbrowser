using FenBrowser.Core.Dom;
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
        private readonly Element _root;
        private readonly IExecutionContext _context;
        private readonly Uri _baseUri;
        private IObject _prototype;
        private string _readyState = "complete"; // Default, managed by SetReadyState
        public object NativeObject { get; set; }

        public DocumentWrapper(Element root, IExecutionContext context, Uri baseUri = null)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _baseUri = baseUri;
        }

        public IValue Get(string key, IExecutionContext context = null)
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
                         if (!string.IsNullOrEmpty(title)) { var t = new Element("title") { Text = title }; head.Append(t); }
                         newLite.Append(head);
                         newLite.Append(new Element("body"));
                         return FenValue.FromObject(new DocumentWrapper(newLite, _context));
                    })));
                    return FenValue.FromObject(impl);

                default:
                    return FenValue.Undefined;
            }
        }

        public void Set(string key, IValue value, IExecutionContext context = null)
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

        private IValue CreateElement(IValue[] args, IValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var tagName = args[0].ToString();
            
            var element = new Element(tagName);
            return FenValue.FromObject(new ElementWrapper(element, _context));
        }

        private IValue GetElementById(IValue[] args, IValue thisVal)
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
            var frag = new Element("#document-fragment");
            return FenValue.FromObject(new ElementWrapper(frag, _context));
        }

        private IValue CreateTextNode(IValue[] args, IValue thisVal)
        {
            var text = args.Length > 0 ? args[0].ToString() : "";
            var node = new Element("#text") { Text = text };
            return FenValue.FromObject(new ElementWrapper(node, _context));
        }

        private IValue CreateComment(IValue[] args, IValue thisVal)
        {
            var text = args.Length > 0 ? args[0].ToString() : "";
            var node = new Element("#comment") { Text = text };
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
            var results = new List<Element>();
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
            var results = new List<Element>();
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
            var results = new List<Element>();
            RecursiveTagName(_root, tagName, results);
            
            var list = new FenObject();
            for(int i=0; i<results.Count; i++) list.Set(i.ToString(), FenValue.FromObject(new ElementWrapper(results[i], _context)));
            list.Set("length", FenValue.FromNumber(results.Count));
            return FenValue.FromObject(list);
        }

        private Element FindFirstSelector(Element el, string selector)
        {
            if (CssLoader.MatchesSelector(el, selector)) return el;
            if (el.Children != null) {
                foreach(var c in el.Children.OfType<Element>()) {
                    var f = FindFirstSelector(c, selector);
                    if (f != null) return f;
                }
            }
            return null;
        }

        private void RecursiveQuerySelector(Element el, string selector, List<Element> results)
        {
            if (CssLoader.MatchesSelector(el, selector)) results.Add(el);
            if (el.Children != null) foreach(var c in el.Children.OfType<Element>()) RecursiveQuerySelector(c, selector, results);
        }

        private void RecursiveClassName(Element el, string[] classes, List<Element> results)
        {
            var elClasses = el.Classes;
            bool match = true;
            foreach (var cls in classes) {
                if (!elClasses.Contains(cls, StringComparer.Ordinal)) { match = false; break; }
            }
            if (match && classes.Length > 0) results.Add(el);
            
            if (el.Children != null) foreach(var c in el.Children.OfType<Element>()) RecursiveClassName(c, classes, results);
        }

        private void RecursiveTagName(Element el, string tagName, List<Element> results)
        {
            if (string.Equals(el.Tag, tagName, StringComparison.OrdinalIgnoreCase) || tagName == "*") results.Add(el);
            if (el.Children != null) foreach(var c in el.Children.OfType<Element>()) RecursiveTagName(c, tagName, results);
        }

        private Element FindElementById(Element element, string id)
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
                foreach (var child in element.Children.OfType<Element>())
                {
                    var found = FindElementById(child, id);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private Element FindElementByTag(Element element, string tagName)
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
                foreach (var child in element.Children.OfType<Element>())
                {
                    var found = FindElementByTag(child, tagName);
                    if (found != null) return found;
                }
            }

            return null;
        }

        // --- Event Listener Implementation ---

        private IValue AddEventListenerMethod(IValue[] args, IValue thisValue)
        {
            if (args.Length < 2) return FenValue.Undefined;

            var type = args[0].ToString();
            var callback = args[1];

            if (string.IsNullOrEmpty(type) || callback == null || !callback.IsFunction)
                return FenValue.Undefined;
            
            FenLogger.Debug($"[DocumentWrapper] addEventListener called for '{type}'", FenBrowser.Core.Logging.LogCategory.JavaScript);

            // Handle immediate execution for load events if ready
            if ((type == "DOMContentLoaded" || type == "load") && (_readyState == "complete" || _readyState == "interactive"))
            {
                FenLogger.Debug($"[DocumentWrapper] Immediate execution of {type}", FenBrowser.Core.Logging.LogCategory.JavaScript);
                try {
                    var evt = new DomEvent(type);
                    callback.AsFunction().Invoke(new IValue[] { FenValue.FromObject(evt) }, _context);
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
                        capture = opts.Get("capture")?.ToBoolean() ?? false;
                        once = opts.Get("once")?.ToBoolean() ?? false;
                        passive = opts.Get("passive")?.ToBoolean() ?? false;
                    }
                }
            }

            ElementWrapper.EventRegistry.Add(_root, type, callback, capture, once, passive);
            return FenValue.Undefined;
        }

        private IValue RemoveEventListenerMethod(IValue[] args, IValue thisValue)
        {
            if (args.Length < 2) return FenValue.Undefined;
            var type = args[0].ToString();
            var callback = args[1];
            bool capture = false;
            if (args.Length >= 3 && args[2].IsBoolean) capture = args[2].ToBoolean();
            
            ElementWrapper.EventRegistry.Remove(_root, type, callback, capture);
            return FenValue.Undefined;
        }

        private IValue DispatchEventMethod(IValue[] args, IValue thisValue)
        {
            if (args.Length == 0 || !args[0].IsObject) return FenValue.FromBoolean(false);
            
            var eventObj = args[0].AsObject() as DomEvent;
            // Create proper event object if needed
            if (eventObj == null)
            {
                var obj = args[0].AsObject() as FenObject;
                if (obj == null) return FenValue.FromBoolean(false);
                var type = obj.Get("type")?.ToString() ?? "";
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
                 try { l.Callback.AsFunction().Invoke(new IValue[] { FenValue.FromObject(eventObj) }, _context); } catch {}
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
    }
}

