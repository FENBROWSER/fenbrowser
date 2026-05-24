using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class ReflectBuiltin : IBuiltinModule
{
    public string Name => "Reflect";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("Reflect", JsValue.FromObject(context.MaterializeReflectObject())) };
}
