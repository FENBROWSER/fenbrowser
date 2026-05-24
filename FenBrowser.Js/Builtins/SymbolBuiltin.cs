using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

[EcmaSpecReference("20.4", AbstractOperation = "Symbol", Url = "https://tc39.es/ecma262/#sec-symbol-objects")]
public sealed class SymbolBuiltin : IBuiltinModule
{
    public string Name => "Symbol";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;

        var capturedCtx = context;
        var constructor = new NativeFunctionObject(
            "Symbol",
            (_, args) =>
            {
                var desc = args.Count > 0 && args[0].Tag != JsValueTag.Undefined ? context.ToStringValue(args[0]) : null;
                return context.CreateSymbol(desc);
            },
            _ => throw new JsThrownException(context.CreateTypeError("Symbol is not a constructor.")),
            length: 0);

        var handle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(handle);

        // ECMA-262 20.4.2 well-known symbols
        var wellKnown = new[] { "iterator", "asyncIterator", "hasInstance", "isConcatSpreadable",
            "match", "matchAll", "replace", "search", "species", "split", "toPrimitive",
            "toStringTag", "unscopables" };
        foreach (var name in wellKnown)
        {
            var sym = context.CreateWellKnownSymbol(name);
            constructor.DefineOwnProperty(name,
                new JsPropertyDescriptor(sym, Writable: false, Enumerable: false, Configurable: false));
        }

        // Symbol.for
        context.DefineIntrinsicFunction(handle, constructor, "for", (_, args) =>
            context.SymbolFor(args.Count > 0 ? context.ToStringValue(args[0]) : "undefined"), length: 1);

        // Symbol.keyFor
        context.DefineIntrinsicFunction(handle, constructor, "keyFor", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Symbol)
                throw new JsThrownException(context.CreateTypeError("Symbol.keyFor requires a symbol argument."));
            return context.SymbolKeyFor(args[0].AsSymbolId());
        }, length: 1);

        return new[] { BuiltinBinding.NonEnumerable("Symbol", JsValue.FromObject(handle)) };
    }
}
