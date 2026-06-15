"""Apply Intl fixes - clean approach"""
import sys

def fix_intl_file():
    FILE = "FenBrowser.Js/Interpreter/BytecodeInterpreter.Intl.cs"
    with open(FILE, "r", encoding="utf-8") as f:
        content = f.read()

    print(f"Processing {FILE} ({len(content)} chars)")
    changes = 0

    # === FIX 1: CollatorConstruct → shared prototype + options validation ===
    old = """    // ECMA-402 10.1.1 InitializeCollator.
    private JsValue CollatorConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
        var culture = IntlDateTimeFormatting.ResolveCulture(locale);

        var prototype = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        var protoMethod = new NativeFunctionObject(
            "compare",
            (_, cmpArgs) =>
            {
                var a = cmpArgs.Count > 0 ? ToStringValue(cmpArgs[0]) : string.Empty;
                var b = cmpArgs.Count > 1 ? ToStringValue(cmpArgs[1]) : string.Empty;
                var result = culture.CompareInfo.Compare(a, b, System.Globalization.CompareOptions.None);
                return JsValue.FromNumber(result);
            },
            length: 2);
        var protoMethodHandle = _heap.AllocateObject(protoMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("compare",
            new JsPropertyDescriptor(JsValue.FromObject(protoMethodHandle),
                Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, protoMethodHandle);

        var resolvedOptsMethod = new NativeFunctionObject("resolvedOptions", (_, _2) =>
        {
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(string.IsNullOrEmpty(locale) ? "en-US" : locale), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("usage", new JsPropertyDescriptor(JsValue.FromString("sort"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("sensitivity", new JsPropertyDescriptor(JsValue.FromString("variant"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("ignorePunctuation", new JsPropertyDescriptor(JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("collation", new JsPropertyDescriptor(JsValue.FromString("default"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("numeric", new JsPropertyDescriptor(JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("caseFirst", new JsPropertyDescriptor(JsValue.FromString("false"), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        var resolvedOptsHandle = _heap.AllocateObject(resolvedOptsMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("resolvedOptions", new JsPropertyDescriptor(JsValue.FromObject(resolvedOptsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, resolvedOptsHandle);

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);
        return JsValue.FromObject(instanceHandle);
    }"""

    new = """    // ECMA-402 10.1.1 InitializeCollator.
    private JsValue CollatorConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
        var culture = IntlDateTimeFormatting.ResolveCulture(locale);
        var optionsValue = args.Count > 1 ? args[1] : JsValue.Undefined;
        string usage = "sort", sensitivity = "variant", caseFirst = "false", collation = "default";
        bool ignorePunctuation = false, numeric = false;

        if (optionsValue.Tag == JsValueTag.Null)
            throw new JsThrownException(CreateTypeError("Cannot convert null to object."));

        if (optionsValue.Tag != JsValueTag.Undefined)
        {
            var obj = ToObject(optionsValue);
            var recv = optionsValue.Tag == JsValueTag.Object ? optionsValue : JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
            string? G(string n) => TryGetPropertyValue(obj, recv, n, out var v) && v.Tag != JsValueTag.Undefined ? ToStringValue(v) : null;
            bool? B(string n) => TryGetPropertyValue(obj, recv, n, out var v) && v.Tag != JsValueTag.Undefined ? IsTruthy(v) : null;

            var lm = G("localeMatcher");
            if (lm is not null && lm is not "lookup" and not "best fit")
                throw new JsThrownException(CreateRangeError($"{lm} is an invalid localeMatcher option value"));

            usage = G("usage") ?? usage;
            if (usage is not "sort" and not "search")
                throw new JsThrownException(CreateRangeError($"{usage} is an invalid usage option value"));
            sensitivity = G("sensitivity") ?? sensitivity;
            if (sensitivity is not "base" and not "accent" and not "case" and not "variant")
                throw new JsThrownException(CreateRangeError($"{sensitivity} is an invalid sensitivity option value"));
            ignorePunctuation = B("ignorePunctuation") ?? ignorePunctuation;
            numeric = B("numeric") ?? numeric;
            caseFirst = G("caseFirst") ?? caseFirst;
            if (caseFirst is not "upper" and not "lower" and not "false")
                throw new JsThrownException(CreateRangeError($"{caseFirst} is an invalid caseFirst option value"));
            collation = G("collation") ?? collation;
            if (collation is not "default" and not "big5han" and not "compat" and not "dict" and not "direct" and not "ducet" and not "eor" and not "gb2312" and not "phonebk" and not "phonetic" and not "pinyin" and not "reformed" and not "searchjl" and not "stroke" and not "trad" and not "unihan" and not "zhuyin")
                throw new JsThrownException(CreateRangeError($"{collation} is an invalid collation option value"));
        }

        var lc = ExtractUnicodeKeyword(locale, "co");
        if (lc is not null) collation = lc;
        if (string.IsNullOrEmpty(locale)) locale = "en-US";

        var state = CreateOrdinaryObject();
        state.DefineOwnProperty("__locale", new JsPropertyDescriptor(JsValue.FromString(locale), Writable: false, Enumerable: false, Configurable: false));
        state.DefineOwnProperty("__usage", new JsPropertyDescriptor(JsValue.FromString(usage), Writable: false, Enumerable: false, Configurable: false));
        state.DefineOwnProperty("__sensitivity", new JsPropertyDescriptor(JsValue.FromString(sensitivity), Writable: false, Enumerable: false, Configurable: false));
        state.DefineOwnProperty("__ignorePunctuation", new JsPropertyDescriptor(JsValue.FromBoolean(ignorePunctuation), Writable: false, Enumerable: false, Configurable: false));
        state.DefineOwnProperty("__numeric", new JsPropertyDescriptor(JsValue.FromBoolean(numeric), Writable: false, Enumerable: false, Configurable: false));
        state.DefineOwnProperty("__caseFirst", new JsPropertyDescriptor(JsValue.FromString(caseFirst), Writable: false, Enumerable: false, Configurable: false));
        state.DefineOwnProperty("__collation", new JsPropertyDescriptor(JsValue.FromString(collation), Writable: false, Enumerable: false, Configurable: false));
        var stateHandle = _heap.AllocateObject(state, AllocationSite.Current());

        var protoHandle = EnsureCollatorPrototype();
        var inst = CreateOrdinaryObject();
        inst.SetPrototype(protoHandle);
        inst.DefineOwnProperty("__collatorState", new JsPropertyDescriptor(JsValue.FromObject(stateHandle), Writable: false, Enumerable: false, Configurable: false));
        var instHandle = _heap.AllocateObject(inst, AllocationSite.Current());
        _heap.WriteBarrier(instHandle, protoHandle);
        _heap.WriteBarrier(instHandle, stateHandle);
        return JsValue.FromObject(instHandle);
    }"""

    if old in content:
        content = content.replace(old, new)
        changes += 1; print("  [OK] CollatorConstruct")
    else:
        print("  [FAIL] CollatorConstruct NOT FOUND")

    # === FIX 2: ListFormatConstruct → shared proto + options ===
    old2 = """    private JsValue ListFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
        var state = ParseListFormatState(locale, args.Count > 1 ? args[1] : JsValue.Undefined);

        var prototype = CreateOrdinaryObject();
        var protoHandle = _heap.AllocateObject(prototype, AllocationSite.Current());
        _heap.PushRoot(protoHandle);

        var formatMethod = new NativeFunctionObject(
            "format",
            (_, fmtArgs) =>
            {
                var list = GetListFormatItems(fmtArgs.Count > 0 ? fmtArgs[0] : JsValue.Undefined);
                var parts = FormatListToParts(list, state);
                return JsValue.FromString(string.Concat(parts.Select(static p => p.Value)));
            },
            length: 1);
        var formatHandle = _heap.AllocateObject(formatMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("format",
            new JsPropertyDescriptor(JsValue.FromObject(formatHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, formatHandle);

        var formatToPartsMethod = new NativeFunctionObject(
            "formatToParts",
            (_, fmtArgs) =>
            {
                var list = GetListFormatItems(fmtArgs.Count > 0 ? fmtArgs[0] : JsValue.Undefined);
                return CreateIntlPartsArray(FormatListToParts(list, state));
            },
            length: 1);
        var formatToPartsHandle = _heap.AllocateObject(formatToPartsMethod, AllocationSite.Current());
        prototype.DefineOwnProperty("formatToParts",
            new JsPropertyDescriptor(JsValue.FromObject(formatToPartsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, formatToPartsHandle);

        var lfResOpts = new NativeFunctionObject("resolvedOptions", (_, _2) =>
        {
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(string.IsNullOrEmpty(state.Locale) ? "en-US" : state.Locale), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(state.Type), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("style", new JsPropertyDescriptor(JsValue.FromString(state.Style), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        var lfResHandle = _heap.AllocateObject(lfResOpts, AllocationSite.Current());
        prototype.DefineOwnProperty("resolvedOptions", new JsPropertyDescriptor(JsValue.FromObject(lfResHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(protoHandle, lfResHandle);

        var instance = CreateOrdinaryObject();
        instance.SetPrototype(protoHandle);
        var instanceHandle = _heap.AllocateObject(instance, AllocationSite.Current());
        _heap.WriteBarrier(instanceHandle, protoHandle);
        return JsValue.FromObject(instanceHandle);
    }"""

    new2 = """    private JsValue ListFormatConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : string.Empty;
        var optsVal = args.Count > 1 ? args[1] : JsValue.Undefined;
        var type = "conjunction";
        var style = "long";

        if (optsVal.Tag == JsValueTag.Null)
            throw new JsThrownException(CreateTypeError("Cannot convert null to object."));

        if (optsVal.Tag != JsValueTag.Undefined)
        {
            var opts = ToObject(optsVal);
            var recv = optsVal.Tag == JsValueTag.Object ? optsVal : JsValue.FromObject(_heap.AllocateObject(opts, AllocationSite.Current()));
            string? G(string n) => TryGetPropertyValue(opts, recv, n, out var v) && v.Tag != JsValueTag.Undefined ? ToStringValue(v) : null;

            var lm = G("localeMatcher");
            if (lm is not null && lm is not "lookup" and not "best fit")
                throw new JsThrownException(CreateRangeError($"{lm} is an invalid localeMatcher option value"));
            type = G("type") ?? type;
            if (type is not "conjunction" and not "disjunction" and not "unit")
                throw new JsThrownException(CreateRangeError($"{type} is an invalid type option value"));
            style = G("style") ?? style;
            if (style is not "long" and not "short" and not "narrow")
                throw new JsThrownException(CreateRangeError($"{style} is an invalid style option value"));
        }

        if (string.IsNullOrEmpty(locale)) locale = "en-US";
        var protoHandle = EnsureListFormatPrototype();
        var inst = CreateOrdinaryObject();
        inst.SetPrototype(protoHandle);
        inst.DefineOwnProperty("__listFormatState", new JsPropertyDescriptor(JsValue.FromString($"{locale}|{type}|{style}"), Writable: false, Enumerable: false, Configurable: false));
        var instHandle = _heap.AllocateObject(inst, AllocationSite.Current());
        _heap.WriteBarrier(instHandle, protoHandle);
        return JsValue.FromObject(instHandle);
    }"""

    if old2 in content:
        content = content.replace(old2, new2)
        changes += 1; print("  [OK] ListFormatConstruct")
    else:
        print("  [FAIL] ListFormatConstruct NOT FOUND")

    # === FIX 3: EnsureCollatorPrototype → instance state + toStringTag ===
    old3 = """    private ObjectHandle EnsureCollatorPrototype()
    {
        if (_collatorPrototypeHandle is { } existing)
            return existing;

        var proto = CreateOrdinaryObject();
        var ph = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(ph);

        var compareMethod = new NativeFunctionObject("compare", (thisValue, cmpArgs) =>
        {
            string localeStr = "en-US";
            if (thisValue.Tag == JsValueTag.Object)
            {
                var receiver = _heap.GetObject(thisValue.AsObjectHandle());
                if (receiver.TryGetProperty("__collator_locale", x => _heap.GetObject(x), out var locDesc) &&
                    locDesc.Value.Tag == JsValueTag.String)
                    localeStr = locDesc.Value.AsString();
            }
            var cult = IntlDateTimeFormatting.ResolveCulture(localeStr);
            var a = cmpArgs.Count > 0 ? ToStringValue(cmpArgs[0]) : string.Empty;
            var b = cmpArgs.Count > 1 ? ToStringValue(cmpArgs[1]) : string.Empty;
            var result = cult.CompareInfo.Compare(a, b, System.Globalization.CompareOptions.None);
            return JsValue.FromNumber(result);
        }, length: 2);
        var compareHandle = _heap.AllocateObject(compareMethod, AllocationSite.Current());
        _ = proto.DefineOwnProperty("compare",
            new JsPropertyDescriptor(JsValue.FromObject(compareHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(ph, compareHandle);

        var resolvedOptsMethod = new NativeFunctionObject("resolvedOptions", (thisValue, _2) =>
        {
            string localeStr = "en-US";
            if (thisValue.Tag == JsValueTag.Object)
            {
                var receiver = _heap.GetObject(thisValue.AsObjectHandle());
                if (receiver.TryGetProperty("__collator_locale", x => _heap.GetObject(x), out var locDesc) &&
                    locDesc.Value.Tag == JsValueTag.String)
                    localeStr = locDesc.Value.AsString();
            }
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(localeStr), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("usage", new JsPropertyDescriptor(JsValue.FromString("sort"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("sensitivity", new JsPropertyDescriptor(JsValue.FromString("variant"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("ignorePunctuation", new JsPropertyDescriptor(JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("collation", new JsPropertyDescriptor(JsValue.FromString("default"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("numeric", new JsPropertyDescriptor(JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("caseFirst", new JsPropertyDescriptor(JsValue.FromString("false"), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        var resolvedOptsHandle = _heap.AllocateObject(resolvedOptsMethod, AllocationSite.Current());
        _ = proto.DefineOwnProperty("resolvedOptions",
            new JsPropertyDescriptor(JsValue.FromObject(resolvedOptsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(ph, resolvedOptsHandle);

        _collatorPrototypeHandle = ph;
        return ph;
    }"""

    new3 = """    private ObjectHandle EnsureCollatorPrototype()
    {
        if (_collatorPrototypeHandle is { } existing)
            return existing;

        var proto = CreateOrdinaryObject();
        var ph = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(ph);

        var compareMethod = new NativeFunctionObject("compare", (thisValue, cmpArgs) =>
        {
            string localeStr = GetCollatorInstanceState(thisValue, "__locale") ?? "en-US";
            var cult = IntlDateTimeFormatting.ResolveCulture(localeStr);
            var a = cmpArgs.Count > 0 ? ToStringValue(cmpArgs[0]) : string.Empty;
            var b = cmpArgs.Count > 1 ? ToStringValue(cmpArgs[1]) : string.Empty;
            var result = cult.CompareInfo.Compare(a, b, System.Globalization.CompareOptions.None);
            return JsValue.FromNumber(result);
        }, length: 2);
        var compareHandle = _heap.AllocateObject(compareMethod, AllocationSite.Current());
        _ = proto.DefineOwnProperty("compare",
            new JsPropertyDescriptor(JsValue.FromObject(compareHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(ph, compareHandle);

        var resolvedOptsMethod = new NativeFunctionObject("resolvedOptions", (thisValue, _2) =>
        {
            string? S(string k) => GetCollatorInstanceState(thisValue, k);
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(S("__locale") ?? "en-US"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("usage", new JsPropertyDescriptor(JsValue.FromString(S("__usage") ?? "sort"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("sensitivity", new JsPropertyDescriptor(JsValue.FromString(S("__sensitivity") ?? "variant"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("ignorePunctuation", new JsPropertyDescriptor(JsValue.FromBoolean(S("__ignorePunctuation") == "True"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("collation", new JsPropertyDescriptor(JsValue.FromString(S("__collation") ?? "default"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("numeric", new JsPropertyDescriptor(JsValue.FromBoolean(S("__numeric") == "True"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("caseFirst", new JsPropertyDescriptor(JsValue.FromString(S("__caseFirst") ?? "false"), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        var resolvedOptsHandle = _heap.AllocateObject(resolvedOptsMethod, AllocationSite.Current());
        _ = proto.DefineOwnProperty("resolvedOptions",
            new JsPropertyDescriptor(JsValue.FromObject(resolvedOptsHandle), Writable: true, Enumerable: false, Configurable: true));
        _heap.WriteBarrier(ph, resolvedOptsHandle);

        DefineBuiltinToStringTag(proto, "Intl.Collator");

        _collatorPrototypeHandle = ph;
        return ph;
    }

    private string? GetCollatorInstanceState(JsValue thisValue, string key)
    {
        if (thisValue.Tag != JsValueTag.Object) return null;
        var recv = _heap.GetObject(thisValue.AsObjectHandle());
        if (!recv.TryGetProperty("__collatorState", x => _heap.GetObject(x), out var sd) || sd.Value.Tag != JsValueTag.Object)
            return null;
        var state = _heap.GetObject(sd.Value.AsObjectHandle());
        return state.TryGetOwnProperty(key, out var d) && d.Value.Tag == JsValueTag.String ? d.Value.AsString() : null;
    }"""

    if old3 in content:
        content = content.replace(old3, new3)
        changes += 1; print("  [OK] EnsureCollatorPrototype")
    else:
        print("  [FAIL] EnsureCollatorPrototype NOT FOUND")

    # === FIX 4: EnsureListFormatPrototype → instance state + toStringTag + GetListFormatInstanceState ===
    old4 = """    private ObjectHandle EnsureListFormatPrototype()
    {
        if (_listFormatPrototypeHandle is { } existing)
            return existing;

        var proto = CreateOrdinaryObject();
        var ph = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(ph);

        var formatFn = new NativeFunctionObject("format", (_, a) =>
        {
            var list = GetListFormatItems(a.Count > 0 ? a[0] : JsValue.Undefined);
            var parts = FormatListToParts(list, new ListFormatState("en", "conjunction", "long"));
            return JsValue.FromString(string.Concat(parts.Select(static p => p.Value)));
        }, length: 1);
        proto.DefineOwnProperty("format", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(formatFn, AllocationSite.Current())), Writable: true, Enumerable: false, Configurable: true));

        var formatToPartsFn = new NativeFunctionObject("formatToParts", (_, a) =>
        {
            var list = GetListFormatItems(a.Count > 0 ? a[0] : JsValue.Undefined);
            return CreateIntlPartsArray(FormatListToParts(list, new ListFormatState("en", "conjunction", "long")));
        }, length: 1);
        proto.DefineOwnProperty("formatToParts", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(formatToPartsFn, AllocationSite.Current())), Writable: true, Enumerable: false, Configurable: true));

        var resOptsFn = new NativeFunctionObject("resolvedOptions", (_, _2) =>
        {
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString("en"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString("conjunction"), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("style", new JsPropertyDescriptor(JsValue.FromString("long"), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        proto.DefineOwnProperty("resolvedOptions", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(resOptsFn, AllocationSite.Current())), Writable: true, Enumerable: false, Configurable: true));

        _listFormatPrototypeHandle = ph;
        return ph;
    }"""

    new4 = """    private ObjectHandle EnsureListFormatPrototype()
    {
        if (_listFormatPrototypeHandle is { } existing)
            return existing;

        var proto = CreateOrdinaryObject();
        var ph = _heap.AllocateObject(proto, AllocationSite.Current());
        _heap.PushRoot(ph);

        var formatFn = new NativeFunctionObject("format", (thisValue, a) =>
        {
            var state = GetListFormatInstanceState(thisValue);
            var list = GetListFormatItems(a.Count > 0 ? a[0] : JsValue.Undefined);
            var parts = FormatListToParts(list, state);
            return JsValue.FromString(string.Concat(parts.Select(static p => p.Value)));
        }, length: 1);
        proto.DefineOwnProperty("format", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(formatFn, AllocationSite.Current())), Writable: true, Enumerable: false, Configurable: true));

        var formatToPartsFn = new NativeFunctionObject("formatToParts", (thisValue, a) =>
        {
            var state = GetListFormatInstanceState(thisValue);
            var list = GetListFormatItems(a.Count > 0 ? a[0] : JsValue.Undefined);
            return CreateIntlPartsArray(FormatListToParts(list, state));
        }, length: 1);
        proto.DefineOwnProperty("formatToParts", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(formatToPartsFn, AllocationSite.Current())), Writable: true, Enumerable: false, Configurable: true));

        var resOptsFn = new NativeFunctionObject("resolvedOptions", (thisValue, _2) =>
        {
            var state = GetListFormatInstanceState(thisValue);
            var o = CreateOrdinaryObject();
            o.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(state.Locale), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(state.Type), Writable: true, Enumerable: true, Configurable: true));
            o.DefineOwnProperty("style", new JsPropertyDescriptor(JsValue.FromString(state.Style), Writable: true, Enumerable: true, Configurable: true));
            return JsValue.FromObject(_heap.AllocateObject(o, AllocationSite.Current()));
        }, length: 0);
        proto.DefineOwnProperty("resolvedOptions", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(resOptsFn, AllocationSite.Current())), Writable: true, Enumerable: false, Configurable: true));

        DefineBuiltinToStringTag(proto, "Intl.ListFormat");

        _listFormatPrototypeHandle = ph;
        return ph;
    }

    private ListFormatState GetListFormatInstanceState(JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.Object)
        {
            var recv = _heap.GetObject(thisValue.AsObjectHandle());
            if (recv.TryGetProperty("__listFormatState", x => _heap.GetObject(x), out var sd) && sd.Value.Tag == JsValueTag.String)
            {
                var parts = sd.Value.AsString().Split('|');
                if (parts.Length == 3)
                    return new ListFormatState(parts[0], parts[1], parts[2]);
            }
        }
        return new ListFormatState("en", "conjunction", "long");
    }"""

    if old4 in content:
        content = content.replace(old4, new4)
        changes += 1; print("  [OK] EnsureListFormatPrototype")
    else:
        print("  [FAIL] EnsureListFormatPrototype NOT FOUND")

    # === FIX 5: Remove dead ParseListFormatState ===
    old5 = """    private static ListFormatState ParseListFormatState(string locale, JsValue optionsValue)
    {
        string type = "conjunction";
        string style = "long";
        if (optionsValue.Tag == JsValueTag.Object)
        {
            // Parsing is intentionally shallow: DurationFormat helper paths only need type/style.
        }

        return new ListFormatState(locale, type, style);
    }"""
    if old5 in content:
        content = content.replace(old5, "")
        changes += 1; print("  [OK] ParseListFormatState removed")
    else:
        print("  [INFO] ParseListFormatState not found or already removed")

    # === FIX 6: toStringTag for Segmenter, DisplayNames, PluralRules ===
    for marker, tag_name in [
        ("        _segmenterPrototypeHandle = ph;\n        return ph;\n    }", "Intl.Segmenter"),
        ("        _displayNamesPrototypeHandle = ph;\n        return ph;\n    }", "Intl.DisplayNames"),
        ("        _pluralRulesPrototypeHandle = ph;\n        return ph;\n    }", "Intl.PluralRules"),
    ]:
        replacement = f"        DefineBuiltinToStringTag(proto, \"{tag_name}\");\n\n{marker}"
        if marker in content:
            content = content.replace(marker, replacement)
            changes += 1; print(f"  [OK] toStringTag for {tag_name}")
        else:
            print(f"  [INFO] toStringTag marker for {tag_name} not found")

    # === FIX 7: GetGraphemeClusterOffsets helper ===
    helper = """
    // Returns grapheme cluster boundary offsets using .NET's StringInfo.
    private static List<int> GetGraphemeClusterOffsets(string text)
    {
        var offsets = new List<int>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            offsets.Add(enumerator.ElementIndex);
        if (offsets.Count == 0)
            offsets.Add(0);
        return offsets;
    }
"""
    idx = content.rfind("\n}")
    if idx >= 0:
        content = content[:idx] + helper + "\n}" + content[idx+2:]
        changes += 1; print("  [OK] GetGraphemeClusterOffsets helper")

    # === FIX 8: Segmenter segment() with grapheme clusters + containing ===
    old8_start = """        var segmentFn = new NativeFunctionObject("segment", (_, a) =>"""
    if old8_start in content:
        # Find the full block
        start_idx = content.find(old8_start)
        # Find the matching closing for this lambda
        # This is the old code that reads single chars - replace with grapheme-cluster version
        # The block ends with the iterObj setup
        end_marker = """            return JsValue.FromObject(iterObjHandle);
        }, length: 1);"""
        end_idx = content.find(end_marker, start_idx)
        if start_idx > 0 and end_idx > start_idx:
            old_seg_fn = content[start_idx:end_idx + len(end_marker)]
            new_seg_fn = """        var segmentFn = new NativeFunctionObject("segment", (thisValue, a) =>
        {
            var str = a.Count > 0 ? ToStringValue(a[0]) : "";
            var boundaries = GetGraphemeClusterOffsets(str);
            var segments = CreateOrdinaryObject();
            segments.DefineOwnProperty("_str", new JsPropertyDescriptor(JsValue.FromString(str), Writable: false, Enumerable: false, Configurable: false));
            segments.DefineOwnProperty("_idx", new JsPropertyDescriptor(JsValue.FromNumber(0), Writable: true, Enumerable: false, Configurable: false));
            segments.DefineOwnProperty("_boundaries", new JsPropertyDescriptor(JsValue.FromString(string.Join(",", boundaries)), Writable: false, Enumerable: false, Configurable: false));

            var containingFn = new NativeFunctionObject("containing", (segThisValue, ca) =>
            {
                var segObj = _heap.GetObject(segThisValue.AsObjectHandle());
                int pos = ca.Count > 0 ? (int)ToNumber(ca[0]) : 0;
                string s = "";
                if (segObj.TryGetOwnProperty("_str", out var sd2)) s = sd2.Value.AsString();
                if (pos < 0) pos = 0;
                if (pos > s.Length) pos = s.Length;
                int segStart = 0, segEnd = s.Length;
                if (segObj.TryGetOwnProperty("_boundaries", out var bd))
                {
                    var bounds = bd.Value.AsString().Split(',').Where(x => x.Length > 0).Select(int.Parse).ToList();
                    for (int bi = 0; bi < bounds.Count; bi++)
                    {
                        if (bounds[bi] > pos) break;
                        segStart = bounds[bi];
                        segEnd = bi + 1 < bounds.Count ? bounds[bi + 1] : s.Length;
                    }
                }
                var result = CreateOrdinaryObject();
                result.DefineOwnProperty("segment", new JsPropertyDescriptor(JsValue.FromString(s[segStart..segEnd]), Writable: true, Enumerable: true, Configurable: true));
                result.DefineOwnProperty("index", new JsPropertyDescriptor(JsValue.FromNumber(segStart), Writable: true, Enumerable: true, Configurable: true));
                result.DefineOwnProperty("input", new JsPropertyDescriptor(JsValue.FromString(s), Writable: true, Enumerable: true, Configurable: true));
                return JsValue.FromObject(_heap.AllocateObject(result, AllocationSite.Current()));
            }, length: 1);
            segments.DefineOwnProperty("containing", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(containingFn, AllocationSite.Current())), Writable: true, Enumerable: false, Configurable: true));

            var iterFn = new NativeFunctionObject("next", (_, _2) =>
            {
                var segsObj = _heap.GetObject(_.AsObjectHandle())!;
                int idx = 0;
                if (segsObj.TryGetOwnProperty("_idx", out var idxDesc))
                    idx = (int)idxDesc.Value.AsNumber();
                string storedStr = "";
                if (segsObj.TryGetOwnProperty("_str", out var strDesc))
                    storedStr = strDesc.Value.AsString();
                if (idx >= storedStr.Length)
                {
                    var doneObj = CreateOrdinaryObject();
                    doneObj.DefineOwnProperty("done", new JsPropertyDescriptor(JsValue.FromBoolean(true), Writable: true, Enumerable: true, Configurable: true));
                    doneObj.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.Undefined, Writable: true, Enumerable: true, Configurable: true));
                    return JsValue.FromObject(_heap.AllocateObject(doneObj, AllocationSite.Current()));
                }
                int nextIdx = storedStr.Length;
                if (segsObj.TryGetOwnProperty("_boundaries", out var bd))
                {
                    var bounds = bd.Value.AsString().Split(',').Where(x => x.Length > 0).Select(int.Parse).ToList();
                    int bi = bounds.IndexOf(idx);
                    if (bi >= 0)
                        nextIdx = bi + 1 < bounds.Count ? bounds[bi + 1] : storedStr.Length;
                    else
                        nextIdx = idx + 1;
                }
                else
                {
                    nextIdx = idx + 1;
                }
                var seg = CreateOrdinaryObject();
                seg.DefineOwnProperty("segment", new JsPropertyDescriptor(JsValue.FromString(storedStr[idx..nextIdx]), Writable: true, Enumerable: true, Configurable: true));
                seg.DefineOwnProperty("index", new JsPropertyDescriptor(JsValue.FromNumber(idx), Writable: true, Enumerable: true, Configurable: true));
                seg.DefineOwnProperty("input", new JsPropertyDescriptor(JsValue.FromString(storedStr), Writable: true, Enumerable: true, Configurable: true));
                seg.DefineOwnProperty("isWordLike", new JsPropertyDescriptor(JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
                var iterResult = CreateOrdinaryObject();
                iterResult.DefineOwnProperty("value", new JsPropertyDescriptor(JsValue.FromObject(_heap.AllocateObject(seg, AllocationSite.Current())), Writable: true, Enumerable: true, Configurable: true));
                iterResult.DefineOwnProperty("done", new JsPropertyDescriptor(JsValue.FromBoolean(false), Writable: true, Enumerable: true, Configurable: true));
                segsObj.DefineOwnProperty("_idx", new JsPropertyDescriptor(JsValue.FromNumber(nextIdx), Writable: true, Enumerable: false, Configurable: false));
                return JsValue.FromObject(_heap.AllocateObject(iterResult, AllocationSite.Current()));
            }, length: 0);"""
            content = content.replace(old_seg_fn, new_seg_fn)
            changes += 1; print("  [OK] Segmenter segment() enhanced")
        else:
            print("  [FAIL] Segmenter segment() end marker NOT FOUND")
    else:
        print("  [FAIL] Segmenter segment() start NOT FOUND")

    # Write back
    with open(FILE, "w", encoding="utf-8") as f:
        f.write(content)
    print(f"\n{FILE}: {changes} changes applied")


