using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Wraps a LiteElement to expose it to JavaScript.
    /// Provides DOM manipulation methods with permission checking.
    /// </summary>
    public partial class ElementWrapper : IObject
    {
        private readonly LiteElement _element;
        private readonly IExecutionContext _context;
        private IObject _prototype;

        public ElementWrapper(LiteElement element, IExecutionContext context)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public IValue Get(string key)
        {
            _context?.CheckExecutionTimeLimit();
            


            switch (key.ToLowerInvariant())
            {
                case "innerhtml":
                    return GetInnerHTML();
                
                case "textcontent":
                    return GetTextContent();
                
                case "tagname":
                    return FenValue.FromString(_element.Tag?.ToUpperInvariant() ?? "");
                
                case "id":
                    return FenValue.FromString(_element.Attr?.ContainsKey("id") == true ? _element.Attr["id"] : "");
                
                case "getattribute":
                    return FenValue.FromFunction(new FenFunction("getAttribute", GetAttribute));
                
                case "setattribute":
                    return FenValue.FromFunction(new FenFunction("setAttribute", SetAttribute));

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

                case "classname":
                    return FenValue.FromString(_element.Attr?.ContainsKey("class") == true ? _element.Attr["class"] : "");

                case "parentelement":
                case "parentnode":
                    if (_element.Parent != null)
                        return FenValue.FromObject(new ElementWrapper(_element.Parent, _context));
                    return FenValue.Null;

                case "children":
                    var childElements = _element.Children?.Where(c => !c.IsText).ToList() ?? new List<LiteElement>();
                    return CreateArrayFromElements(childElements);

                case "firstelementchild":
                    var firstChild = _element.Children?.FirstOrDefault(c => !c.IsText);
                    return firstChild != null ? FenValue.FromObject(new ElementWrapper(firstChild, _context)) : FenValue.Null;

                case "lastelementchild":
                    var lastChild = _element.Children?.LastOrDefault(c => !c.IsText);
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
                    if (_element.Tag?.ToUpperInvariant() == "DIALOG")
                        return FenValue.FromBoolean(_element.Attr?.ContainsKey("open") == true);
                    return FenValue.Undefined;
                
                
                // Shadow DOM
                case "attachshadow":
                    return FenValue.FromFunction(new FenFunction("attachShadow", AttachShadow));
                
                case "shadowroot":
                    if (_element.ShadowRoot != null)
                    {
                        // Return a wrapper for the shadow root
                        var shadowHost = new LiteElement("shadow-root");
                        shadowHost.Children.AddRange(_element.ShadowRoot);
                        return FenValue.FromObject(new ElementWrapper(shadowHost, _context));
                    }
                    return FenValue.Null;
                
                default:

                    return FenValue.Undefined;
            }
        }

        public void Set(string key, IValue value)
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
            }
        }

        public bool Has(string key) => !Get(key).IsUndefined;
        public bool Delete(string key) => false;
        public IEnumerable<string> Keys() 
            => new[] { "innerHTML", "textContent", "tagName", "id", "getAttribute", "setAttribute", "getContext", "width", "height", "clientWidth", "clientHeight" };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        private IValue GetContext(IValue[] args, IValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var type = args[0].ToString();
            
            // Check if element is canvas
            if (string.Equals(_element.Tag, "canvas", StringComparison.OrdinalIgnoreCase) && 
                string.Equals(type, "2d", StringComparison.OrdinalIgnoreCase))
            {
                // We need access to JavaScriptEngine to create context
                // But ElementWrapper only has IExecutionContext.
                // We might need to store the engine or access it via context?
                // Actually, CanvasRenderingContext2D needs JavaScriptEngine for GetVisual.
                // Let's assume we can get it or pass null if not strictly needed for basic drawing?
                // Wait, GetVisual is static in JavaScriptEngine!
                // So we just need to pass null as engine if it's not used for other things.
                
                return FenValue.FromObject(new FenBrowser.FenEngine.Scripting.CanvasRenderingContext2D(_element, null));
            }
            return FenValue.Null;
        }

        private IValue GetInnerHTML()
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomRead, "innerHTML"))
                throw new FenSecurityError("DOM read permission required");

            return FenValue.FromString(CollectInnerHtml(_element));
        }

        private string CollectInnerHtml(LiteElement element)
        {
            if (element.Children == null || element.Children.Count == 0)
                return element.Text ?? "";

            var sb = new StringBuilder();
            foreach (var child in element.Children)
            {
                // Simple reconstruction - in real app would need proper serialization
                if (child.IsText)
                {
                    sb.Append(child.Text);
                }
                else
                {
                    sb.Append($"<{child.Tag}");
                    if (child.Attr != null)
                    {
                        foreach (var kvp in child.Attr)
                        {
                            sb.Append($" {kvp.Key}=\"{kvp.Value}\"");
                        }
                    }
                    sb.Append(">");
                    sb.Append(CollectInnerHtml(child));
                    sb.Append($"</{child.Tag}>");
                }
            }
            return sb.ToString();
        }

        private void SetInnerHTML(IValue value)
        {
            var removed = _element.Children != null ? new System.Collections.Generic.List<LiteElement>(_element.Children) : new System.Collections.Generic.List<LiteElement>();

            _element.Children?.Clear();
            var htmlString = value.ToString();
            
            var added = new System.Collections.Generic.List<LiteElement>();

            if (!string.IsNullOrEmpty(htmlString))
            {
                try
                {
                    var parser = new HtmlLiteParser(htmlString);
                    var parsed = parser.Parse();
                    if (parsed?.Children != null)
                    {
                        foreach (var child in parsed.Children)
                        {
                            _element.Append(child);
                            added.Add(child);
                        }
                    }
                }
                catch
                {
                    var textNode = new LiteElement("#text") { Text = htmlString };
                    _element.Append(textNode);
                    added.Add(textNode);
                }
            }
            _context.RequestRender?.Invoke();

            _context.OnMutation?.Invoke(new FenBrowser.FenEngine.Core.MutationRecord
            {
                Type = "childList",
                Target = _element,
                AddedNodes = added,
                RemovedNodes = removed
            });
        }

        private IValue GetTextContent()
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomRead, "textContent"))
                throw new FenSecurityError("DOM read permission required");

            return FenValue.FromString(CollectText(_element));
        }

        private string CollectText(LiteElement element)
        {
            if (element.IsText) return element.Text ?? "";
            if (element.Children == null) return "";
            
            var sb = new StringBuilder();
            foreach (var child in element.Children)
            {
                sb.Append(CollectText(child));
            }
            return sb.ToString();
        }

        private void SetTextContent(IValue value)
        {

            _element.Children?.Clear();
            var text = value.ToString();
            if (!string.IsNullOrEmpty(text))
                _element.Append(new LiteElement("#text") { Text = text });
            
            _context.RequestRender?.Invoke();
        }

        private IValue GetAttribute(IValue[] args, IValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var attrName = args[0].ToString();
            return _element.Attr != null && _element.Attr.TryGetValue(attrName, out var value)
                ? FenValue.FromString(value)
                : FenValue.Null;
        }

        private IValue SetAttribute(IValue[] args, IValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "setAttribute"))
                throw new FenSecurityError("DOM write permission required");

            if (args.Length < 2) return FenValue.Undefined;

            var name = args[0].ToString();
            var value = args[1].ToString();
            var oldValue = _element.Attr.ContainsKey(name) ? _element.Attr[name] : null;
            
            _element.Attr[name] = value;
            _context.RequestRender?.Invoke();
            
            _context.OnMutation?.Invoke(new FenBrowser.FenEngine.Core.MutationRecord
            {
                Type = "attributes",
                Target = _element,
                AttributeName = name,
                OldValue = oldValue
            });
            
            return FenValue.Undefined;
        }
        private double GetDimension(string attrName)
        {
            if (_element.Attr != null && _element.Attr.TryGetValue(attrName, out var val))
            {
                if (double.TryParse(val, out var d)) return d;
            }
            return 0;
        }

        private double GetClientWidth()
        {
            // For <html> element (documentElement), return viewport width
            if (string.Equals(_element.Tag, "html", StringComparison.OrdinalIgnoreCase))
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
            if (string.Equals(_element.Tag, "html", StringComparison.OrdinalIgnoreCase))
            {
                // Return viewport height (typical desktop height)
                return 1080;
            }
            // For other elements, use height attribute or return 0
            return GetDimension("height");
        }

        private IValue AppendChild(IValue[] args, IValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "appendChild"))
                throw new FenSecurityError("DOM write permission required");

            if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;

            var childWrapper = args[0].AsObject() as ElementWrapper;
            
            if (childWrapper != null)
            {
                _element.Append(childWrapper.Element);
                _context.RequestRender?.Invoke();
                
                _context.OnMutation?.Invoke(new FenBrowser.FenEngine.Core.MutationRecord
                {
                    Type = "childList",
                    Target = _element,
                    AddedNodes = new System.Collections.Generic.List<LiteElement> { childWrapper.Element }
                });
                
                return args[0];
            }
            
            return FenValue.Null;
        }

        /// <summary>
        /// Implements element.matches(selector) - checks if element matches a CSS selector
        /// </summary>
        private IValue MatchesSelector(IValue[] args, IValue thisVal)
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
        private IValue ClosestSelector(IValue[] args, IValue thisVal)
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
                    current = current.Parent;
                }
                
                return FenValue.Null;
            }
            catch
            {
                return FenValue.Null;
            }
        }

        /// <summary>
        /// Implements element.querySelector(selector) - finds first descendant matching selector
        /// </summary>
        private IValue QuerySelector(IValue[] args, IValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsString) return FenValue.Null;
            
            try
            {
                var selector = args[0].ToString();
                var result = FindFirstDescendant(_element, selector);
                return result != null ? FenValue.FromObject(new ElementWrapper(result, _context)) : FenValue.Null;
            }
            catch
            {
                return FenValue.Null;
            }
        }

        /// <summary>
        /// Implements element.querySelectorAll(selector) - finds all descendants matching selector
        /// </summary>
        private IValue QuerySelectorAll(IValue[] args, IValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsString) return CreateEmptyArray();
            
            try
            {
                var selector = args[0].ToString();
                var results = new List<IValue>();
                FindAllDescendants(_element, selector, results);
                return CreateArrayFromResults(results);
            }
            catch
            {
                return CreateEmptyArray();
            }
        }

        private LiteElement FindFirstDescendant(LiteElement parent, string selector)
        {
            if (parent.Children == null) return null;
            
            foreach (var child in parent.Children)
            {
                if (child.IsText) continue;
                
                if (Rendering.CssLoader.MatchesSelector(child, selector))
                    return child;
                
                var result = FindFirstDescendant(child, selector);
                if (result != null) return result;
            }
            
            return null;
        }

        private void FindAllDescendants(LiteElement parent, string selector, List<IValue> results)
        {
            if (parent.Children == null) return;
            
            foreach (var child in parent.Children)
            {
                if (child.IsText) continue;
                
                if (Rendering.CssLoader.MatchesSelector(child, selector))
                    results.Add(FenValue.FromObject(new ElementWrapper(child, _context)));
                
                FindAllDescendants(child, selector, results);
            }
        }

        /// <summary>
        /// Create an array-like FenObject from a list of LiteElements
        /// </summary>
        private IValue CreateArrayFromElements(List<LiteElement> elements)
        {
            var arr = new FenObject();
            for (int i = 0; i < elements.Count; i++)
            {
                arr.Set(i.ToString(), FenValue.FromObject(new ElementWrapper(elements[i], _context)));
            }
            arr.Set("length", FenValue.FromNumber(elements.Count));
            return FenValue.FromObject(arr);
        }

        /// <summary>
        /// Create an array-like FenObject from a list of IValue results
        /// </summary>
        private IValue CreateArrayFromResults(List<IValue> results)
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
        private IValue CreateEmptyArray()
        {
            var arr = new FenObject();
            arr.Set("length", FenValue.FromNumber(0));
            return FenValue.FromObject(arr);
        }

        // Expose underlying element to other wrappers
        internal LiteElement Element => _element;
    }

    public class CSSStyleDeclaration : IObject
    {
        private readonly LiteElement _element;
        private readonly IExecutionContext _context;
        private IObject _prototype;

        public CSSStyleDeclaration(LiteElement element, IExecutionContext context)
        {
            _element = element;
            _context = context;
        }

        public IValue Get(string key)
        {
            // Get style property from element attributes (style="key:value")
            // Simplified: parsing style attribute every time is slow but works for now
            var styleStr = _element.Attr?.ContainsKey("style") == true ? _element.Attr["style"] : "";
            var styles = ParseStyle(styleStr);
            return styles.ContainsKey(key) ? FenValue.FromString(styles[key]) : FenValue.Undefined;
        }

        public void Set(string key, IValue value)
        {
             if (_context != null)
            {
                _context.CheckExecutionTimeLimit();
                // Permission check should be here ideally
            }

            var styleStr = _element.Attr?.ContainsKey("style") == true ? _element.Attr["style"] : "";
            var styles = ParseStyle(styleStr);
            styles[key] = value.ToString();
            
            // Rebuild style string
            var sb = new StringBuilder();
            foreach (var kvp in styles)
            {
                sb.Append($"{kvp.Key}:{kvp.Value};");
            }
            
            if (_element.Attr != null)
            {
                _element.Attr["style"] = sb.ToString();
                // Debug: Log element tag and final style value
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSSStyleDeclaration] Set on <{_element.Tag}> style='{sb}'\r\n"); } catch {}
            }
            else
            {
                // Debug: Attr is null!
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSSStyleDeclaration] FAILED: Attr is null for <{_element.Tag}>\r\n"); } catch {}
            }
            FenLogger.Debug($"[CSS] Set style {key}={value}", LogCategory.CSS);
            _context.RequestRender?.Invoke();
        }

        public bool Has(string key) => !Get(key).IsUndefined;
        public bool Delete(string key) => false;
        public IEnumerable<string> Keys() => new string[0]; // TODO: Implement enumeration
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

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
    
    // ElementWrapper partial class - dialog methods
    public partial class ElementWrapper
    {
        /// <summary>
        /// Show the dialog element (non-modal)
        /// </summary>
        private IValue ShowDialog(IValue[] args, IValue thisValue)
        {
            if (_element.Tag?.ToUpperInvariant() != "DIALOG")
                return FenValue.Undefined;
            
            if (_element.Attr != null)
            {
                _element.Attr["open"] = "";
                _context?.RequestRender?.Invoke();
            }
            return FenValue.Undefined;
        }
        
        /// <summary>
        /// Show the dialog element as a modal (with backdrop)
        /// </summary>
        private IValue ShowModalDialog(IValue[] args, IValue thisValue)
        {
            if (_element.Tag?.ToUpperInvariant() != "DIALOG")
                return FenValue.Undefined;
            
            if (_element.Attr != null)
            {
                _element.Attr["open"] = "";
                _element.Attr["modal"] = ""; // Mark as modal for backdrop rendering
                _context?.RequestRender?.Invoke();
            }
            return FenValue.Undefined;
        }
        
        /// <summary>
        /// Close the dialog element
        /// </summary>
        private IValue CloseDialog(IValue[] args, IValue thisValue)
        {
            if (_element.Tag?.ToUpperInvariant() != "DIALOG")
                return FenValue.Undefined;
            
            _element.Attr?.Remove("open");
            _element.Attr?.Remove("modal");
            _context?.RequestRender?.Invoke();
            return FenValue.Undefined;
        }
        
        /// <summary>
        /// Attach a shadow root to this element (Shadow DOM)
        /// </summary>
        private IValue AttachShadow(IValue[] args, IValue thisValue)
        {
            // Check if element can have shadow root
            string tag = _element.Tag?.ToUpperInvariant();
            var validTags = new[] { "ARTICLE", "ASIDE", "BLOCKQUOTE", "BODY", "DIV", "FOOTER", 
                "H1", "H2", "H3", "H4", "H5", "H6", "HEADER", "MAIN", "NAV", "P", "SECTION", "SPAN" };
            
            bool isValid = Array.Exists(validTags, t => t == tag);
            if (!isValid)
            {
                throw new Errors.FenInternalError($"Failed to execute 'attachShadow': {tag} is not a valid element for shadow DOM");
            }
            
            if (_element.ShadowRoot != null)
            {
                throw new Errors.FenInternalError("Failed to execute 'attachShadow': Shadow root already attached");
            }
            
            // Parse options (mode: 'open' or 'closed')
            string mode = "open";
            if (args.Length >= 1 && args[0] is FenValue optVal && optVal.AsObject() is FenObject optObj)
            {
                var modeVal = optObj.Get("mode");
                if (modeVal is FenValue mv)
                {
                    mode = mv.AsString()?.ToLowerInvariant() ?? "open";
                }
            }
            
            // Create shadow root
            _element.ShadowRoot = new List<LiteElement>();
            
            // Return the shadow root wrapper (for open mode)
            if (mode == "open")
            {
                var shadowHost = new LiteElement("shadow-root");
                return FenValue.FromObject(new ElementWrapper(shadowHost, _context));
            }
            
            return FenValue.Null;
        }
    }
}
