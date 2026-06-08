using System.Text.RegularExpressions;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

[EcmaSpecReference("22.1", AbstractOperation = "String", Url = "https://tc39.es/ecma262/#sec-string-objects")]
public sealed class StringBuiltin : IBuiltinModule
{
    public string Name => "String";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;

        var prototype = new StringObject(string.Empty);
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

        var capturedCtx = context;
        var capturedProto = prototypeHandle;
        var constructor = new NativeFunctionObject(
            "String",
            (_, args) => JsValue.FromString(args.Count > 0 ? context.ToStringValue(args[0]) : string.Empty),
            args =>
            {
                var obj = new StringObject(args.Count > 0 ? context.ToStringValue(args[0]) : string.Empty);
                obj.SetPrototype(capturedProto);
                return JsValue.FromObject(heap.AllocateObject(obj, AllocationSite.Current()));
            },
            length: 1);
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(constructorHandle);
        heap.WriteBarrier(constructorHandle, prototypeHandle);

        var protoObj = heap.GetObject(prototypeHandle);
        _ = protoObj.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        heap.WriteBarrier(prototypeHandle, constructorHandle);

        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "toString", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv)));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "valueOf", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv)));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "charAt", CharAt, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "charCodeAt", CharCodeAt, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "codePointAt", CodePointAt, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "at", At, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "indexOf", IndexOf, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "lastIndexOf", LastIndexOf, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "includes", Includes, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "startsWith", StartsWith, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "endsWith", EndsWith, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "slice", Slice, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "substring", Substring, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "substr", Substr, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "concat", Concat, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "repeat", Repeat, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "padStart", PadStart, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "padEnd", PadEnd, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "trim", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).Trim()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "trimStart", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).TrimStart()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "trimEnd", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).TrimEnd()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "toUpperCase", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).ToUpperInvariant()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "toLowerCase", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).ToLowerInvariant()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "toLocaleUpperCase", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).ToUpperInvariant()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "toLocaleLowerCase", (ctx, tv, _) => JsValue.FromString(RequireString(ctx, tv).ToLowerInvariant()));
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "match", Match, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "matchAll", MatchAll, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "search", Search, length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "split", Split, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "replace", Replace, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "replaceAll", ReplaceAll, length: 2);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "normalize", Normalize, length: 0);
        DefineIteratorMethod(capturedCtx, heap, prototypeHandle, protoObj);

        // Annex B B.2.2 — legacy HTML wrappers. Spec is purely lexical:
        // each wraps `this` in an HTML tag, escaping any " in the attribute.
        // Audit gap §4.1.
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "anchor",
            (ctx, tv, args) => JsValue.FromString(HtmlTagWithAttr(ctx, tv, "a", "name", args)), length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "link",
            (ctx, tv, args) => JsValue.FromString(HtmlTagWithAttr(ctx, tv, "a", "href", args)), length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "fontcolor",
            (ctx, tv, args) => JsValue.FromString(HtmlTagWithAttr(ctx, tv, "font", "color", args)), length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "fontsize",
            (ctx, tv, args) => JsValue.FromString(HtmlTagWithAttr(ctx, tv, "font", "size", args)), length: 1);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "big",
            (ctx, tv, _) => JsValue.FromString(HtmlTag(ctx, tv, "big")), length: 0);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "blink",
            (ctx, tv, _) => JsValue.FromString(HtmlTag(ctx, tv, "blink")), length: 0);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "bold",
            (ctx, tv, _) => JsValue.FromString(HtmlTag(ctx, tv, "b")), length: 0);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "fixed",
            (ctx, tv, _) => JsValue.FromString(HtmlTag(ctx, tv, "tt")), length: 0);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "italics",
            (ctx, tv, _) => JsValue.FromString(HtmlTag(ctx, tv, "i")), length: 0);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "small",
            (ctx, tv, _) => JsValue.FromString(HtmlTag(ctx, tv, "small")), length: 0);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "strike",
            (ctx, tv, _) => JsValue.FromString(HtmlTag(ctx, tv, "strike")), length: 0);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "sub",
            (ctx, tv, _) => JsValue.FromString(HtmlTag(ctx, tv, "sub")), length: 0);
        DefineProtoMethod(capturedCtx, heap, prototypeHandle, protoObj, "sup",
            (ctx, tv, _) => JsValue.FromString(HtmlTag(ctx, tv, "sup")), length: 0);

        // String.fromCharCode
        context.DefineIntrinsicFunction(constructorHandle, constructor, "fromCharCode", (_, args) =>
        {
            var sb = new System.Text.StringBuilder(args.Count);
            for (var i = 0; i < args.Count; i++)
                sb.Append((char)(ushort)MathHelpers.ToInt32(context.ToNumber(args[i])));
            return JsValue.FromString(sb.ToString());
        }, length: 1);

        // String.fromCodePoint
        context.DefineIntrinsicFunction(constructorHandle, constructor, "fromCodePoint", (_, args) =>
        {
            var sb = new System.Text.StringBuilder(args.Count);
            for (var i = 0; i < args.Count; i++)
            {
                var n = context.ToNumber(args[i]);
                if (double.IsNaN(n) || n < 0 || n > 0x10FFFF || Math.Floor(n) != n)
                    throw new JsThrownException(context.CreateRangeError("Invalid code point in String.fromCodePoint argument list."));
                var codePoint = (int)n;
                if (codePoint <= 0xFFFF)
                {
                    // ECMAScript allows lone surrogate code points here; emit the
                    // corresponding single UTF-16 code unit directly.
                    sb.Append((char)codePoint);
                    continue;
                }

                var astral = codePoint - 0x10000;
                sb.Append((char)(0xD800 + (astral >> 10)));
                sb.Append((char)(0xDC00 + (astral & 0x3FF)));
            }
            return JsValue.FromString(sb.ToString());
        }, length: 1);

        // String.raw
        context.DefineIntrinsicFunction(constructorHandle, constructor, "raw", (_, args) =>
        {
            if (args.Count == 0 || (args[0].Tag != JsValueTag.Object && args[0].Tag != JsValueTag.String))
                throw new JsThrownException(context.CreateTypeError("String.raw: template must be coercible to Object."));
            var template = args[0];
            var templateObj = heap.GetObject(template.AsObjectHandle());
            if (!context.TryGetPropertyValue(templateObj, template, "raw", out var rawValue) || rawValue.Tag != JsValueTag.Object)
                throw new JsThrownException(context.CreateTypeError("String.raw: template.raw must be an object."));
            var rawObj = heap.GetObject(rawValue.AsObjectHandle());
            var rawLen = context.GetArrayLength(rawObj);
            if (rawLen == 0) return JsValue.FromString(string.Empty);
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < rawLen; i++)
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (context.TryGetPropertyValue(rawObj, rawValue, key, out var seg))
                    sb.Append(context.ToStringValue(seg));
                if (i + 1 == rawLen) break;
                if (i + 1 < args.Count)
                    sb.Append(context.ToStringValue(args[i + 1]));
            }
            return JsValue.FromString(sb.ToString());
        }, length: 1);

        return new[] { BuiltinBinding.NonEnumerable("String", JsValue.FromObject(constructorHandle)) };
    }

    // Annex B B.2.2.2.1 CreateHTML(string, tag, attribute, value). Per spec
    // the attribute value has any `"` replaced by `&quot;` and is wrapped in
    // double quotes; tag and attribute names are emitted lowercase.
    private static string HtmlTag(IBuiltinContext ctx, JsValue thisValue, string tag)
    {
        var s = RequireString(ctx, thisValue);
        return "<" + tag + ">" + s + "</" + tag + ">";
    }

    private static string HtmlTagWithAttr(IBuiltinContext ctx, JsValue thisValue, string tag, string attr, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var val = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        val = val.Replace("\"", "&quot;");
        return "<" + tag + " " + attr + "=\"" + val + "\">" + s + "</" + tag + ">";
    }

    private static string RequireString(IBuiltinContext ctx, JsValue thisValue)
    {
        if (thisValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            throw new JsThrownException(ctx.CreateTypeError("String.prototype method called on incompatible receiver."));
        }

        if (thisValue.Tag == JsValueTag.String)
        {
            return thisValue.AsString();
        }

        if (thisValue.Tag == JsValueTag.Object &&
            ctx.Heap.GetObject(thisValue.AsObjectHandle()) is StringObject so)
        {
            return so.Value;
        }

        return ctx.ToStringValue(thisValue);
    }

    private static int ClampIndex(IReadOnlyList<JsValue> args, int idx, int defaultValue, int length)
    {
        if (idx >= args.Count || args[idx].Tag == JsValueTag.Undefined)
            return Math.Clamp(defaultValue, 0, length);
        var raw = args[idx].Tag == JsValueTag.Int32 ? args[idx].AsInt32() : (int)args[idx].AsNumber();
        return Math.Clamp(raw, 0, length);
    }

    private static int WrapNegative(int value, int length) => value < 0 ? Math.Max(length + value, 0) : Math.Min(value, length);

    private delegate JsValue ProtoMethod(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args);

    private static void DefineProtoMethod(IBuiltinContext ctx, JsHeap heap, ObjectHandle protoHandle, JsObject proto, string name, ProtoMethod method, int length = 0)
    {
        var captured = ctx;
        var fn = new NativeFunctionObject(name, (thisValue, args) => method(captured, thisValue, args), length: length);
        var fnHandle = heap.AllocateObject(fn, AllocationSite.Current());
        var callHandle = ctx.GetFunctionCallMethod();
        fn.SetPrototype(ctx.GetObjectPrototype());
        fn.SetProperty("call", JsValue.FromObject(callHandle));
        heap.WriteBarrier(fnHandle, callHandle);
        proto.DefineOwnProperty(name, new JsPropertyDescriptor(JsValue.FromObject(fnHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(protoHandle, fnHandle);
    }

    private static void DefineIteratorMethod(IBuiltinContext ctx, JsHeap heap, ObjectHandle protoHandle, JsObject proto)
    {
        var iteratorPrototypeHandle = GetIteratorPrototypeHandle(ctx);
        var stringIteratorPrototype = new JsObject();
        stringIteratorPrototype.SetPrototype(iteratorPrototypeHandle);
        var stringIteratorPrototypeHandle = heap.AllocateObject(stringIteratorPrototype, AllocationSite.Current());
        heap.PushRoot(stringIteratorPrototypeHandle);
        heap.WriteBarrier(stringIteratorPrototypeHandle, iteratorPrototypeHandle);

        var callHandle = ctx.GetFunctionCallMethod();
        var next = new NativeFunctionObject("next", (thisValue, _) => StringIteratorNext(ctx, thisValue), length: 0);
        var nextHandle = heap.AllocateObject(next, AllocationSite.Current());
        next.SetPrototype(ctx.GetObjectPrototype());
        next.SetProperty("call", JsValue.FromObject(callHandle));
        heap.WriteBarrier(nextHandle, callHandle);
        _ = stringIteratorPrototype.DefineOwnProperty(
            "next",
            new JsPropertyDescriptor(JsValue.FromObject(nextHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(stringIteratorPrototypeHandle, nextHandle);

        var toStringTag = ctx.CreateWellKnownSymbol("toStringTag");
        _ = stringIteratorPrototype.DefineOwnSymbolProperty(
            toStringTag.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromString("String Iterator"), Writable: false, Enumerable: false, Configurable: true));

        var iteratorMethod = new NativeFunctionObject("[Symbol.iterator]", (thisValue, _) =>
        {
            var iterator = new StringIteratorInstance(RequireString(ctx, thisValue));
            iterator.SetPrototype(stringIteratorPrototypeHandle);
            var iteratorHandle = heap.AllocateObject(iterator, AllocationSite.Current());
            heap.WriteBarrier(iteratorHandle, stringIteratorPrototypeHandle);
            return JsValue.FromObject(iteratorHandle);
        }, length: 0);
        var iteratorMethodHandle = heap.AllocateObject(iteratorMethod, AllocationSite.Current());
        iteratorMethod.SetPrototype(ctx.GetObjectPrototype());
        iteratorMethod.SetProperty("call", JsValue.FromObject(callHandle));
        heap.WriteBarrier(iteratorMethodHandle, callHandle);

        var iteratorSymbol = ctx.CreateWellKnownSymbol("iterator");
        _ = proto.DefineOwnSymbolProperty(
            iteratorSymbol.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromObject(iteratorMethodHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(protoHandle, iteratorMethodHandle);
    }

    private static ObjectHandle GetIteratorPrototypeHandle(IBuiltinContext ctx)
    {
        var iteratorConstructorHandle = ctx.MaterializeIteratorConstructor();
        var iteratorConstructor = ctx.Heap.GetObject(iteratorConstructorHandle);
        if (ctx.TryGetPropertyValue(iteratorConstructor, JsValue.FromObject(iteratorConstructorHandle), "prototype", out var prototype) &&
            prototype.Tag == JsValueTag.Object)
        {
            return prototype.AsObjectHandle();
        }

        throw new InvalidOperationException("Iterator constructor prototype is not available.");
    }

    private static JsValue StringIteratorNext(IBuiltinContext ctx, JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            ctx.Heap.GetObject(thisValue.AsObjectHandle()) is not StringIteratorInstance iterator)
        {
            throw new JsThrownException(ctx.CreateTypeError("StringIterator.prototype.next called on incompatible receiver."));
        }

        if (iterator.Index >= iterator.Value.Length)
        {
            return CreateIteratorResult(ctx, JsValue.Undefined, done: true);
        }

        var start = iterator.Index;
        var width = 1;
        if (char.IsHighSurrogate(iterator.Value[start]) &&
            start + 1 < iterator.Value.Length &&
            char.IsLowSurrogate(iterator.Value[start + 1]))
        {
            width = 2;
        }

        iterator.Index += width;
        return CreateIteratorResult(ctx, JsValue.FromString(iterator.Value.Substring(start, width)), done: false);
    }

    private static JsValue CreateIteratorResult(IBuiltinContext ctx, JsValue value, bool done)
    {
        var result = new JsObject();
        result.SetPrototype(ctx.GetObjectPrototype());
        _ = result.DefineOwnProperty("value", new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
        _ = result.DefineOwnProperty("done", new JsPropertyDescriptor(JsValue.FromBoolean(done), Writable: true, Enumerable: true, Configurable: true));
        return JsValue.FromObject(ctx.Heap.AllocateObject(result, AllocationSite.Current()));
    }

    private sealed class StringIteratorInstance : JsObject
    {
        public StringIteratorInstance(string value)
        {
            Value = value;
        }

        public string Value { get; }
        public int Index { get; set; }
    }

    private static JsValue CharAt(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var pos = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        return pos < 0 || pos >= s.Length ? JsValue.FromString(string.Empty) : JsValue.FromString(s[pos].ToString());
    }

    private static JsValue CharCodeAt(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var pos = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        return pos < 0 || pos >= s.Length ? JsValue.FromNumber(double.NaN) : JsValue.FromNumber(s[pos]);
    }

    private static JsValue CodePointAt(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var pos = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        if (pos < 0 || pos >= s.Length) return JsValue.Undefined;
        var high = s[pos];
        if (char.IsHighSurrogate(high) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]))
            return JsValue.FromNumber(char.ConvertToUtf32(high, s[pos + 1]));
        return JsValue.FromNumber(high);
    }

    private static JsValue At(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var raw = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        var idx = raw < 0 ? s.Length + raw : raw;
        return idx < 0 || idx >= s.Length ? JsValue.Undefined : JsValue.FromString(s[idx].ToString());
    }

    private static JsValue IndexOf(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var search = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? Math.Clamp((int)ctx.ToNumber(args[1]), 0, s.Length) : 0;
        return JsValue.FromNumber(s.IndexOf(search, from, StringComparison.Ordinal));
    }

    private static JsValue LastIndexOf(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var search = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Min(s.Length, Math.Max(0, (int)ctx.ToNumber(args[1])) + search.Length) : s.Length;
        if (search.Length == 0) return JsValue.FromNumber(from);
        return JsValue.FromNumber(s[..from].LastIndexOf(search, StringComparison.Ordinal));
    }

    private static JsValue Includes(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var search = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? Math.Clamp((int)ctx.ToNumber(args[1]), 0, s.Length) : 0;
        return JsValue.FromBoolean(s.IndexOf(search, from, StringComparison.Ordinal) >= 0);
    }

    private static JsValue StartsWith(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var search = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var from = args.Count > 1 ? Math.Clamp((int)ctx.ToNumber(args[1]), 0, s.Length) : 0;
        if (from + search.Length > s.Length) return JsValue.FromBoolean(false);
        return JsValue.FromBoolean(s.AsSpan(from, search.Length).SequenceEqual(search.AsSpan()));
    }

    private static JsValue EndsWith(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var search = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var endPos = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Clamp((int)ctx.ToNumber(args[1]), 0, s.Length) : s.Length;
        var start = endPos - search.Length;
        if (start < 0) return JsValue.FromBoolean(false);
        return JsValue.FromBoolean(s.AsSpan(start, search.Length).SequenceEqual(search.AsSpan()));
    }

    private static JsValue Slice(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? WrapNegative((int)ctx.ToNumber(args[0]), len) : 0;
        var end = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? WrapNegative((int)ctx.ToNumber(args[1]), len) : len;
        return start >= end ? JsValue.FromString(string.Empty) : JsValue.FromString(s[start..end]);
    }

    private static JsValue Substring(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? Math.Clamp((int)ctx.ToNumber(args[0]), 0, len) : 0;
        var end = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? Math.Clamp((int)ctx.ToNumber(args[1]), 0, len) : len;
        if (start > end) (start, end) = (end, start);
        return JsValue.FromString(s[start..end]);
    }

    private static JsValue Substr(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var len = s.Length;
        var start = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        if (start < 0) start = Math.Max(0, len + start);
        start = Math.Min(start, len);
        var count = args.Count > 1 && args[1].Tag != JsValueTag.Undefined
            ? Math.Max(0, Math.Min(len - start, (int)ctx.ToNumber(args[1]))) : len - start;
        return JsValue.FromString(s.Substring(start, count));
    }

    private static JsValue Concat(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var sb = new System.Text.StringBuilder(RequireString(ctx, thisValue));
        for (var i = 0; i < args.Count; i++) sb.Append(ctx.ToStringValue(args[i]));
        return JsValue.FromString(sb.ToString());
    }

    private static JsValue Repeat(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var n = args.Count > 0 ? ctx.ToNumber(args[0]) : 0;
        if (double.IsNaN(n) || n < 0 || double.IsInfinity(n))
            throw new JsThrownException(ctx.CreateRangeError("Invalid repeat count."));
        var count = (int)n;
        if (count == 0 || s.Length == 0) return JsValue.FromString(string.Empty);
        var sb = new System.Text.StringBuilder(s.Length * count);
        for (var i = 0; i < count; i++) sb.Append(s);
        return JsValue.FromString(sb.ToString());
    }

    private static JsValue PadStart(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var targetLen = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        if (targetLen <= s.Length) return JsValue.FromString(s);
        var pad = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? ctx.ToStringValue(args[1]) : " ";
        if (pad.Length == 0) return JsValue.FromString(s);
        return JsValue.FromString(BuildPadding(pad, targetLen - s.Length) + s);
    }

    private static JsValue PadEnd(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var targetLen = args.Count > 0 ? (int)ctx.ToNumber(args[0]) : 0;
        if (targetLen <= s.Length) return JsValue.FromString(s);
        var pad = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? ctx.ToStringValue(args[1]) : " ";
        if (pad.Length == 0) return JsValue.FromString(s);
        return JsValue.FromString(s + BuildPadding(pad, targetLen - s.Length));
    }

    private static JsValue Split(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var limit = args.Count > 1 && args[1].Tag != JsValueTag.Undefined ? Math.Max(0, (int)ctx.ToNumber(args[1])) : int.MaxValue;
        var items = new List<JsValue>();
        if (limit == 0) return CreateArrayResult(ctx, items);
        if (args.Count == 0 || args[0].Tag == JsValueTag.Undefined) { items.Add(JsValue.FromString(s)); return CreateArrayResult(ctx, items); }

        if (TryDispatchToSymbolMethod(ctx, args[0], "split", JsValue.FromString(s), args.Count > 1 ? args[1] : JsValue.Undefined, out var dispatched))
            return dispatched;

        var sep = ctx.ToStringValue(args[0]);
        if (sep.Length == 0)
        {
            for (var i = 0; i < s.Length && items.Count < limit; i++)
                items.Add(JsValue.FromString(s[i].ToString()));
            return CreateArrayResult(ctx, items);
        }
        var start = 0;
        while (start <= s.Length && items.Count < limit)
        {
            var idx = s.IndexOf(sep, start, StringComparison.Ordinal);
            if (idx < 0) break;
            items.Add(JsValue.FromString(s[start..idx]));
            start = idx + sep.Length;
        }
        if (items.Count < limit)
            items.Add(JsValue.FromString(s[start..]));
        return CreateArrayResult(ctx, items);
    }

    private static JsValue Replace(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        if (args.Count < 2) return JsValue.FromString(s);

        if (TryDispatchToSymbolMethod(ctx, args[0], "replace", JsValue.FromString(s), args[1], out var dispatched))
            return dispatched;

        var search = ctx.ToStringValue(args[0]);
        var idx = s.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0) return JsValue.FromString(s);
        var replacement = ResolveReplacement(ctx, args[1], s, idx, search);
        return JsValue.FromString(string.Concat(s[..idx], replacement, s[(idx + search.Length)..]));
    }

    private static JsValue ReplaceAll(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        if (args.Count < 2) return JsValue.FromString(s);

        // 22.1.3.20 step 2.b: a RegExp searchValue must carry the global flag.
        if (args[0].Tag == JsValueTag.Object &&
            ctx.Heap.GetObject(args[0].AsObjectHandle()) is RegExpObject &&
            ctx.TryGetPropertyValue(ctx.Heap.GetObject(args[0].AsObjectHandle()), args[0], "flags", out var flagsValue) &&
            ctx.ToStringValue(flagsValue).IndexOf('g') < 0)
        {
            throw new JsThrownException(ctx.CreateTypeError(
                "String.prototype.replaceAll called with a non-global RegExp argument."));
        }

        if (TryDispatchToSymbolMethod(ctx, args[0], "replace", JsValue.FromString(s), args[1], out var dispatched))
            return dispatched;

        var search = ctx.ToStringValue(args[0]);
        if (search.Length == 0) return JsValue.FromString(s);
        var replacement = args[1];
        var sb = new System.Text.StringBuilder();
        var start = 0;
        while (start <= s.Length)
        {
            var idx = s.IndexOf(search, start, StringComparison.Ordinal);
            if (idx < 0) break;
            sb.Append(s[start..idx]);
            sb.Append(ResolveReplacement(ctx, replacement, s, idx, search));
            start = idx + search.Length;
        }
        sb.Append(s[start..]);
        return JsValue.FromString(sb.ToString());
    }

    private static JsValue Match(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var regexp = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (TryDispatchToSymbolMethod(ctx, regexp, "match", JsValue.FromString(s), out var dispatched))
            return dispatched;

        if (regexp.Tag == JsValueTag.Object &&
            ctx.Heap.GetObject(regexp.AsObjectHandle()) is RegExpObject regExpObject)
        {
            return BuildMatchResultArray(ctx, s, regExpObject.Regex.Match(s));
        }

        var pattern = (args.Count == 0 || regexp.Tag == JsValueTag.Undefined)
            ? string.Empty
            : ToStringForRegExpPattern(ctx, regexp);
        // Cap backtracking: an arbitrary user pattern run through the BCL engine can
        // backtrack catastrophically and wedge the thread. The per-test timeout
        // abandons (does not kill) the worker thread, so an unbounded native regex
        // is exactly what blocks the whole suite. A 250 ms match timeout matches the
        // compiled-RegExp path and turns a hang into a catchable failure.
        var match = BclRegex.Match(s, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
        return BuildMatchResultArray(ctx, s, match);
    }

    private static JsValue MatchAll(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var regexp = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (TryDispatchToSymbolMethod(ctx, regexp, "matchAll", JsValue.FromString(s), out var dispatched))
            return dispatched;
        return CreateArrayResult(ctx, new List<JsValue>());
    }

    private static JsValue Search(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var regexp = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (TryDispatchToSymbolMethod(ctx, regexp, "search", JsValue.FromString(s), out var dispatched))
            return dispatched;

        var needle = args.Count > 0 ? ctx.ToStringValue(args[0]) : "undefined";
        var idx = s.IndexOf(needle, StringComparison.Ordinal);
        return JsValue.FromNumber(idx);
    }

    private static bool TryDispatchToSymbolMethod(
        IBuiltinContext ctx,
        JsValue receiver,
        string symbolName)
    {
        return TryGetSymbolMethod(ctx, receiver, symbolName, out _);
    }

    private static bool TryDispatchToSymbolMethod(
        IBuiltinContext ctx,
        JsValue receiver,
        string symbolName,
        JsValue firstArg,
        out JsValue result)
    {
        result = JsValue.Undefined;
        if (!TryGetSymbolMethod(ctx, receiver, symbolName, out var method))
            return false;
        result = ctx.CallFunction(method, new[] { firstArg }, receiver);
        return true;
    }

    private static bool TryDispatchToSymbolMethod(
        IBuiltinContext ctx,
        JsValue receiver,
        string symbolName,
        JsValue firstArg,
        JsValue secondArg,
        out JsValue result)
    {
        result = JsValue.Undefined;
        if (!TryGetSymbolMethod(ctx, receiver, symbolName, out var method))
            return false;
        result = ctx.CallFunction(method, new[] { firstArg, secondArg }, receiver);
        return true;
    }

    private static bool TryGetSymbolMethod(
        IBuiltinContext ctx,
        JsValue receiver,
        string symbolName,
        out JsValue method)
    {
        method = JsValue.Undefined;
        if (receiver.Tag != JsValueTag.Object)
        {
            return false;
        }

        var symbol = ctx.CreateWellKnownSymbol(symbolName);
        if (symbol.Tag != JsValueTag.Symbol)
        {
            return false;
        }

        var obj = ctx.Heap.GetObject(receiver.AsObjectHandle());
        if (!obj.TryGetSymbolProperty(symbol.AsSymbolId(), h => ctx.Heap.GetObject(h), out var desc))
        {
            return false;
        }

        if (desc.IsAccessor)
        {
            if (desc.Get.Tag == JsValueTag.Undefined)
            {
                return false;
            }

            if (!IsCallable(ctx, desc.Get))
            {
                throw new JsThrownException(ctx.CreateTypeError("@@ method getter is not callable."));
            }

            method = ctx.CallFunction(desc.Get, Array.Empty<JsValue>(), receiver);
        }
        else
        {
            method = desc.Value;
        }

        if (method.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            method = JsValue.Undefined;
            return false;
        }

        if (!IsCallable(ctx, method))
        {
            throw new JsThrownException(ctx.CreateTypeError("@@ method is not callable."));
        }

        return true;
    }

    private static bool IsCallable(IBuiltinContext ctx, JsValue value)
    {
        return value.Tag == JsValueTag.Object &&
               ctx.Heap.GetObject(value.AsObjectHandle()) is JsFunctionObject or NativeFunctionObject or BoundFunctionObject;
    }

    private static JsValue BuildMatchResultArray(IBuiltinContext ctx, string input, Match match)
    {
        if (!match.Success)
        {
            return JsValue.Null;
        }

        var values = new List<JsValue>(match.Groups.Count);
        for (var i = 0; i < match.Groups.Count; i++)
        {
            var group = match.Groups[i];
            values.Add(group.Success ? JsValue.FromString(group.Value) : JsValue.Undefined);
        }

        var result = new ArrayObject();
        result.SetPrototype(ctx.GetArrayPrototype());
        for (var i = 0; i < values.Count; i++)
        {
            _ = result.DefineOwnProperty(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new JsPropertyDescriptor(values[i], Writable: true, Enumerable: true, Configurable: true));
        }

        _ = result.DefineOwnProperty(
            "index",
            new JsPropertyDescriptor(JsValue.FromNumber(match.Index), Writable: true, Enumerable: true, Configurable: true));
        _ = result.DefineOwnProperty(
            "input",
            new JsPropertyDescriptor(JsValue.FromString(input), Writable: true, Enumerable: true, Configurable: true));
        _ = result.DefineOwnProperty(
            "length",
            new JsPropertyDescriptor(JsValue.FromNumber(values.Count), Writable: true, Enumerable: false, Configurable: false));

        return JsValue.FromObject(ctx.Heap.AllocateObject(result, AllocationSite.Current()));
    }

    private static string ToStringForRegExpPattern(IBuiltinContext ctx, JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return ctx.ToStringValue(value);
        }

        var obj = ctx.Heap.GetObject(value.AsObjectHandle());
        if (TryCallPrimitiveMethod(ctx, obj, value, "toString", out var primitive))
        {
            return ctx.ToStringValue(primitive);
        }

        if (TryCallPrimitiveMethod(ctx, obj, value, "valueOf", out primitive))
        {
            return ctx.ToStringValue(primitive);
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert object to primitive value."));
    }

    private static bool TryCallPrimitiveMethod(
        IBuiltinContext ctx,
        JsObject obj,
        JsValue thisValue,
        string methodName,
        out JsValue primitive)
    {
        primitive = JsValue.Undefined;
        if (!ctx.TryGetPropertyValue(obj, thisValue, methodName, out var method))
        {
            return false;
        }

        if (!IsCallable(ctx, method))
        {
            return false;
        }

        var result = ctx.CallFunction(method, Array.Empty<JsValue>(), thisValue);
        if (result.Tag == JsValueTag.Object)
        {
            return false;
        }

        primitive = result;
        return true;
    }

    private static string ResolveReplacement(IBuiltinContext ctx, JsValue replacement, string source, int matchStart, string matched)
    {
        if (replacement.Tag == JsValueTag.Object)
        {
            var obj = ctx.Heap.GetObject(replacement.AsObjectHandle());
            if (obj is JsFunctionObject or NativeFunctionObject)
            {
                var result = ctx.CallFunction(replacement,
                    new JsValue[] { JsValue.FromString(matched), JsValue.FromNumber(matchStart), JsValue.FromString(source) },
                    JsValue.Undefined);
                return ctx.ToStringValue(result);
            }
        }

        // ECMA-262 22.1.3.18.1 GetSubstitution — the $-pattern grammar in a string
        // replacement. Numbered captures ($n) are RegExp-only and never occur on this
        // string-search path, so a $n is left literal.
        var template = ctx.ToStringValue(replacement);
        if (template.IndexOf('$') < 0)
        {
            return template;
        }

        var sb = new System.Text.StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '$' || i + 1 >= template.Length)
            {
                sb.Append(template[i]);
                continue;
            }

            switch (template[i + 1])
            {
                case '$': sb.Append('$'); i++; break;
                case '&': sb.Append(matched); i++; break;
                case '`': sb.Append(source, 0, matchStart); i++; break;
                case '\'': sb.Append(source, matchStart + matched.Length, source.Length - matchStart - matched.Length); i++; break;
                default: sb.Append('$'); break;
            }
        }

        return sb.ToString();
    }

    private static JsValue Normalize(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var s = RequireString(ctx, thisValue);
        var form = args.Count > 0 && args[0].Tag != JsValueTag.Undefined ? ctx.ToStringValue(args[0]) : "NFC";
        return form switch
        {
            "NFC" => JsValue.FromString(s.Normalize(System.Text.NormalizationForm.FormC)),
            "NFD" => JsValue.FromString(s.Normalize(System.Text.NormalizationForm.FormD)),
            "NFKC" => JsValue.FromString(s.Normalize(System.Text.NormalizationForm.FormKC)),
            "NFKD" => JsValue.FromString(s.Normalize(System.Text.NormalizationForm.FormKD)),
            _ => throw new JsThrownException(ctx.CreateRangeError($"Invalid normalization form: {form}."))
        };
    }

    private static JsValue CreateArrayResult(IBuiltinContext ctx, List<JsValue> items)
    {
        var arr = new ArrayObject();
        arr.SetPrototype(ctx.GetArrayPrototype());
        for (var i = 0; i < items.Count; i++)
            arr.SetProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture), items[i]);
        _ = arr.DefineOwnProperty("length", new JsPropertyDescriptor(JsValue.FromNumber(items.Count), Writable: true, Enumerable: false, Configurable: false));
        return JsValue.FromObject(ctx.Heap.AllocateObject(arr, AllocationSite.Current()));
    }

    private static string BuildPadding(string fill, int needed)
    {
        var sb = new System.Text.StringBuilder(needed);
        while (sb.Length < needed) { var remaining = needed - sb.Length; sb.Append(remaining >= fill.Length ? fill : fill[..remaining]); }
        return sb.ToString();
    }
}
