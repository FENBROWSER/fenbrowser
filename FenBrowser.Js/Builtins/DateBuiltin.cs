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

        var prototype = new DateObject(double.NaN);
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

        var capturedCtx = context;
        var capturedProto = prototypeHandle;
        var constructor = new NativeFunctionObject(
            "Date",
            (_, args) =>
            {
                var d = new DateObject(args.Count > 0 ? capturedCtx.ToNumber(args[0]) : double.NaN);
                d.SetPrototype(capturedProto);
                return JsValue.FromObject(heap.AllocateObject(d, AllocationSite.Current()));
            },
            args =>
            {
                var d = new DateObject(args.Count > 0 ? capturedCtx.ToNumber(args[0]) : double.NaN);
                d.SetPrototype(capturedProto);
                return JsValue.FromObject(heap.AllocateObject(d, AllocationSite.Current()));
            },
            length: 7);
        constructor.SetProperty("prototype", JsValue.FromObject(prototypeHandle));
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
            var year = (int)context.ToNumber(args[0]);
            if (year >= 0 && year <= 99) year += 1900;
            var month = args.Count > 1 ? (int)context.ToNumber(args[1]) : 0;
            var day = args.Count > 2 ? (int)context.ToNumber(args[2]) : 1;
            var hours = args.Count > 3 ? (int)context.ToNumber(args[3]) : 0;
            var minutes = args.Count > 4 ? (int)context.ToNumber(args[4]) : 0;
            var seconds = args.Count > 5 ? (int)context.ToNumber(args[5]) : 0;
            var ms = args.Count > 6 ? (int)context.ToNumber(args[6]) : 0;
            try
            {
                var dt = new DateTimeOffset(year, month + 1, day, hours, minutes, seconds, ms, TimeSpan.Zero);
                return JsValue.FromNumber(dt.ToUnixTimeMilliseconds());
            }
            catch (ArgumentOutOfRangeException)
            {
                return JsValue.FromNumber(double.NaN);
            }
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
