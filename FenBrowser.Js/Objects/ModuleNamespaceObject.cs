using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Environments;

namespace FenBrowser.Js.Objects;

public sealed class ModuleNamespaceObject : JsObject
{
    private readonly HashSet<string> _exportNames;
    private readonly ModuleEnvironmentRecord? _environment;
    private readonly IReadOnlyDictionary<string, string>? _localNames;

    public ModuleNamespaceObject(Dictionary<string, JsValue> exports)
    {
        _exportNames = new HashSet<string>(exports.Keys, StringComparer.Ordinal);
        foreach (var kvp in exports)
        {
            // Module namespace export properties report writable:true even though
            // the namespace exotic [[Set]] operation always rejects writes.
            base.DefineOwnProperty(
                kvp.Key,
                new JsPropertyDescriptor(kvp.Value, Writable: true, Enumerable: true, Configurable: false));
        }
        SetPrototype(null);
        PreventExtensions();
    }

    public ModuleNamespaceObject(
        ModuleEnvironmentRecord environment,
        IReadOnlyDictionary<string, string> localNames,
        long toStringTagId)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(localNames);
        _environment = environment;
        _localNames = localNames;
        _exportNames = new HashSet<string>(localNames.Keys, StringComparer.Ordinal);
        foreach (var exportName in _exportNames)
        {
            base.DefineOwnProperty(
                exportName,
                new JsPropertyDescriptor(
                    JsValue.Undefined,
                    Writable: true,
                    Enumerable: true,
                    Configurable: false));
        }
        if (toStringTagId != 0)
        {
            base.DefineOwnSymbolProperty(
                toStringTagId,
                new JsPropertyDescriptor(
                    JsValue.FromString("Module"),
                    Writable: false,
                    Enumerable: false,
                    Configurable: false));
        }
        SetPrototype(null);
        PreventExtensions();
    }

    /// <summary>Every name routes through the module's own bindings.</summary>
    public override bool MayGainOwnPropertyOutsideShape(string key) => true;

    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (_environment is not null &&
            _localNames is not null &&
            _localNames.TryGetValue(key, out var localName))
        {
            var result = _environment.GetBindingValue(localName, strict: true, out var value);
            descriptor = new JsPropertyDescriptor(
                result == BindingOpResult.Ok ? value : JsValue.Undefined,
                Writable: true,
                Enumerable: true,
                Configurable: false);
            return true;
        }

        return base.TryGetOwnProperty(key, out descriptor);
    }

    public override IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        // Module namespace [[OwnPropertyKeys]] exposes exported string names in
        // sorted order, independent of source/dictionary insertion order.
        var names = new List<string>(_exportNames);
        names.Sort(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (TryGetOwnProperty(name, out var descriptor))
                yield return new KeyValuePair<string, JsPropertyDescriptor>(name, descriptor);
        }
    }

    // ECMA-262 Module Namespace Exotic [[DefineOwnProperty]]. Compatible
    // redefinitions of an existing export succeed, but callers cannot make the
    // property configurable/non-enumerable/non-writable or change its value.
    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        if (!_exportNames.Contains(key))
            return false;
        if (descriptor.HasConfigurable && descriptor.Configurable)
            return false;
        if (descriptor.HasEnumerable && !descriptor.Enumerable)
            return false;
        if (descriptor.IsAccessor)
            return false;
        if (descriptor.HasWritable && !descriptor.Writable)
            return false;

        if (descriptor.HasValue)
        {
            if (!TryGetOwnProperty(key, out var current) || !SameValue(descriptor.Value, current.Value))
                return false;
        }

        return true;
    }

    public override bool SetProperty(string key, JsValue value) => false;
    public override bool DeleteProperty(string key) => !_exportNames.Contains(key);

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        if (tracer.TraceEnvironmentChains)
        {
            _environment?.Trace(tracer);
        }
    }

    private static bool SameValue(JsValue left, JsValue right)
    {
        if ((left.Tag == JsValueTag.Int32 || left.Tag == JsValueTag.Number) &&
            (right.Tag == JsValueTag.Int32 || right.Tag == JsValueTag.Number))
        {
            var a = left.AsNumber();
            var b = right.AsNumber();
            if (double.IsNaN(a) && double.IsNaN(b))
                return true;
            if (a == 0d && b == 0d)
                return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
            return a == b;
        }

        if (left.Tag != right.Tag)
            return false;

        return left.Tag switch
        {
            JsValueTag.Undefined => true,
            JsValueTag.Null => true,
            JsValueTag.Boolean => left.AsBoolean() == right.AsBoolean(),
            JsValueTag.Int32 => left.AsInt32() == right.AsInt32(),
            JsValueTag.BigInt => left.AsBigInt() == right.AsBigInt(),
            JsValueTag.String => string.Equals(left.AsString(), right.AsString(), StringComparison.Ordinal),
            JsValueTag.Symbol => left.AsSymbolId() == right.AsSymbolId(),
            JsValueTag.Object => left.AsObjectHandle().Equals(right.AsObjectHandle()),
            JsValueTag.HostObject => left.AsHostObjectHandle().Equals(right.AsHostObjectHandle()),
            _ => false,
        };
    }
}
