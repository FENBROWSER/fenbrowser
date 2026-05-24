using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class SetBuiltin : IBuiltinModule
{
    public string Name => "Set";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("Set", JsValue.FromObject(context.MaterializeSetConstructor())) };
}
