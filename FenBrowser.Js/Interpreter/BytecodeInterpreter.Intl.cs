using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// ECMA-402 Internationalization API — partial class with Intl-specific methods.
// Also carries EnsureProxyConstructor() placeholder until full Proxy lands.
public sealed partial class BytecodeInterpreter
{
    private ObjectHandle EnsureProxyConstructor()
    {
        throw new NotSupportedException("Proxy is not yet implemented.");
    }

    // ECMA-402 11.1.1 InitializeDateTimeFormat.
    private JsValue DateTimeFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
        var culture = ResolveCulture(locale);

        var prototype = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        var protoMethod = new NativeFunctionObject(
            "format",
            (_, fmtArgs) => DateTimeFormatPrototypeFormat(culture, fmtArgs),
            length: 1);
        var protoMethodHandle = _heap.AllocateObject(protoMethod, AllocationSite.Current());
        prototype.DefineOwnProperty(
            "format",
            new JsPropertyDescriptor(
                JsValue.FromObject(protoMethodHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        _heap.WriteBarrier(protoHandle, protoMethodHandle);

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);

        return JsValue.FromObject(instanceHandle);
    }

    // ECMA-402 11.3.2 Intl.DateTimeFormat.prototype.format(date).
    private JsValue DateTimeFormatPrototypeFormat(
        System.Globalization.CultureInfo culture,
        IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined)
            return JsValue.FromString("Invalid Date");

        var timestamp = DateArgToTimeClip(args[0]);
        if (double.IsNaN(timestamp))
            return JsValue.FromString("Invalid Date");

        try
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp).DateTime;
            return JsValue.FromString(dt.ToString("F", culture));
        }
        catch
        {
            return JsValue.FromString("Invalid Date");
        }
    }

    // Resolve a BCP 47 locale tag to a System.Globalization.CultureInfo, falling
    // back to InvariantCulture when the locale is not recognised.
    private static System.Globalization.CultureInfo ResolveCulture(string locale)
    {
        if (string.IsNullOrEmpty(locale))
            return System.Globalization.CultureInfo.InvariantCulture;

        try
        {
            return System.Globalization.CultureInfo.GetCultureInfo(locale);
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return System.Globalization.CultureInfo.InvariantCulture;
        }
    }

    // Extract a millisecond-since-epoch value from various JS date-like types.
    private double DateArgToTimeClip(JsValue arg)
    {
        if (arg.Tag == JsValueTag.Number || arg.Tag == JsValueTag.Int32)
            return arg.Tag == JsValueTag.Int32 ? (double)arg.AsInt32() : arg.AsNumber();

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = _heap.GetObject(arg.AsObjectHandle());
            if (obj is DateObject dateObj)
                return dateObj.TimeValue;
        }

        return ToNumber(arg);
    }
}
