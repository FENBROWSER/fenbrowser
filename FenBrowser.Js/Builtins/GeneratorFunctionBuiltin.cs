using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class GeneratorFunctionBuiltin : IBuiltinModule
{
    public string Name => "GeneratorFunction";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        var ctor = context.MaterializeGeneratorFunctionConstructor();
        return new[] { BuiltinBinding.NonEnumerable("GeneratorFunction", JsValue.FromObject(ctor)) };
    }
}
