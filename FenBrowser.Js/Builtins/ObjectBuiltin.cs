using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class ObjectBuiltin : IBuiltinModule
{
    public string Name => "Object";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("Object", JsValue.FromObject(context.MaterializeObjectConstructor())) };
}
