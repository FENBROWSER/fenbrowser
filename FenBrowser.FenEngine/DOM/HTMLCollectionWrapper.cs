using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FenBrowser.FenEngine.DOM
{
    public class HTMLCollectionWrapper : IObject
    {
        private readonly IEnumerable<Element> _source;
        private readonly IExecutionContext _context;
        private readonly Dictionary<string, PropertyDescriptor> _expandos = new(StringComparer.Ordinal);
        private readonly List<string> _expandoOrder = new();

        public HTMLCollectionWrapper(IEnumerable<Element> source, IExecutionContext context)
        {
            _source = source ?? Array.Empty<Element>();
            _context = context;
        }

        public object NativeObject { get; set; }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            if (_expandos.TryGetValue(key, out var expandoDesc))
            {
                if (expandoDesc.IsAccessor)
                {
                    return expandoDesc.Getter != null
                        ? expandoDesc.Getter.Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(this))
                        : FenValue.Undefined;
                }

                return expandoDesc.Value ?? FenValue.Undefined;
            }

            if (TryParseArrayIndex(key, out var arrayIndex))
            {
                var count = (uint)_source.Count();
                if (arrayIndex < count)
                {
                    return Item((int)arrayIndex);
                }

                return FenValue.Undefined;
            }

            var named = NamedItem(key);
            if (!named.IsNull)
            {
                return named;
            }

            switch (key)
            {
                case "length":
                    return FenValue.FromNumber(_source.Count());

                case "item":
                    return FenValue.FromFunction(new FenFunction("item", (args, thisVal) =>
                    {
                        if (args.Length > 0 && args[0].IsNumber)
                        {
                            return Item((int)args[0].ToNumber());
                        }

                        return FenValue.Null;
                    }));

                case "namedItem":
                    return FenValue.FromFunction(new FenFunction("namedItem", (args, thisVal) =>
                    {
                        if (args.Length > 0)
                        {
                            return NamedItem(args[0].ToString());
                        }

                        return FenValue.Null;
                    }));

                case "[Symbol.iterator]":
                case "Symbol.iterator":
                    return FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) =>
                    {
                        var iterator = new FenObject();
                        int i = 0;
                        iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (nextArgs, nextThis) =>
                        {
                            var result = new FenObject();
                            if (i >= _source.Count())
                            {
                                result.Set("done", FenValue.FromBoolean(true));
                                result.Set("value", FenValue.Undefined);
                            }
                            else
                            {
                                result.Set("done", FenValue.FromBoolean(false));
                                result.Set("value", Item(i));
                                i++;
                            }

                            return FenValue.FromObject(result);
                        })));
                        iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (itArgs, itThis) => itThis)));
                        iterator.Set("Symbol.iterator", FenValue.FromFunction(new FenFunction("Symbol.iterator", (itArgs, itThis) => itThis)));
                        return FenValue.FromObject(iterator);
                    }));
            }

            return FenValue.Undefined;
        }

        public bool IsSupportedNamedProperty(string key)
        {
            return !string.IsNullOrEmpty(key) && !NamedItem(key).IsNull;
        }

        private FenValue Item(int index)
        {
            var el = _source.ElementAtOrDefault(index);
            return WrapElement(el);
        }

        private FenValue NamedItem(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return FenValue.Null;
            }

            var el = _source.FirstOrDefault(e => string.Equals(e.Id, name, StringComparison.Ordinal));
            if (el == null)
            {
                el = _source.FirstOrDefault(e => string.Equals(e.GetAttribute("name"), name, StringComparison.Ordinal)
                                                 && string.Equals(e.NamespaceUri, Namespaces.Html, StringComparison.Ordinal));
            }

            return WrapElement(el);
        }

        private IEnumerable<string> EnumerateNamedProperties()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in _source)
            {
                if (el == null)
                {
                    continue;
                }

                var id = el.Id;
                if (!string.IsNullOrEmpty(id) && seen.Add(id))
                {
                    yield return id;
                }

                var name = el.GetAttribute("name");
                if (!string.IsNullOrEmpty(name)
                    && string.Equals(el.NamespaceUri, Namespaces.Html, StringComparison.Ordinal)
                    && seen.Add(name))
                {
                    yield return name;
                }
            }
        }

        private static bool TryParseArrayIndex(string key, out uint index)
        {
            index = 0;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            if (parsed == uint.MaxValue)
            {
                return false;
            }

            index = parsed;
            return true;
        }

        private FenValue WrapElement(Element el)
        {
            if (el == null)
            {
                return FenValue.Null;
            }

            return DomWrapperFactory.Wrap(el, _context);
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            var execCtx = context ?? _context;
            SetInternal(key, value, execCtx, execCtx?.StrictMode == true);
        }

        internal void SetFromVm(string key, FenValue value, bool strictMode)
        {
            SetInternal(key, value, _context, strictMode);
        }

        private void SetInternal(string key, FenValue value, IExecutionContext context, bool strictMode)
        {
            if (TryParseArrayIndex(key, out _))
            {
                if (strictMode)
                {
                    throw new FenTypeError("Cannot assign to read only property on HTMLCollection");
                }

                return;
            }

            if (!_expandos.ContainsKey(key) && IsSupportedNamedProperty(key))
            {
                if (strictMode)
                {
                    throw new FenTypeError("Cannot assign to read only named property on HTMLCollection");
                }

                return;
            }

            if (_expandos.TryGetValue(key, out var existing))
            {
                if (existing.IsAccessor)
                {
                    if (existing.Setter != null)
                    {
                        existing.Setter.Invoke(new[] { value }, context, FenValue.FromObject(this));
                    }

                    return;
                }

                if (existing.Writable == false)
                {
                    return;
                }

                existing.Value = value;
                _expandos[key] = existing;
                return;
            }

            _expandos[key] = new PropertyDescriptor
            {
                Value = value,
                Writable = true,
                Enumerable = true,
                Configurable = true,
                Getter = null,
                Setter = null
            };
            _expandoOrder.Add(key);
        }

        public IEnumerable<string> Keys(IExecutionContext context = null)
        {
            var count = _source.Count();
            for (int i = 0; i < count; i++)
            {
                yield return i.ToString(CultureInfo.InvariantCulture);
            }

            foreach (var name in EnumerateNamedProperties())
            {
                yield return name;
            }

            foreach (var expando in _expandoOrder)
            {
                if (_expandos.TryGetValue(expando, out var desc) && (desc.Enumerable ?? false))
                {
                    yield return expando;
                }
            }
        }

        public IEnumerable<string> GetOwnPropertyNames(IExecutionContext context = null)
        {
            var count = _source.Count();
            for (int i = 0; i < count; i++)
            {
                yield return i.ToString(CultureInfo.InvariantCulture);
            }

            foreach (var name in EnumerateNamedProperties())
            {
                yield return name;
            }

            foreach (var expando in _expandoOrder)
            {
                if (_expandos.ContainsKey(expando))
                {
                    yield return expando;
                }
            }
        }

        public bool Has(string key, IExecutionContext context = null)
        {
            if (_expandos.ContainsKey(key))
            {
                return true;
            }

            if (TryParseArrayIndex(key, out var index))
            {
                return index < (uint)_source.Count();
            }

            if (key == "length" || key == "item" || key == "namedItem" || key == "[Symbol.iterator]" || key == "Symbol.iterator" || key == "Symbol(Symbol.iterator)")
            {
                return true;
            }

            return IsSupportedNamedProperty(key);
        }

        public bool Delete(string key, IExecutionContext context = null)
        {
            if (_expandos.TryGetValue(key, out var desc))
            {
                if (desc.Configurable == false)
                {
                    return false;
                }

                _expandos.Remove(key);
                _expandoOrder.Remove(key);
                return true;
            }

            if (TryParseArrayIndex(key, out _) || IsSupportedNamedProperty(key))
            {
                return false;
            }

            return true;
        }

        public PropertyDescriptor? GetOwnPropertyDescriptor(string key)
        {
            if (_expandos.TryGetValue(key, out var expandoDesc))
            {
                return expandoDesc;
            }

            if (TryParseArrayIndex(key, out var index) && index < (uint)_source.Count())
            {
                return new PropertyDescriptor { Value = Get(key), Writable = false, Enumerable = true, Configurable = true, Getter = null, Setter = null };
            }

            if (IsSupportedNamedProperty(key))
            {
                return new PropertyDescriptor { Value = Get(key), Writable = false, Enumerable = false, Configurable = true, Getter = null, Setter = null };
            }

            return null;
        }

        public IObject _prototype;
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        public bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            if (!_expandos.ContainsKey(key) && (TryParseArrayIndex(key, out _) || IsSupportedNamedProperty(key)))
            {
                throw new FenTypeError("Cannot redefine non-configurable HTMLCollection property");
            }

            if (_expandos.TryGetValue(key, out var current))
            {
                if (current.Configurable == false)
                {
                    if (desc.Configurable == true) return false;
                    if (desc.Enumerable.HasValue && desc.Enumerable != current.Enumerable) return false;

                    if (current.IsData && current.Writable == false)
                    {
                        if (desc.Writable == true) return false;
                        if (desc.Value.HasValue && !desc.Value.Value.StrictEquals(current.Value)) return false;
                    }
                }

                if (desc.Value.HasValue) current.Value = desc.Value;
                if (desc.Writable.HasValue) current.Writable = desc.Writable;
                if (desc.Enumerable.HasValue) current.Enumerable = desc.Enumerable;
                if (desc.Configurable.HasValue) current.Configurable = desc.Configurable;
                if (desc.Getter != null || desc.Setter != null)
                {
                    current.Getter = desc.Getter;
                    current.Setter = desc.Setter;
                    if (!desc.Value.HasValue)
                    {
                        current.Value = null;
                    }
                }

                _expandos[key] = current;
                return true;
            }

            var normalized = desc;
            if (normalized.IsData)
            {
                if (!normalized.Value.HasValue) normalized.Value = FenValue.Undefined;
                if (!normalized.Writable.HasValue) normalized.Writable = false;
            }
            if (!normalized.Enumerable.HasValue) normalized.Enumerable = false;
            if (!normalized.Configurable.HasValue) normalized.Configurable = false;

            _expandos[key] = normalized;
            _expandoOrder.Add(key);
            return true;
        }
    }
}
