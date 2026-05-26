using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class ArrayBufferBuiltin : IBuiltinModule
{
    public string Name => "ArrayBuffer";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("ArrayBuffer", JsValue.FromObject(context.MaterializeArrayBufferConstructor())) };
}
