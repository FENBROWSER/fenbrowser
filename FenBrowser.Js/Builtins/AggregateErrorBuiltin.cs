using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class AggregateErrorBuiltin : IBuiltinModule
{
    public string Name => "AggregateError";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("AggregateError", JsValue.FromObject(context.MaterializeAggregateErrorConstructor())) };
}
