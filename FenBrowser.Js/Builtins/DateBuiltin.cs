using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

[EcmaSpecReference("21.4", AbstractOperation = "Date", Url = "https://tc39.es/ecma262/#sec-date-objects")]
public sealed class DateBuiltin : IBuiltinModule
{
    public string Name => "Date";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;

        // ECMA-262 21.4.3: the Date prototype is an ordinary object, NOT a Date
        // instance — it has no [[DateValue]], so Date.prototype.getTime() etc. throw.
        var prototype = new JsObject();
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

        var capturedCtx = context;
        var capturedProto = prototypeHandle;
        var constructor = new NativeFunctionObject(
            "Date",
            // Called as a function: return a string for "now" (arguments ignored).
            (_, _) => JsValue.FromString(DateTimeOffset.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", System.Globalization.CultureInfo.InvariantCulture)),
            // Called with new: full ECMA-262 21.4.2.1 argument handling.
            args => capturedCtx.ConstructDate(args),
            length: 7);
        constructor.SetPrototype(context.GetFunctionPrototype());
        constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(constructorHandle);
        heap.WriteBarrier(constructorHandle, prototypeHandle);

        var protoObj = heap.GetObject(prototypeHandle);
        protoObj.DefineOwnProperty("constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, constructorHandle);

        // Date.now()
        context.DefineIntrinsicFunction(constructorHandle, constructor, "now", (_, _) =>
            JsValue.FromNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), length: 0);

        // Date.UTC(year[, month[, ...]])
        context.DefineIntrinsicFunction(constructorHandle, constructor, "UTC", (_, args) =>
        {
            if (args.Count == 0) return JsValue.FromNumber(double.NaN);
            // ECMA-262 21.4.3.4: ToNumber every component (full double precision, not
            // an int cast), map a 0-99 year to 1900+year, then compose via MakeDate.
            var year = context.ToNumber(args[0]);
            if (double.IsFinite(year) && year >= 0 && year <= 99) year += 1900;
            var month = args.Count > 1 ? context.ToNumber(args[1]) : 0;
            var day = args.Count > 2 ? context.ToNumber(args[2]) : 1;
            var hours = args.Count > 3 ? context.ToNumber(args[3]) : 0;
            var minutes = args.Count > 4 ? context.ToNumber(args[4]) : 0;
            var seconds = args.Count > 5 ? context.ToNumber(args[5]) : 0;
            var ms = args.Count > 6 ? context.ToNumber(args[6]) : 0;
            var v = DateMath.MakeDate(DateMath.MakeDay(year, month, day), DateMath.MakeTime(hours, minutes, seconds, ms));
            return JsValue.FromNumber(DateMath.TimeClip(v));
        }, length: 7);

        // Date.parse(string)
        context.DefineIntrinsicFunction(constructorHandle, constructor, "parse", (_, args) =>
        {
            var text = args.Count > 0 ? context.ToStringValue(args[0]) : "Invalid Date";
            if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
                return JsValue.FromNumber(dt.ToUnixTimeMilliseconds());
            return JsValue.FromNumber(double.NaN);
        }, length: 1);

        // Delegate prototype method installation to the interpreter
        context.InstallDatePrototypeMethods(prototypeHandle, protoObj);

        return new[] { BuiltinBinding.NonEnumerable("Date", JsValue.FromObject(constructorHandle)) };
    }
}
