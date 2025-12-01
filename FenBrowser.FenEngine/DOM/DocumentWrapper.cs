using System;
using System.Linq;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Errors;

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

                case "body":
                    var body = FindElementByTag(_root, "body");
                    return body != null ? FenValue.FromObject(new ElementWrapper(body, _context)) : FenValue.Null;

                case "head":
                    var head = FindElementByTag(_root, "head");
                    return head != null ? FenValue.FromObject(new ElementWrapper(head, _context)) : FenValue.Null;

                case "title":
                    var titleEl = FindElementByTag(_root, "title");
                    return FenValue.FromString(titleEl?.Text ?? "");

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
            => new[] { "getElementById", "querySelector", "body", "head", "title" };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        private IValue GetElementById(IValue[] args)
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

        private IValue QuerySelector(IValue[] args)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomRead, "querySelector"))
                throw new FenSecurityError("DOM read permission required");

            if (args.Length == 0) return FenValue.Null;

            var selector = args[0].ToString();
            
            // Simple implementation: only support ID selectors for now
            if (selector.StartsWith("#"))
            {
                var id = selector.Substring(1);
                var element = FindElementById(_root, id);
                return element != null 
                    ? FenValue.FromObject(new ElementWrapper(element, _context))
                    : FenValue.Null;
            }

            return FenValue.Null;
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
