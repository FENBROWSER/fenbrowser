using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class MapBuiltin : IBuiltinModule
{
    public string Name => "Map";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("Map", JsValue.FromObject(context.MaterializeMapConstructor())) };
}
