using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class TypedArrayBuiltin : IBuiltinModule
{
    public string Name => "%TypedArray%";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        var bindings = context.MaterializeTypedArrayConstructors();
        // Uint8Array-only base64/hex methods (proposal-arraybuffer-base64).
        context.InstallUint8ArrayBase64Hex();
        return bindings;
    }
}
