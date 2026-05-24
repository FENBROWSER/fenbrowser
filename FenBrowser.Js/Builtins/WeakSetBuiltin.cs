using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class WeakSetBuiltin : IBuiltinModule
{
    public string Name => "WeakSet";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("WeakSet", JsValue.FromObject(context.MaterializeWeakSetConstructor())) };
}
