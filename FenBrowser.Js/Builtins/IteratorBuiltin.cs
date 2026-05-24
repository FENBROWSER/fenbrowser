using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class IteratorBuiltin : IBuiltinModule
{
    public string Name => "Iterator";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("Iterator", JsValue.FromObject(context.MaterializeIteratorConstructor())) };
}
