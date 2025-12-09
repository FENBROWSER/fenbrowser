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
    public class ElementWrapper : IObject
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
}
