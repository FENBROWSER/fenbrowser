using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// ECMA-262 19.1.1 - Value Properties of the Global Object.
//
// The spec mandates that NaN, Infinity, and undefined are properties of the global
// object with {[[Writable]]: false, [[Enumerable]]: false, [[Configurable]]: false}.
// Together with globalThis (19.1.2.13, a writable+configurable own property of the
// global object), these are the value-typed globals that need no allocation - which
// is why they are the first builtin module: the interpreter can already install them
// today without depending on any heap shape change, and the module exists primarily
// to migrate the inline `InitializeBuiltinGlobals` code onto the registry surface.
//
// globalThis takes its concrete value from the host - hosts can pass null to disable
// it (matching surfaces that do not expose globalThis at all, such as the bare
// standalone shell early in startup).
[EcmaSpecReference(
    "19.1.1",
    AbstractOperation = "GlobalObject",
    Url = "https://tc39.es/ecma262/#sec-value-properties-of-the-global-object")]
public sealed class GlobalConstantsBuiltin : IBuiltinModule
{
    private readonly JsValue? _globalThisValue;

    public GlobalConstantsBuiltin(JsValue? globalThisValue = null)
    {
        _globalThisValue = globalThisValue;
    }

    public string Name => "GlobalConstants";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bindings = new List<BuiltinBinding>(4)
        {
            // 19.1.1.1 NaN
            BuiltinBinding.Frozen("NaN", JsValue.FromNumber(double.NaN)),
            // 19.1.1.2 Infinity
            BuiltinBinding.Frozen("Infinity", JsValue.FromNumber(double.PositiveInfinity)),
            // 19.1.1.3 undefined
            BuiltinBinding.Frozen("undefined", JsValue.Undefined),
        };

        if (_globalThisValue.HasValue)
        {
            // 19.1.2.13 globalThis: writable, non-enumerable, configurable - matches
            // the standard builtin shape (NonEnumerable factory), not Frozen.
            bindings.Add(BuiltinBinding.NonEnumerable("globalThis", _globalThisValue.Value));
        }

        return bindings;
    }
}
