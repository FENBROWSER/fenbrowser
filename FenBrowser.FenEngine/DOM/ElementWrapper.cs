using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core;
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
            => new[] { "innerHTML", "textContent", "tagName", "id", "getAttribute", "setAttribute" };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

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
            try { System.IO.File.AppendAllText("debug_log.txt", $"[ElementWrapper] SetInnerHTML: {value.ToString()}\r\n"); } catch { }
            _element.Children?.Clear();
            var htmlString = value.ToString();
            if (string.IsNullOrEmpty(htmlString)) return;

            try
            {
                var parser = new HtmlLiteParser(htmlString);
                var parsed = parser.Parse();
                if (parsed?.Children != null)
                {
                    foreach (var child in parsed.Children)
                        _element.Append(child);
                }
            }
            catch
            {
                _element.Append(new LiteElement("#text") { Text = htmlString });
            }
            _context.RequestRender?.Invoke();
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
            try { System.IO.File.AppendAllText("debug_log.txt", $"[ElementWrapper] SetTextContent: {value.ToString()}\r\n"); } catch { }
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

            // Attr is read-only property but mutable dictionary
            // We can add/update keys, but not assign a new dictionary
            // LiteElement constructor ensures Attr is not null
            
            
            _element.Attr[args[0].ToString()] = args[1].ToString();
            _context.RequestRender?.Invoke();
            return FenValue.Undefined;
        }
    }
}
