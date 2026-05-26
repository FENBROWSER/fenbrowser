using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;
namespace FenBrowser.Js.Objects;
public sealed class ModuleNamespaceObject : JsObject
{
    private readonly Dictionary<string, JsValue> _exports;
    public ModuleNamespaceObject(Dictionary<string, JsValue> exports)
    {
        _exports = new Dictionary<string, JsValue>(exports, StringComparer.Ordinal);
        foreach (var kvp in _exports)
            base.DefineOwnProperty(kvp.Key, new JsPropertyDescriptor(kvp.Value, Writable: false, Enumerable: true, Configurable: false));
        SetPrototype(null);
        PreventExtensions();
    }
    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor) => false;
    public override bool SetProperty(string key, JsValue value) => false;
    public override bool DeleteProperty(string key) => !_exports.ContainsKey(key);
    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
    }
}
