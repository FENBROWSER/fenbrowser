using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// RadioNodeList wrapper used by HTMLFormControlsCollection.namedItem().
    /// </summary>
    public sealed class RadioNodeListWrapper : IObject
    {
        private readonly Func<IEnumerable<Element>> _sourceProvider;
        private readonly IExecutionContext _context;
        private readonly Dictionary<string, FenValue> _expandos = new(StringComparer.Ordinal);

        public RadioNodeListWrapper(Func<IEnumerable<Element>> sourceProvider, IExecutionContext context)
        {
            _sourceProvider = sourceProvider ?? (() => Array.Empty<Element>());
            _context = context;

            var prototype = DomWrapperFactory.GetConstructorPrototype(context, "RadioNodeList")
                ?? DomWrapperFactory.GetConstructorPrototype(context, "NodeList");
            if (prototype != null)
            {
                SetPrototype(prototype);
            }
        }

        public object NativeObject { get; set; }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            if (TryParseArrayIndex(key, out var index))
            {
                return Item(index);
            }

            switch (key)
            {
                case "length":
                    return FenValue.FromNumber(GetSnapshot().Count);
                case "item":
                    return FenValue.FromFunction(new FenFunction("item", (args, thisVal) =>
                    {
                        if (args.Length == 0)
                        {
                            return FenValue.Null;
                        }

                        return Item(ToCollectionIndex(args[0]));
                    }));
                case "value":
                    return FenValue.FromString(GetValue());
                case "keys":
                    return FenValue.FromFunction(new FenFunction("keys", (_, __) =>
                        FenValue.FromObject(CreateIteratorObject((i, _) => FenValue.FromNumber(i)))));
                case "values":
                    return FenValue.FromFunction(new FenFunction("values", (_, __) =>
                        FenValue.FromObject(CreateIteratorObject((_, element) => WrapElement(element)))));
                case "entries":
                    return FenValue.FromFunction(new FenFunction("entries", (_, __) =>
                        FenValue.FromObject(CreateIteratorObject((i, element) =>
                        {
                            var pair = FenObject.CreateArray();
                            pair.Set("0", FenValue.FromNumber(i));
                            pair.Set("1", WrapElement(element));
                            pair.Set("length", FenValue.FromNumber(2));
                            return FenValue.FromObject(pair);
                        }))));
                case "[Symbol.iterator]":
                case "Symbol.iterator":
                case "Symbol(Symbol.iterator)":
                    return FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, __) =>
                        FenValue.FromObject(CreateIteratorObject((_, element) => WrapElement(element)))));
                case "forEach":
                    return FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) =>
                    {
                        if (args.Length == 0 || !args[0].IsFunction)
                        {
                            return FenValue.Undefined;
                        }

                        var callback = args[0].AsFunction();
                        var snapshot = GetSnapshot();
                        for (var i = 0; i < snapshot.Count; i++)
                        {
                            var wrapped = WrapElement(snapshot[i]);
                            callback.Invoke(new[] { wrapped, FenValue.FromNumber(i), FenValue.FromObject(this) }, _context);
                        }

                        return FenValue.Undefined;
                    }));
            }

            if (_expandos.TryGetValue(key, out var expando))
            {
                return expando;
            }

            return FenValue.Undefined;
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            if (string.Equals(key, "value", StringComparison.Ordinal))
            {
                SetValue(value.ToString() ?? string.Empty);
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
            if (_expandos.ContainsKey(key))
            {
                return true;
            }

            if (TryParseArrayIndex(key, out var index))
            {
                return index < (uint)GetSnapshot().Count;
            }

            return key == "length" ||
                   key == "item" ||
                   key == "value" ||
                   key == "keys" ||
                   key == "values" ||
                   key == "entries" ||
                   key == "[Symbol.iterator]" ||
                   key == "Symbol.iterator" ||
                   key == "Symbol(Symbol.iterator)" ||
                   key == "forEach";
        }

        public bool Delete(string key, IExecutionContext context = null)
        {
            if (TryParseArrayIndex(key, out var index))
            {
                return index >= (uint)GetSnapshot().Count;
            }

            return _expandos.Remove(key) || !_expandos.ContainsKey(key);
        }

        public IEnumerable<string> Keys(IExecutionContext context = null)
        {
            var count = GetSnapshot().Count;
            for (var i = 0; i < count; i++)
            {
                yield return i.ToString(CultureInfo.InvariantCulture);
            }

            foreach (var key in _expandos.Keys)
            {
                yield return key;
            }
        }

        public IEnumerable<string> GetOwnPropertyNames(IExecutionContext context = null) => Keys(context);

        public PropertyDescriptor? GetOwnPropertyDescriptor(string key)
        {
            if (_expandos.TryGetValue(key, out var value))
            {
                return new PropertyDescriptor
                {
                    Value = value,
                    Writable = true,
                    Enumerable = true,
                    Configurable = true,
                    Getter = null,
                    Setter = null
                };
            }

            if (TryParseArrayIndex(key, out var index) && index < (uint)GetSnapshot().Count)
            {
                return new PropertyDescriptor
                {
                    Value = Get(key),
                    Writable = false,
                    Enumerable = true,
                    Configurable = true,
                    Getter = null,
                    Setter = null
                };
            }

            return null;
        }

        public bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            if (key == "value")
            {
                if (desc.Value.HasValue)
                {
                    SetValue(desc.Value.Value.ToString() ?? string.Empty);
                }

                return true;
            }

            if (key == "length" ||
                key == "item" ||
                key == "keys" ||
                key == "values" ||
                key == "entries" ||
                key == "[Symbol.iterator]" ||
                key == "Symbol.iterator" ||
                key == "Symbol(Symbol.iterator)" ||
                key == "forEach")
            {
                return false;
            }

            if (TryParseArrayIndex(key, out var index))
            {
                return index >= (uint)GetSnapshot().Count;
            }

            if (desc.IsAccessor)
            {
                return false;
            }

            _expandos[key] = desc.Value ?? FenValue.Undefined;
            return true;
        }

        public IObject _prototype;
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        private FenValue Item(uint index)
        {
            if (index > int.MaxValue)
            {
                return FenValue.Null;
            }

            var snapshot = GetSnapshot();
            if (index >= snapshot.Count)
            {
                return FenValue.Null;
            }

            return WrapElement(snapshot[(int)index]);
        }

        private FenValue WrapElement(Element element)
        {
            if (element == null)
            {
                return FenValue.Null;
            }

            return DomWrapperFactory.Wrap(element, _context);
        }

        private List<Element> GetSnapshot()
        {
            return (_sourceProvider?.Invoke() ?? Array.Empty<Element>())
                .Where(element => element != null)
                .ToList();
        }

        private string GetValue()
        {
            foreach (var element in GetSnapshot())
            {
                var wrapped = DomWrapperFactory.Wrap(element, _context);
                if (!wrapped.IsObject)
                {
                    continue;
                }

                var checkedValue = wrapped.AsObject().Get("checked", _context);
                if (checkedValue.ToBoolean())
                {
                    var value = wrapped.AsObject().Get("value", _context);
                    return value.IsUndefined || value.IsNull ? string.Empty : value.ToString() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private void SetValue(string value)
        {
            var targetValue = value ?? string.Empty;
            var radios = GetSnapshot().Where(IsRadioInput).ToList();
            if (radios.Count == 0)
            {
                return;
            }

            Element match = null;
            foreach (var radio in radios)
            {
                if (string.Equals(GetElementValue(radio), targetValue, StringComparison.Ordinal))
                {
                    match = radio;
                    break;
                }
            }

            foreach (var radio in radios)
            {
                var shouldCheck = ReferenceEquals(radio, match);
                var wrapped = DomWrapperFactory.Wrap(radio, _context);
                if (wrapped.IsObject)
                {
                    wrapped.AsObject().Set("checked", FenValue.FromBoolean(shouldCheck), _context);
                }
                else
                {
                    if (shouldCheck)
                    {
                        radio.SetAttribute("checked", string.Empty);
                    }
                    else
                    {
                        radio.RemoveAttribute("checked");
                    }
                }
            }
        }

        private static bool IsRadioInput(Element element)
        {
            if (!string.Equals(element?.LocalName, "input", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(element.GetAttribute("type"), "radio", StringComparison.OrdinalIgnoreCase);
        }

        private string GetElementValue(Element element)
        {
            if (element == null)
            {
                return string.Empty;
            }

            var wrapped = DomWrapperFactory.Wrap(element, _context);
            if (wrapped.IsObject)
            {
                var value = wrapped.AsObject().Get("value", _context);
                if (!value.IsUndefined && !value.IsNull)
                {
                    return value.ToString() ?? string.Empty;
                }
            }

            return element.GetAttribute("value") ?? "on";
        }

        private FenObject CreateIteratorObject(Func<int, Element, FenValue> projection)
        {
            var snapshot = GetSnapshot();
            var iterator = new FenObject();
            var index = 0;
            iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
            {
                var step = new FenObject();
                if (index >= snapshot.Count)
                {
                    step.Set("value", FenValue.Undefined);
                    step.Set("done", FenValue.FromBoolean(true));
                    return FenValue.FromObject(step);
                }

                step.Set("value", projection(index, snapshot[index]));
                step.Set("done", FenValue.FromBoolean(false));
                index++;
                return FenValue.FromObject(step);
            })));
            iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, __) => FenValue.FromObject(iterator))));
            iterator.Set("Symbol.iterator", iterator.Get("[Symbol.iterator]"));
            iterator.Set("Symbol(Symbol.iterator)", iterator.Get("[Symbol.iterator]"));
            return iterator;
        }

        private static bool TryParseArrayIndex(string key, out uint index)
        {
            return uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index);
        }

        private static uint ToCollectionIndex(FenValue value)
        {
            var number = value.ToNumber();
            if (double.IsNaN(number) || double.IsInfinity(number) || number < 0)
            {
                return uint.MaxValue;
            }

            return unchecked((uint)number);
        }
    }
}
