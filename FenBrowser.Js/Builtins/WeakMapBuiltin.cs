using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class WeakMapBuiltin : IBuiltinModule
{
    public string Name => "WeakMap";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("WeakMap", JsValue.FromObject(context.MaterializeWeakMapConstructor())) };
}
