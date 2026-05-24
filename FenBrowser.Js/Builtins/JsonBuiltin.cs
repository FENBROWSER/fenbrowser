using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class JsonBuiltin : IBuiltinModule
{
    public string Name => "JSON";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("JSON", JsValue.FromObject(context.MaterializeJsonObject())) };
}
