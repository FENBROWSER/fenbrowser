using System.Globalization;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 10.4.3 — String exotic object wrapping a [[StringData]] internal
// slot. Indexed integer property keys in [0, length) yield single-char
// substrings as enumerable+non-writable+non-configurable data properties.
// 'length' is non-enumerable, non-writable, non-configurable.
public sealed class StringObject : JsObject
{
    public StringObject(string value)
    {
        Value = value ?? string.Empty;
        // length installs as a regular slot so inline caches still hit it.
        _ = DefineOwnProperty("length", new JsPropertyDescriptor(
            JsValue.FromNumber(Value.Length),
            Writable: false, Enumerable: false, Configurable: false));
    }

    public string Value { get; }

    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        // Ordinary own properties (length, user-added) win first.
        if (base.TryGetOwnProperty(key, out descriptor)) return true;

        // ECMA-262 10.4.3.5 [[GetOwnProperty]] — synthesise indexed-char.
        if (TryGetStringIndexDescriptor(key, out descriptor))
            return true;

        descriptor = default;
        return false;
    }

    // ECMA-262 10.4.3.3 [[DefineOwnProperty]]. A character index denotes a
    // permanently non-writable/non-configurable property. It may be redefined
    // only with a descriptor compatible with that already-existing property;
    // importantly, it must never fall through to the ordinary-object path and
    // create a shadowing slot for "0", "1", ... .
    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        if (!TryGetStringIndexDescriptor(key, out var current))
            return base.DefineOwnProperty(key, descriptor);

        if (descriptor.IsAccessor)
            return false;
        if (descriptor.HasConfigurable && descriptor.Configurable)
            return false;
        if (descriptor.HasEnumerable && !descriptor.Enumerable)
            return false;
        if (descriptor.HasWritable && descriptor.Writable)
            return false;
        if (descriptor.HasValue && !SameStringIndexValue(descriptor.Value, current.Value))
            return false;

        return true;
    }

    // ECMA-262 10.4.3.4 [[Get]] ultimately observes the indexed property as
    // non-writable, so assigning to an in-range character index must fail rather
    // than creating an ordinary property with the same key.
    public override bool SetProperty(string key, JsValue value)
    {
        if (TryParseArrayIndex(key, out var index) && index < Value.Length)
            return false;
        return base.SetProperty(key, value);
    }

    // The synthetic indexed properties are non-configurable.
    public override bool DeleteProperty(string key)
    {
        if (TryParseArrayIndex(key, out var index) && index < Value.Length)
            return false;
        return base.DeleteProperty(key);
    }

    public override IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        // ECMA-262 10.4.3.13 [[OwnPropertyKeys]]: integer index keys come
        // first (in numeric order), then the ordinary string keys.
        for (var i = 0; i < Value.Length; i++)
        {
            yield return new KeyValuePair<string, JsPropertyDescriptor>(
                i.ToString(CultureInfo.InvariantCulture),
                CreateStringIndexDescriptor(i));
        }

        foreach (var pair in base.EnumerateOwnProperties())
            yield return pair;
    }

    private bool TryGetStringIndexDescriptor(string key, out JsPropertyDescriptor descriptor)
    {
        if (TryParseArrayIndex(key, out var index) && index < Value.Length)
        {
            descriptor = CreateStringIndexDescriptor(index);
            return true;
        }

        descriptor = default;
        return false;
    }

    private JsPropertyDescriptor CreateStringIndexDescriptor(int index)
        => new(
            JsValue.FromString(Value[index].ToString(CultureInfo.InvariantCulture)),
            Writable: false,
            Enumerable: true,
            Configurable: false);

    private static bool SameStringIndexValue(JsValue left, JsValue right)
        => left.Tag == JsValueTag.String &&
           right.Tag == JsValueTag.String &&
           string.Equals(left.AsString(), right.AsString(), StringComparison.Ordinal);

    private static bool TryParseArrayIndex(string key, out int index)
    {
        // Spec: only canonical-numeric string keys map to indexed slots.
        // "0", "1", ..., not "00" or "+1" or " 1".
        index = 0;
        if (string.IsNullOrEmpty(key)) return false;
        if (key.Length > 1 && key[0] == '0') return false;
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c < '0' || c > '9') return false;
            var next = index * 10L + (c - '0');
            if (next > int.MaxValue) return false;
            index = (int)next;
        }
        return true;
    }
}
