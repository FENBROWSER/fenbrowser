using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Wraps a DOM Attr object for JavaScript access.
    /// SPEC: DOM Living Standard §4.9
    /// </summary>
    public class AttrWrapper : IObject
    {
        private readonly Attr _attr;
        private readonly IExecutionContext _context;
        private IObject _prototype;
        public object NativeObject { get; set; }

        public AttrWrapper(Attr attr, IExecutionContext context)
        {
            _attr = attr ?? throw new ArgumentNullException(nameof(attr));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            NativeObject = attr;
        }

        public Attr Attr => _attr;

        public IValue Get(string key, IExecutionContext context = null)
        {
            _context?.CheckExecutionTimeLimit();

            switch (key.ToLowerInvariant())
            {
                case "name":
                    return FenValue.FromString(_attr.Name);

                case "value":
                    return FenValue.FromString(_attr.Value);

                case "specified":
                    return FenValue.FromBoolean(_attr.Specified);

                case "ownerelement":
                    return _attr.OwnerElement != null 
                        ? FenValue.FromObject(new ElementWrapper(_attr.OwnerElement, _context)) 
                        : FenValue.Null;

                // Legacy DOM Level 2 stuff often still present or shimmed
                case "nodename":
                    return FenValue.FromString(_attr.Name);
                case "nodevalue":
                    return FenValue.FromString(_attr.Value);
                case "nodetype":
                    return FenValue.FromNumber((int)_attr.NodeType);

                default:
                    return FenValue.Undefined;
            }
        }

        public void Set(string key, IValue value, IExecutionContext context = null)
        {
            _context?.CheckExecutionTimeLimit();

            if (key.ToLowerInvariant() == "value")
            {
                if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "Attr.value"))
                    throw new FenSecurityError("DOM write permission required for Attr.value");
                
                // Just setting property on Attr object doesn't automatically trigger Element updates 
                // unless we go through Element.SetAttribute or we implement observer notification in Attr itself.
                // However, core Attr is a Node, but changing its Value property *should* ideally notify the owner element.
                // Let's check Core.Attr... it's a simple property. 
                // So updating it here might desync if the Element relies on re-setting.
                // But for now, direct update:
                _attr.Value = value.ToString();
                
                // If attached, this should trigger mutation. 
                // But since Core.Attr.Value set doesn't seem to notify OwnerElement in the code I read earlier,
                // we might need to manually trigger update on owner if present.
                // Actually Element.SetAttributeNode logic handles the connection.
                // The correct way in DOM is that changing Attr.value *updates* the attribute.
                if (_attr.OwnerElement != null)
                {
                    // Re-set to trigger side effects/mutations
                    _attr.OwnerElement.SetAttributeNode(_attr);
                }
            }
        }

        public bool Has(string key, IExecutionContext context = null) => !Get(key, context).IsUndefined;
        public bool Delete(string key, IExecutionContext context = null) => false;
        
        public IEnumerable<string> Keys(IExecutionContext context = null) 
            => new[] { "name", "value", "specified", "ownerElement", "nodeName", "nodeValue", "nodeType" };
            
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
    }
}
