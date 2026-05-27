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
        var prototype = new JsObject();
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

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
        _ = constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));

        var handle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(handle);
        heap.WriteBarrier(handle, prototypeHandle);
        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(JsValue.FromObject(handle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, handle);

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

        context.DefineIntrinsicFunction(prototypeHandle, prototype, "valueOf", (thisValue, _) =>
        {
            if (thisValue.Tag == JsValueTag.Symbol)
            {
                return thisValue;
            }

            if (thisValue.Tag == JsValueTag.Object &&
                context.Heap.GetObject(thisValue.AsObjectHandle()) is SymbolObject symbolObject)
            {
                return JsValue.SymbolFromId(symbolObject.SymbolId);
            }

            throw new JsThrownException(context.CreateTypeError("Symbol.prototype.valueOf called on incompatible receiver."));
        }, length: 0);

        context.DefineIntrinsicFunction(prototypeHandle, prototype, "toString", (thisValue, _) =>
        {
            var symbol = thisValue.Tag == JsValueTag.Symbol
                ? thisValue
                : thisValue.Tag == JsValueTag.Object && context.Heap.GetObject(thisValue.AsObjectHandle()) is SymbolObject symbolObject
                    ? JsValue.SymbolFromId(symbolObject.SymbolId)
                    : throw new JsThrownException(context.CreateTypeError("Symbol.prototype.toString called on incompatible receiver."));
            return JsValue.FromString("Symbol(" + (symbol.AsSymbolDescription() ?? string.Empty) + ")");
        }, length: 0);

        return new[] { BuiltinBinding.NonEnumerable("Symbol", JsValue.FromObject(handle)) };
    }
}
