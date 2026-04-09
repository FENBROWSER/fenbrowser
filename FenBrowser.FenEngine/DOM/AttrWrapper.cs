using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
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
        private static readonly string[] BuiltInKeys =
        {
            "namespaceURI",
            "prefix",
            "localName",
            "name",
            "value",
            "ownerElement",
            "specified",
            "nodeName",
            "nodeValue",
            "nodeType",
            "textContent"
        };

        private readonly Attr _attr;
        private readonly IExecutionContext _context;
        private readonly Dictionary<string, PropertyDescriptor> _expandos = new(StringComparer.Ordinal);
        private IObject _prototype;
        public object NativeObject { get; set; }

        public AttrWrapper(Attr attr, IExecutionContext context)
        {
            _attr = attr ?? throw new ArgumentNullException(nameof(attr));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            NativeObject = attr;

            var attrCtor = _context.Environment?.Get("Attr") ?? FenValue.Undefined;
            if (attrCtor.IsFunction)
            {
                var prototype = attrCtor.AsFunction()?.Get("prototype", _context) ?? FenValue.Undefined;
                if (prototype.IsObject)
                {
                    _prototype = prototype.AsObject();
                }
            }
        }

        public Attr Attr => _attr;

        public FenValue Get(string key, IExecutionContext context = null)
        {
            _context?.CheckExecutionTimeLimit();

            if (_expandos.TryGetValue(key, out var expando) && expando.Value.HasValue)
                return expando.Value.Value;

            switch (key)
            {
                case "namespaceURI":
                case "namespaceuri":
                    return _attr.NamespaceUri != null ? FenValue.FromString(_attr.NamespaceUri) : FenValue.Null;

                case "prefix":
                    return _attr.Prefix != null ? FenValue.FromString(_attr.Prefix) : FenValue.Null;

                case "localName":
                case "localname":
                    return FenValue.FromString(_attr.LocalName);

                case "name":
                    return FenValue.FromString(_attr.Name);

                case "value":
                    return FenValue.FromString(_attr.Value);

                case "specified":
                    return FenValue.FromBoolean(_attr.Specified);

                case "ownerelement":
                case "ownerElement":
                    return _attr.OwnerElement != null 
                        ? FenValue.FromObject(new ElementWrapper(_attr.OwnerElement, _context)) 
                        : FenValue.Null;

                // Legacy DOM Level 2 stuff often still present or shimmed
                case "nodename":
                    return FenValue.FromString(_attr.Name);
                case "nodevalue":
                    return FenValue.FromString(_attr.Value);
                case "nodetype":
                    return FenValue.FromNumber(2); // ATTRIBUTE_NODE
                case "textcontent":
                case "textContent":
                    return FenValue.FromString(_attr.Value);

                default:
                    return FenValue.Undefined;
            }
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            _context?.CheckExecutionTimeLimit();

            if (IsWritableBuiltIn(key))
            {
                SetAttributeValue(value);
                return;
            }

            if (_expandos.TryGetValue(key, out var desc))
            {
                if (desc.Writable == false)
                    return;

                desc.Value = value;
                if (!desc.Writable.HasValue)
                    desc.Writable = true;
                if (!desc.Enumerable.HasValue)
                    desc.Enumerable = true;
                if (!desc.Configurable.HasValue)
                    desc.Configurable = true;

                _expandos[key] = desc;
                return;
            }

            _expandos[key] = PropertyDescriptor.DataDefault(value);
        }

        public bool Has(string key, IExecutionContext context = null)
            => IsBuiltInKey(key) || _expandos.ContainsKey(key);

        public bool Delete(string key, IExecutionContext context = null)
        {
            if (IsBuiltInKey(key))
                return false;

            if (!_expandos.TryGetValue(key, out var desc))
                return true;

            if (desc.Configurable == false)
                return false;

            return _expandos.Remove(key);
        }

        public IEnumerable<string> Keys(IExecutionContext context = null) 
        {
            foreach (var key in BuiltInKeys)
                yield return key;

            foreach (var key in _expandos.Keys)
                yield return key;
        }
            
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        public bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            if (IsBuiltInKey(key))
            {
                if (!IsWritableBuiltIn(key))
                    return !desc.IsAccessor && !desc.Value.HasValue && !desc.Writable.HasValue &&
                           !desc.Enumerable.HasValue && !desc.Configurable.HasValue;

                if (desc.IsAccessor)
                    return false;

                if (desc.Enumerable.HasValue || desc.Configurable.HasValue)
                    return false;

                if (desc.Writable == false)
                    return false;

                if (desc.Value.HasValue)
                    SetAttributeValue(desc.Value.Value);

                return true;
            }

            if (desc.IsAccessor)
                return false;

            if (_expandos.TryGetValue(key, out var existing) && existing.Configurable == false)
            {
                if (desc.Configurable == true)
                    return false;

                if (desc.Enumerable.HasValue && desc.Enumerable != existing.Enumerable)
                    return false;

                if (existing.Writable == false)
                {
                    if (desc.Writable == true)
                        return false;

                    if (desc.Value.HasValue && (!existing.Value.HasValue || !existing.Value.Value.StrictEquals(desc.Value.Value)))
                        return false;
                }
            }

            _expandos[key] = MergeDescriptor(existing, desc);
            return true;
        }

        private static bool IsBuiltInKey(string key)
        {
            switch (key)
            {
                case "namespaceURI":
                case "namespaceuri":
                case "prefix":
                case "localName":
                case "localname":
                case "name":
                case "value":
                case "ownerElement":
                case "ownerelement":
                case "specified":
                case "nodeName":
                case "nodename":
                case "nodeValue":
                case "nodevalue":
                case "nodeType":
                case "nodetype":
                case "textContent":
                case "textcontent":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsWritableBuiltIn(string key)
        {
            switch (key)
            {
                case "value":
                case "nodeValue":
                case "nodevalue":
                case "textContent":
                case "textcontent":
                    return true;
                default:
                    return false;
            }
        }

        private void SetAttributeValue(FenValue value)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "Attr.value"))
                throw new FenSecurityError("DOM write permission required for Attr.value");

            _attr.Value = value.IsNull || value.IsUndefined ? string.Empty : value.ToString();
        }

        private static PropertyDescriptor MergeDescriptor(PropertyDescriptor existing, PropertyDescriptor update)
        {
            return new PropertyDescriptor
            {
                Value = update.Value ?? existing.Value ?? FenValue.Undefined,
                Writable = update.Writable ?? existing.Writable ?? true,
                Enumerable = update.Enumerable ?? existing.Enumerable ?? true,
                Configurable = update.Configurable ?? existing.Configurable ?? true,
                Getter = null,
                Setter = null
            };
        }
    }
}
