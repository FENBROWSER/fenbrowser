using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class DataViewBuiltin : IBuiltinModule
{
    public string Name => "DataView";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("DataView", JsValue.FromObject(context.MaterializeDataViewConstructor())) };
}
