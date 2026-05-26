using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// ECMA-402 The Intl Object.
//
// Intl is a singleton ordinary object (not a constructor) whose properties
// are all non-enumerable. It holds the DateTimeFormat, NumberFormat, and
// Collator constructors plus the getCanonicalLocales static function.
//
// This is a thin wrapper — the actual object graph is built by
// BytecodeInterpreter.EnsureIntlObject().
[EcmaSpecReference(
    "8",
    AbstractOperation = "Intl",
    Url = "https://tc39.es/ecma402/#intl-object")]
public sealed class IntlBuiltin : IBuiltinModule
{
    public string Name => "Intl";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new[] { BuiltinBinding.NonEnumerable("Intl", JsValue.FromObject(context.MaterializeIntlObject())) };
    }
}
