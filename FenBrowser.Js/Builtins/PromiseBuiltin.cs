using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class PromiseBuiltin : IBuiltinModule
{
    public string Name => "Promise";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("Promise", JsValue.FromObject(context.MaterializePromiseConstructor())) };
}
