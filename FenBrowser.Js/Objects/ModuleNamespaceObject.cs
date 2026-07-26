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
            base.DefineOwnProperty(kvp.Key, new JsPropertyDescriptor(kvp.Value, Writable: false, Enumerable: true, Configurable: false));
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
                    Writable: false,
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

    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (_environment is not null &&
            _localNames is not null &&
            _localNames.TryGetValue(key, out var localName))
        {
            var result = _environment.GetBindingValue(localName, strict: true, out var value);
            descriptor = new JsPropertyDescriptor(
                result == BindingOpResult.Ok ? value : JsValue.Undefined,
                Writable: false,
                Enumerable: true,
                Configurable: false);
            return true;
        }

        return base.TryGetOwnProperty(key, out descriptor);
    }

    public override IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        foreach (var pair in base.EnumerateOwnProperties())
        {
            if (TryGetOwnProperty(pair.Key, out var descriptor))
            {
                yield return new KeyValuePair<string, JsPropertyDescriptor>(pair.Key, descriptor);
            }
        }
    }

    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor) => false;
    public override bool SetProperty(string key, JsValue value) => false;
    public override bool DeleteProperty(string key) => !_exportNames.Contains(key);

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        _environment?.Trace(tracer);
    }
}
