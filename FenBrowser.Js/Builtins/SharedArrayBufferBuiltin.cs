using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class SharedArrayBufferBuiltin : IBuiltinModule
{
    public string Name => "SharedArrayBuffer";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => new[] { BuiltinBinding.NonEnumerable("SharedArrayBuffer", JsValue.FromObject(context.MaterializeSharedArrayBufferConstructor())) };
}
