using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class TypedArrayBuiltin : IBuiltinModule
{
    public string Name => "%TypedArray%";
    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
        => context.MaterializeTypedArrayConstructors();
}