def fix_main_file():
    FILE = "FenBrowser.Js/Interpreter/BytecodeInterpreter.cs"
    with open(FILE, "r", encoding="utf-8") as f:
        content = f.read()

    print(f"\nProcessing {FILE} ({len(content)} chars)")
    changes = 0

    # === FIX 9: PluralRulesConstruct options validation ===
    old = """    private JsValue PluralRulesConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : "en";
        var opts = args.Count > 1 && args[1].Tag == JsValueTag.Object ? _heap.GetObject(args[1].AsObjectHandle()) : null;
        string type = "cardinal";
        if (opts is not null)
        {
            if (TryGetPropertyValue(opts, args[1], "type", out var tv) && tv.Tag != JsValueTag.Undefined)
                type = ToStringValue(tv);
        }
        var stateObj = CreateOrdinaryObject();
        stateObj.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(locale), Writable: false, Enumerable: false, Configurable: false));
        stateObj.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(type), Writable: false, Enumerable: false, Configurable: false));"""

    new = """    private JsValue PluralRulesConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : "en";
        var optsVal = args.Count > 1 ? args[1] : JsValue.Undefined;
        string type = "cardinal";
        int minIntDigits = 1, minFracDigits = 0, maxFracDigits = 3;

        if (optsVal.Tag == JsValueTag.Null)
            throw new JsThrownException(CreateTypeError("Cannot convert null to object."));

        if (optsVal.Tag != JsValueTag.Undefined)
        {
            var opts = ToObject(optsVal);
            var recv = optsVal.Tag == JsValueTag.Object ? optsVal : JsValue.FromObject(_heap.AllocateObject(opts, AllocationSite.Current()));
            string? G(string n) => TryGetPropertyValue(opts, recv, n, out var v) && v.Tag != JsValueTag.Undefined ? ToStringValue(v) : null;
            int? I(string n) => TryGetPropertyValue(opts, recv, n, out var v) && v.Tag != JsValueTag.Undefined ? (int)ToNumber(v) : null;

            var lm = G("localeMatcher");
            if (lm is not null && lm is not "lookup" and not "best fit")
                throw new JsThrownException(CreateRangeError($"{lm} is an invalid localeMatcher option value"));

            type = G("type") ?? type;
            if (type is not "cardinal" and not "ordinal")
                throw new JsThrownException(CreateRangeError($"{type} is an invalid type option value"));

            minIntDigits = I("minimumIntegerDigits") ?? minIntDigits;
            if (minIntDigits < 1 || minIntDigits > 21)
                throw new JsThrownException(CreateRangeError($"minimumIntegerDigits must be 1-21, got {minIntDigits}"));
            minFracDigits = I("minimumFractionDigits") ?? minFracDigits;
            if (minFracDigits < 0 || minFracDigits > 100)
                throw new JsThrownException(CreateRangeError($"minimumFractionDigits out of range: {minFracDigits}"));
            maxFracDigits = I("maximumFractionDigits") ?? maxFracDigits;
            if (maxFracDigits < 0 || maxFracDigits > 100)
                throw new JsThrownException(CreateRangeError($"maximumFractionDigits out of range: {maxFracDigits}"));
        }

        var stateObj = CreateOrdinaryObject();
        stateObj.DefineOwnProperty("locale", new JsPropertyDescriptor(JsValue.FromString(locale), Writable: false, Enumerable: false, Configurable: false));
        stateObj.DefineOwnProperty("type", new JsPropertyDescriptor(JsValue.FromString(type), Writable: false, Enumerable: false, Configurable: false));
        stateObj.DefineOwnProperty("minimumIntegerDigits", new JsPropertyDescriptor(JsValue.FromNumber(minIntDigits), Writable: false, Enumerable: false, Configurable: false));
        stateObj.DefineOwnProperty("minimumFractionDigits", new JsPropertyDescriptor(JsValue.FromNumber(minFracDigits), Writable: false, Enumerable: false, Configurable: false));
        stateObj.DefineOwnProperty("maximumFractionDigits", new JsPropertyDescriptor(JsValue.FromNumber(maxFracDigits), Writable: false, Enumerable: false, Configurable: false));"""

    if old in content:
        content = content.replace(old, new)
        changes += 1; print("  [OK] PluralRulesConstruct")
    else:
        print("  [FAIL] PluralRulesConstruct NOT FOUND")

    # === FIX 10: SegmenterConstruct options validation ===
    old10 = """    private JsValue SegmenterConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : "en";
        var opts = args.Count > 1 && args[1].Tag == JsValueTag.Object ? _heap.GetObject(args[1].AsObjectHandle()) : null;
        string granularity = "grapheme";
        if (opts is not null)
        {
            if (TryGetPropertyValue(opts, args[1], "granularity", out var gv) && gv.Tag != JsValueTag.Undefined)
                granularity = ToStringValue(gv);
        }"""

    new10 = """    private JsValue SegmenterConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : "en";
        var optsVal = args.Count > 1 ? args[1] : JsValue.Undefined;
        string granularity = "grapheme";

        if (optsVal.Tag == JsValueTag.Null)
            throw new JsThrownException(CreateTypeError("Cannot convert null to object."));

        if (optsVal.Tag != JsValueTag.Undefined)
        {
            var opts = ToObject(optsVal);
            var recv = optsVal.Tag == JsValueTag.Object ? optsVal : JsValue.FromObject(_heap.AllocateObject(opts, AllocationSite.Current()));
            string? G(string n) => TryGetPropertyValue(opts, recv, n, out var v) && v.Tag != JsValueTag.Undefined ? ToStringValue(v) : null;

            var lm = G("localeMatcher");
            if (lm is not null && lm is not "lookup" and not "best fit")
                throw new JsThrownException(CreateRangeError($"{lm} is an invalid localeMatcher option value"));
            granularity = G("granularity") ?? granularity;
            if (granularity is not "grapheme" and not "word" and not "sentence")
                throw new JsThrownException(CreateRangeError($"{granularity} is an invalid granularity option value"));
        }"""

    if old10 in content:
        content = content.replace(old10, new10)
        changes += 1; print("  [OK] SegmenterConstruct")
    else:
        print("  [FAIL] SegmenterConstruct NOT FOUND")

    # === FIX 11: DisplayNamesConstruct options validation ===
    old11 = """    private JsValue DisplayNamesConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : "en";
        var opts = args.Count > 1 && args[1].Tag == JsValueTag.Object ? _heap.GetObject(args[1].AsObjectHandle()) : null;
        string style = "long", type = "language";
        if (opts is not null)
        {
            if (TryGetPropertyValue(opts, args[1], "style", out var sv) && sv.Tag != JsValueTag.Undefined) style = ToStringValue(sv);
            if (TryGetPropertyValue(opts, args[1], "type", out var tv) && tv.Tag != JsValueTag.Undefined) type = ToStringValue(tv);
        }"""

    new11 = """    private JsValue DisplayNamesConstruct(IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStringValue(args[0]) : "en";
        var optsVal = args.Count > 1 ? args[1] : JsValue.Undefined;
        string style = "long", type = "language", fallback = "code", languageDisplay = "dialect";

        if (optsVal.Tag == JsValueTag.Null)
            throw new JsThrownException(CreateTypeError("Cannot convert null to object."));

        if (optsVal.Tag != JsValueTag.Undefined)
        {
            var opts = ToObject(optsVal);
            var recv = optsVal.Tag == JsValueTag.Object ? optsVal : JsValue.FromObject(_heap.AllocateObject(opts, AllocationSite.Current()));
            string? G(string n) => TryGetPropertyValue(opts, recv, n, out var v) && v.Tag != JsValueTag.Undefined ? ToStringValue(v) : null;

            var lm = G("localeMatcher");
            if (lm is not null && lm is not "lookup" and not "best fit")
                throw new JsThrownException(CreateRangeError($"{lm} is an invalid localeMatcher option value"));
            style = G("style") ?? style;
            if (style is not "long" and not "short" and not "narrow")
                throw new JsThrownException(CreateRangeError($"{style} is an invalid style option value"));
            type = G("type") ?? type;
            if (type is not "language" and not "region" and not "script" and not "currency" and not "calendar" and not "dateTimeField")
                throw new JsThrownException(CreateRangeError($"{type} is an invalid type option value"));
            fallback = G("fallback") ?? fallback;
            if (fallback is not "code" and not "none")
                throw new JsThrownException(CreateRangeError($"{fallback} is an invalid fallback option value"));
            languageDisplay = G("languageDisplay") ?? languageDisplay;
            if (languageDisplay is not "dialect" and not "standard")
                throw new JsThrownException(CreateRangeError($"{languageDisplay} is an invalid languageDisplay option value"));
        }"""

    if old11 in content:
        content = content.replace(old11, new11)
        changes += 1; print("  [OK] DisplayNamesConstruct")
    else:
        print("  [FAIL] DisplayNamesConstruct NOT FOUND")

    # Write back
    with open(FILE, "w", encoding="utf-8") as f:
        f.write(content)
    print(f"\n{FILE}: {changes} changes applied")


if __name__ == "__main__":
    fix_intl_file()
    fix_main_file()
    print("\nAll done!")
