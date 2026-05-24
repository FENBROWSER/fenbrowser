using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class FunctionBuiltin : IBuiltinModule
{
    public string Name => "Function";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("Function", JsValue.FromObject(context.MaterializeFunctionConstructor())) };
}
