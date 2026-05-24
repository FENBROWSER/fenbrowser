using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class ArrayBuiltin : IBuiltinModule
{
    public string Name => "Array";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("Array", JsValue.FromObject(context.MaterializeArrayConstructor())) };
}
