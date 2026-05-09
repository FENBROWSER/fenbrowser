using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Implements CSS Typed OM Level 1 (W3C Working Draft) and CSSOM View interfaces.
    ///
    /// Provides typed CSS value manipulation via the StylePropertyMap, CSSStyleValue,
    /// and related subclasses. This replaces string-based style manipulation (element.style.*)
    /// with typed objects for CSS values.
    ///
    /// Spec references:
    ///   CSS Typed OM Level 1: https://www.w3.org/TR/css-typed-om-1/
    ///   CSSOM View Module: https://www.w3.org/TR/cssom-view-1/
    ///
    /// Exposes:
    ///   element.attributeStyleMap → StylePropertyMap (inline styles)
    ///   element.computedStyleMap() → StylePropertyMap (computed styles, read-only)
    ///   CSSStyleValue.parse(property, value) → CSSStyleValue subclass
    ///   CSSStyleValue.parseAll(property, value) → CSSStyleValue[]
    /// </summary>
    public static class CssTypedOM
    {
        private static readonly HashSet<string> s_lengthUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "px", "em", "rem", "vw", "vh", "vmin", "vmax", "%",
            "cm", "mm", "in", "pt", "pc", "q",
            "ch", "ex", "lh", "rlh",
            "cqw", "cqh", "cqi", "cqb", "cqmin", "cqmax"
        };

        private static readonly HashSet<string> s_angleUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "deg", "grad", "rad", "turn"
        };

        private static readonly HashSet<string> s_timeUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "s", "ms"
        };

        private static readonly HashSet<string> s_frequencyUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hz", "khz"
        };

        private static readonly HashSet<string> s_resolutionUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dpi", "dpcm", "dppx"
        };

        private static readonly HashSet<string> s_flexUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fr"
        };

        /// <summary>
        /// Creates the StylePropertyMap constructor function.
        /// JavaScript: new StylePropertyMap() or new StylePropertyMap(init).
        /// </summary>
        public static FenFunction CreateStylePropertyMapConstructor(
            IExecutionContext context,
            Func<string, string> getStyle,
            Action<string, string> setStyle,
            bool isReadOnly = false)
        {
            var ctor = new FenFunction("StylePropertyMap", (args, thisVal) =>
            {
                var map = new FenObject();
                map.InternalClass = "StylePropertyMap";

                var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                map.NativeObject = entries;
                map.Set("__isReadOnly", FenValue.FromBoolean(isReadOnly));
                map.Set("__context", context != null ? FenValue.FromString(GetContextKey(context)) : FenValue.Undefined);
                map.Set("__getStyle", getStyle != null ? FenValue.FromFunction(MakeInternalGetter(getStyle)) : FenValue.Undefined);
                map.Set("__setStyle", setStyle != null && !isReadOnly ? FenValue.FromFunction(MakeInternalSetter(setStyle)) : FenValue.Undefined);

                if (args.Length > 0 && args[0].IsObject)
                {
                    var init = args[0].AsObject();
                    foreach (var key in init.Keys())
                    {
                        var val = init.Get(key).ToString();
                        entries[key] = val;
                        if (setStyle != null && !isReadOnly)
                            setStyle(key, val);
                    }
                }

                var sizeDescriptor = new PropertyDescriptor
                {
                    Getter = new FenFunction("get size", (_args, _this) =>
                    {
                        var mapObj = _this.AsObject();
                        var dict = GetStyleMapDictionary(mapObj);
                        if (dict != null)
                            return FenValue.FromNumber(dict.Count);
                        return FenValue.FromNumber(0);
                    }),
                    Enumerable = true,
                    Configurable = true
                };
                map.DefineOwnProperty("size", sizeDescriptor);

                map.Set("get", FenValue.FromFunction(new FenFunction("get", (gArgs, thisVal) =>
                {
                    if (gArgs.Length < 1)
                        throw new InvalidOperationException("StylePropertyMap.get requires a property name.");

                    var prop = gArgs[0].ToString();
                    var mapObj = thisVal.AsObject();
                    var dict = GetStyleMapDictionary(mapObj);
                    if (dict != null &&
                        dict.TryGetValue(prop, out var rawValue))
                    {
                        return FenValue.FromObject(ParseCssValue(prop, rawValue));
                    }

                    return FenValue.Undefined;
                })));

                map.Set("set", FenValue.FromFunction(new FenFunction("set", (sArgs, thisVal) =>
                {
                    var mapObj = thisVal.AsObject();
                    if (mapObj == null)
                        return FenValue.Undefined;

                    var isRo = mapObj.Get("__isReadOnly").ToBoolean();
                    if (isRo)
                        throw new InvalidOperationException("Cannot set on a read-only StylePropertyMap.");

                    if (sArgs.Length < 2)
                        throw new InvalidOperationException("StylePropertyMap.set requires a property name and value.");

                    var prop = sArgs[0].ToString();
                    var value = sArgs[1];
                    string serialized;

                    if (value.IsObject)
                    {
                        var cssValObj = value.AsObject();
                        serialized = SerializeCssStyleValue(cssValObj);
                    }
                    else
                    {
                        serialized = value.ToString();
                    }

                    var dict = GetStyleMapDictionary(mapObj);
                    if (dict != null)
                        dict[prop] = serialized;

                    var setStyleFn = mapObj.Get("__setStyle");
                    if (setStyleFn.IsFunction)
                        setStyleFn.AsFunction().Invoke(new[] { FenValue.FromString(prop), FenValue.FromString(serialized) }, null);

                    return FenValue.Undefined;
                })));

                map.Set("has", FenValue.FromFunction(new FenFunction("has", (hArgs, thisVal) =>
                {
                    if (hArgs.Length < 1)
                        return FenValue.FromBoolean(false);

                    var prop = hArgs[0].ToString();
                    var dict = GetStyleMapDictionary(thisVal.AsObject());
                    if (dict != null)
                        return FenValue.FromBoolean(dict.ContainsKey(prop));

                    return FenValue.FromBoolean(false);
                })));

                map.Set("delete", FenValue.FromFunction(new FenFunction("delete", (dArgs, thisVal) =>
                {
                    var mapObj = thisVal.AsObject();
                    if (mapObj == null)
                        return FenValue.Undefined;

                    var isRo = mapObj.Get("__isReadOnly").ToBoolean();
                    if (isRo)
                        throw new InvalidOperationException("Cannot delete from a read-only StylePropertyMap.");

                    if (dArgs.Length < 1)
                        return FenValue.Undefined;

                    var prop = dArgs[0].ToString();
                    var dict = GetStyleMapDictionary(mapObj);
                    if (dict != null)
                        dict.Remove(prop);

                    var setStyleFn = mapObj.Get("__setStyle");
                    if (setStyleFn.IsFunction)
                        setStyleFn.AsFunction().Invoke(new[] { FenValue.FromString(prop), FenValue.FromString("") }, null);

                    return FenValue.Undefined;
                })));

                map.Set("clear", FenValue.FromFunction(new FenFunction("clear", (cArgs, thisVal) =>
                {
                    var mapObj = thisVal.AsObject();
                    if (mapObj == null)
                        return FenValue.Undefined;

                    var isRo = mapObj.Get("__isReadOnly").ToBoolean();
                    if (isRo)
                        throw new InvalidOperationException("Cannot clear a read-only StylePropertyMap.");

                    var setStyleFn = mapObj.Get("__setStyle");
                    var dict = GetStyleMapDictionary(mapObj);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            if (setStyleFn.IsFunction)
                                setStyleFn.AsFunction().Invoke(new[] { FenValue.FromString(kvp.Key), FenValue.FromString("") }, null);
                        }
                        dict.Clear();
                    }

                    return FenValue.Undefined;
                })));

                return FenValue.FromObject(map);
            })
            {
                IsConstructor = true,
                NativeLength = 0
            };

            ctor.Set("parse", FenValue.FromFunction(new FenFunction("parse", (args, thisVal) =>
            {
                if (args.Length < 2)
                    throw new InvalidOperationException("CSSStyleValue.parse requires a property name and CSS string.");

                var property = args[0].ToString();
                var cssText = args[1].ToString();

                return FenValue.FromObject(ParseCssValue(property, cssText));
            })));

            ctor.Set("parseAll", FenValue.FromFunction(new FenFunction("parseAll", (args, thisVal) =>
            {
                if (args.Length < 2)
                    throw new InvalidOperationException("CSSStyleValue.parseAll requires a property name and CSS string.");

                var property = args[0].ToString();
                var cssText = args[1].ToString();

                var values = ParseCssValueList(property, cssText);
                var arr = FenObject.CreateArray();
                for (var i = 0; i < values.Count; i++)
                    arr.Set(i.ToString(), FenValue.FromObject(values[i]));

                return FenValue.FromObject(arr);
            })));

            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(ctor));
            ctor.Set("prototype", FenValue.FromObject(prototype));

            return ctor;
        }

        /// <summary>
        /// Creates StylePropertyMap instances for the Element prototype (attributeStyleMap, computedStyleMap).
        /// </summary>
        public static void InstallOnElementPrototype(
            FenObject elementPrototype,
            IExecutionContext context,
            Func<FenObject, string, string> getComputedStyle,
            Func<FenObject, string, string, bool> setInlineStyle,
            Func<FenObject, string, string> getInlineStyle)
        {
            var getAttributeStyleMap = new FenFunction("get attributeStyleMap", (args, thisVal) =>
            {
                var elementObj = thisVal.AsObject();
                if (elementObj == null)
                    return FenValue.Null;

                if (elementObj is not FenObject elementFenObject)
                    return FenValue.Null;

                var ctor = CreateStylePropertyMapConstructor(
                    context,
                    prop => getInlineStyle?.Invoke(elementFenObject, prop),
                    (prop, value) => setInlineStyle?.Invoke(elementFenObject, prop, value),
                    isReadOnly: false);

                return ctor.Invoke(Array.Empty<FenValue>(), context, FenValue.FromFunction(ctor));
            });

            var getComputedStyleMap = new FenFunction("computedStyleMap", (args, thisVal) =>
            {
                var elementObj = thisVal.AsObject();
                if (elementObj == null)
                    return FenValue.Null;

                if (elementObj is not FenObject elementFenObject)
                    return FenValue.Null;

                var ctor = CreateStylePropertyMapConstructor(
                    context,
                    prop => getComputedStyle?.Invoke(elementFenObject, prop),
                    null,
                    isReadOnly: true);

                return ctor.Invoke(Array.Empty<FenValue>(), context, FenValue.FromFunction(ctor));
            });

            elementPrototype.DefineOwnProperty("attributeStyleMap", new PropertyDescriptor
            {
                Getter = getAttributeStyleMap,
                Enumerable = true,
                Configurable = true
            });

            elementPrototype.Set("computedStyleMap", FenValue.FromFunction(getComputedStyleMap));
        }

        #region CSS Value Parsing

        private static FenObject ParseCssValue(string property, string cssText)
        {
            if (string.IsNullOrWhiteSpace(cssText))
                return CreateCssKeywordValue("unset");

            var trimmed = cssText.Trim();

            if (TryParseKeyword(trimmed, out var keywordObj))
                return keywordObj;

            if (TryParseUnitValue(trimmed, property, out var unitObj))
                return unitObj;

            if (TryParseMathFunction(trimmed, property, out var mathObj))
                return mathObj;

            if (TryParseColor(trimmed, out var colorObj))
                return colorObj;

            return CreateCssKeywordValue(trimmed);
        }

        private static List<FenObject> ParseCssValueList(string property, string cssText)
        {
            var result = new List<FenObject>();
            if (string.IsNullOrWhiteSpace(cssText))
                return result;

            var parts = SplitCssValues(cssText);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    result.Add(ParseCssValue(property, trimmed));
            }

            return result;
        }

        private static string[] SplitCssValues(string cssText)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;

            for (var i = 0; i < cssText.Length; i++)
            {
                var ch = cssText[i];
                switch (ch)
                {
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                    case ',':
                        if (depth == 0)
                        {
                            parts.Add(cssText.Substring(start, i - start));
                            start = i + 1;
                        }
                        break;
                    case ' ':
                        if (depth == 0 && parts.Count == 0)
                        {
                            var subParts = SplitSpaceSeparated(cssText);
                            return subParts.ToArray();
                        }
                        break;
                }
            }

            if (start < cssText.Length)
                parts.Add(cssText.Substring(start));

            return parts.ToArray();
        }

        private static List<string> SplitSpaceSeparated(string text)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                switch (ch)
                {
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                    default:
                        if (char.IsWhiteSpace(ch) && depth == 0)
                        {
                            if (i > start)
                                parts.Add(text.Substring(start, i - start));
                            start = i + 1;
                        }
                        break;
                }
            }

            if (start < text.Length)
                parts.Add(text.Substring(start));

            return parts;
        }

        #endregion

        #region CSS Keyword Value

        private static FenObject CreateCssKeywordValue(string keyword)
        {
            var obj = new FenObject();
            obj.InternalClass = "CSSKeywordValue";
            obj.Set("value", FenValue.FromString(keyword));
            obj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
                FenValue.FromString(keyword))));
            return obj;
        }

        private static bool TryParseKeyword(string cssText, out FenObject result)
        {
            result = null;
            var lower = cssText.ToLowerInvariant();

            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "initial", "inherit", "unset", "revert", "revert-layer",
                "auto", "none", "normal", "hidden", "visible",
                "solid", "dashed", "dotted", "double", "groove", "ridge", "inset", "outset",
                "block", "inline", "inline-block", "flex", "grid", "inline-flex", "inline-grid",
                "static", "relative", "absolute", "fixed", "sticky",
                "left", "right", "center", "top", "bottom",
                "bold", "italic", "underline", "overline", "line-through",
                "transparent", "currentcolor",
                "nowrap", "pre", "pre-wrap", "pre-line",
                "border-box", "content-box", "padding-box", "margin-box",
                "start", "end", "stretch", "baseline",
                "repeat", "repeat-x", "repeat-y", "no-repeat", "space", "round",
                "cover", "contain",
                "text-top", "text-bottom", "super", "sub",
                "optimizeSpeed", "optimizeLegibility", "geometricPrecision",
                "economy", "exact",
                "collapse", "separate",
                "ltr", "rtl",
                "uppercase", "lowercase", "capitalize",
                "alternate", "alternate-reverse", "forwards", "backwards", "both",
                "running", "paused",
                "clip", "ellipsis",
                "manual", "auto-phrase",
                "open-quote", "close-quote", "no-open-quote", "no-close-quote",
                "thin", "medium", "thick",
                "inside", "outside",
                "disc", "circle", "square", "decimal", "lower-roman", "upper-roman",
                "lower-alpha", "upper-alpha", "lower-greek", "lower-latin", "upper-latin",
                "armenian", "georgian",
                "landscape", "portrait",
                "cursive", "fantasy", "monospace", "sans-serif", "serif",
                "xx-small", "x-small", "small", "medium", "large", "x-large", "xx-large",
                "xxx-large", "smaller", "larger",
                "lighter", "bolder",
                "local", "scroll",
                "visibleFill", "visibleStroke", "visiblePainted",
                "nonzero", "evenodd",
                "butt", "round", "square",
                "miter", "bevel",
                "xMinYMin", "xMidYMin", "xMaxYMin", "xMinYMid", "xMidYMid",
                "xMaxYMid", "xMinYMax", "xMidYMax", "xMaxYMax",
            };

            if (keywords.Contains(lower))
            {
                result = CreateCssKeywordValue(lower);
                return true;
            }

            return false;
        }

        #endregion

        #region CSS Unit Value

        private static bool TryParseUnitValue(string cssText, string property, out FenObject result)
        {
            result = null;

            if (cssText == "0")
            {
                result = CreateCssUnitValue(0d, "number");
                return true;
            }

            foreach (var unit in s_lengthUnits)
            {
                if (cssText.EndsWith(unit, StringComparison.OrdinalIgnoreCase) &&
                    TryExtractNumber(cssText.Substring(0, cssText.Length - unit.Length), out var value))
                {
                    result = CreateCssUnitValue(value, unit.ToLowerInvariant());
                    return true;
                }
            }

            foreach (var unit in s_angleUnits)
            {
                if (cssText.EndsWith(unit, StringComparison.OrdinalIgnoreCase) &&
                    TryExtractNumber(cssText.Substring(0, cssText.Length - unit.Length), out var value))
                {
                    result = CreateCssUnitValue(value, unit.ToLowerInvariant());
                    return true;
                }
            }

            foreach (var unit in s_timeUnits)
            {
                if (cssText.EndsWith(unit, StringComparison.OrdinalIgnoreCase) &&
                    TryExtractNumber(cssText.Substring(0, cssText.Length - unit.Length), out var value))
                {
                    result = CreateCssUnitValue(value, unit.ToLowerInvariant());
                    return true;
                }
            }

            foreach (var unit in s_frequencyUnits)
            {
                if (cssText.EndsWith(unit, StringComparison.OrdinalIgnoreCase) &&
                    TryExtractNumber(cssText.Substring(0, cssText.Length - unit.Length), out var value))
                {
                    result = CreateCssUnitValue(value, unit.ToLowerInvariant());
                    return true;
                }
            }

            foreach (var unit in s_resolutionUnits)
            {
                if (cssText.EndsWith(unit, StringComparison.OrdinalIgnoreCase) &&
                    TryExtractNumber(cssText.Substring(0, cssText.Length - unit.Length), out var value))
                {
                    result = CreateCssUnitValue(value, unit.ToLowerInvariant());
                    return true;
                }
            }

            foreach (var unit in s_flexUnits)
            {
                if (cssText.EndsWith(unit, StringComparison.OrdinalIgnoreCase) &&
                    TryExtractNumber(cssText.Substring(0, cssText.Length - unit.Length), out var value))
                {
                    result = CreateCssUnitValue(value, unit.ToLowerInvariant());
                    return true;
                }
            }

            if (TryExtractNumber(cssText, out var rawNumber))
            {
                result = CreateCssUnitValue(rawNumber, "number");
                return true;
            }

            return false;
        }

        private static FenObject CreateCssUnitValue(double value, string unit)
        {
            var obj = new FenObject();
            obj.InternalClass = "CSSUnitValue";
            obj.Set("value", FenValue.FromNumber(value));
            obj.Set("unit", FenValue.FromString(unit));
            obj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                var objVal = thisVal.AsObject();
                var val = objVal?.Get("value").ToNumber() ?? 0;
                var u = objVal?.Get("unit").ToString() ?? "";
                if (u == "number")
                    return FenValue.FromString(val.ToString("G", CultureInfo.InvariantCulture));
                return FenValue.FromString(val.ToString("G", CultureInfo.InvariantCulture) + u);
            })));
            return obj;
        }

        private static bool TryExtractNumber(string text, out double value)
        {
            value = 0d;
            if (string.IsNullOrEmpty(text))
                return false;

            return double.TryParse(
                text.Trim(),
                NumberStyles.Float | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out value);
        }

        #endregion

        #region CSS Math Value

        private static bool TryParseMathFunction(string cssText, string property, out FenObject result)
        {
            result = null;

            if (TryParseNamedMath("calc", cssText, out result)) return true;
            if (TryParseNamedMath("min", cssText, out result)) return true;
            if (TryParseNamedMath("max", cssText, out result)) return true;
            if (TryParseNamedMath("clamp", cssText, out result)) return true;
            if (TryParseNamedMath("abs", cssText, out result)) return true;
            if (TryParseNamedMath("sign", cssText, out result)) return true;
            if (TryParseNamedMath("round", cssText, out result)) return true;
            if (TryParseNamedMath("mod", cssText, out result)) return true;
            if (TryParseNamedMath("rem", cssText, out result)) return true;
            if (TryParseNamedMath("sin", cssText, out result)) return true;
            if (TryParseNamedMath("cos", cssText, out result)) return true;
            if (TryParseNamedMath("tan", cssText, out result)) return true;
            if (TryParseNamedMath("asin", cssText, out result)) return true;
            if (TryParseNamedMath("acos", cssText, out result)) return true;
            if (TryParseNamedMath("atan", cssText, out result)) return true;
            if (TryParseNamedMath("atan2", cssText, out result)) return true;
            if (TryParseNamedMath("pow", cssText, out result)) return true;
            if (TryParseNamedMath("sqrt", cssText, out result)) return true;
            if (TryParseNamedMath("hypot", cssText, out result)) return true;
            if (TryParseNamedMath("log", cssText, out result)) return true;
            if (TryParseNamedMath("exp", cssText, out result)) return true;
            if (TryParseNamedMath("pi", cssText, out result)) return true;
            if (TryParseNamedMath("e", cssText, out result)) return true;
            if (TryParseNamedMath("infinity", cssText, out result)) return true;
            if (TryParseNamedMath("NaN", cssText, out result)) return true;

            return false;
        }

        private static bool TryParseNamedMath(string functionName, string cssText, out FenObject result)
        {
            result = null;
            var prefix = functionName + "(";
            if (!cssText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !cssText.EndsWith(")"))
                return false;

            var inner = cssText.Substring(prefix.Length, cssText.Length - prefix.Length - 1);
            var args = SplitCommaSeparatedTopLevel(inner);
            var operatorName = functionName.ToLowerInvariant();

            result = CreateCssMathValue(operatorName, args);
            return true;
        }

        private static List<string> SplitCommaSeparatedTopLevel(string text)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                switch (ch)
                {
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                    case ',':
                        if (depth == 0)
                        {
                            parts.Add(text.Substring(start, i - start).Trim());
                            start = i + 1;
                        }
                        break;
                }
            }

            if (start < text.Length)
                parts.Add(text.Substring(start).Trim());

            return parts;
        }

        private static FenObject CreateCssMathValue(string mathOperator, List<string> args)
        {
            var obj = new FenObject();
            obj.InternalClass = "CSSMathValue";

            var operatorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sum"] = "sum",
                ["product"] = "product",
                ["negate"] = "negate",
                ["invert"] = "invert",
                ["min"] = "min",
                ["max"] = "max",
                ["clamp"] = "clamp",
                ["calc"] = "sum",
                ["abs"] = "abs",
                ["sign"] = "sign",
                ["round"] = "round",
                ["mod"] = "mod",
                ["rem"] = "rem",
                ["sin"] = "sin",
                ["cos"] = "cos",
                ["tan"] = "tan",
                ["asin"] = "asin",
                ["acos"] = "acos",
                ["atan"] = "atan",
                ["atan2"] = "atan2",
                ["pow"] = "pow",
                ["sqrt"] = "sqrt",
                ["hypot"] = "hypot",
                ["log"] = "log",
                ["exp"] = "exp",
                ["pi"] = "pi",
                ["e"] = "e",
                ["infinity"] = "infinity",
                ["nan"] = "NaN",
            };

            obj.Set("operator", FenValue.FromString(
                operatorMap.TryGetValue(mathOperator, out var mapped) ? mapped : mathOperator));

            var valuesArray = FenObject.CreateArray();
            for (var i = 0; i < args.Count; i++)
            {
                var trimmed = args[i].Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                FenObject parsedValue;
                if (TryParseUnitValue(trimmed, string.Empty, out parsedValue) ||
                    TryParseKeyword(trimmed, out parsedValue) ||
                    TryParseMathFunction(trimmed, string.Empty, out parsedValue))
                {
                    valuesArray.Set(i.ToString(), FenValue.FromObject(parsedValue));
                }
                else
                {
                    var wrapped = CreateCssUnitValue(0d, "number");
                    if (TryExtractNumber(trimmed, out var numValue))
                        wrapped = CreateCssUnitValue(numValue, "number");
                    valuesArray.Set(i.ToString(), FenValue.FromObject(wrapped));
                }
            }
            valuesArray.Set("length", FenValue.FromNumber(args.Count));

            obj.Set("values", FenValue.FromObject(valuesArray));
            obj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (tArgs, thisVal) =>
            {
                var o = thisVal.AsObject();
                var op = o?.Get("operator").ToString() ?? "";
                var vals = o?.Get("values") ?? FenValue.Undefined;
                var parts = new List<string>();

                if (vals.IsObject)
                {
                    var valsObj = vals.AsObject();
                    var len = (int)(valsObj.Get("length").ToNumber());
                    for (var i = 0; i < len; i++)
                    {
                        var item = valsObj.Get(i.ToString());
                        if (item.IsObject)
                        {
                            var itemObj = item.AsObject();
                            var toStringFn = itemObj != null ? itemObj.Get("toString") : FenValue.Undefined;
                            parts.Add(toStringFn.IsFunction
                                ? toStringFn.AsFunction().Invoke(Array.Empty<FenValue>(), null, item).ToString()
                                : item.ToString());
                        }
                        else
                        {
                            parts.Add(item.ToString());
                        }
                    }
                }

                return FenValue.FromString(op + "(" + string.Join(", ", parts) + ")");
            })));

            return obj;
        }

        #endregion

        #region CSS Color (basic)

        private static bool TryParseColor(string cssText, out FenObject result)
        {
            result = null;
            var lower = cssText.ToLowerInvariant().Trim();

            var namedColors = new Dictionary<string, (byte r, byte g, byte b)>(StringComparer.OrdinalIgnoreCase)
            {
                ["aliceblue"] = (240, 248, 255),
                ["antiquewhite"] = (250, 235, 215),
                ["aqua"] = (0, 255, 255),
                ["aquamarine"] = (127, 255, 212),
                ["azure"] = (240, 255, 255),
                ["beige"] = (245, 245, 220),
                ["bisque"] = (255, 228, 196),
                ["black"] = (0, 0, 0),
                ["blanchedalmond"] = (255, 235, 205),
                ["blue"] = (0, 0, 255),
                ["blueviolet"] = (138, 43, 226),
                ["brown"] = (165, 42, 42),
                ["burlywood"] = (222, 184, 135),
                ["cadetblue"] = (95, 158, 160),
                ["chartreuse"] = (127, 255, 0),
                ["chocolate"] = (210, 105, 30),
                ["coral"] = (255, 127, 80),
                ["cornflowerblue"] = (100, 149, 237),
                ["cornsilk"] = (255, 248, 220),
                ["crimson"] = (220, 20, 60),
                ["cyan"] = (0, 255, 255),
                ["darkblue"] = (0, 0, 139),
                ["darkcyan"] = (0, 139, 139),
                ["darkgoldenrod"] = (184, 134, 11),
                ["darkgray"] = (169, 169, 169),
                ["darkgreen"] = (0, 100, 0),
                ["darkgrey"] = (169, 169, 169),
                ["darkkhaki"] = (189, 183, 107),
                ["darkmagenta"] = (139, 0, 139),
                ["darkolivegreen"] = (85, 107, 47),
                ["darkorange"] = (255, 140, 0),
                ["darkorchid"] = (153, 50, 204),
                ["darkred"] = (139, 0, 0),
                ["darksalmon"] = (233, 150, 122),
                ["darkseagreen"] = (143, 188, 139),
                ["darkslateblue"] = (72, 61, 139),
                ["darkslategray"] = (47, 79, 79),
                ["darkslategrey"] = (47, 79, 79),
                ["darkturquoise"] = (0, 206, 209),
                ["darkviolet"] = (148, 0, 211),
                ["deeppink"] = (255, 20, 147),
                ["deepskyblue"] = (0, 191, 255),
                ["dimgray"] = (105, 105, 105),
                ["dimgrey"] = (105, 105, 105),
                ["dodgerblue"] = (30, 144, 255),
                ["firebrick"] = (178, 34, 34),
                ["floralwhite"] = (255, 250, 240),
                ["forestgreen"] = (34, 139, 34),
                ["fuchsia"] = (255, 0, 255),
                ["gainsboro"] = (220, 220, 220),
                ["ghostwhite"] = (248, 248, 255),
                ["gold"] = (255, 215, 0),
                ["goldenrod"] = (218, 165, 32),
                ["gray"] = (128, 128, 128),
                ["green"] = (0, 128, 0),
                ["greenyellow"] = (173, 255, 47),
                ["grey"] = (128, 128, 128),
                ["honeydew"] = (240, 255, 240),
                ["hotpink"] = (255, 105, 180),
                ["indianred"] = (205, 92, 92),
                ["indigo"] = (75, 0, 130),
                ["ivory"] = (255, 255, 240),
                ["khaki"] = (240, 230, 140),
                ["lavender"] = (230, 230, 250),
                ["lavenderblush"] = (255, 240, 245),
                ["lawngreen"] = (124, 252, 0),
                ["lemonchiffon"] = (255, 250, 205),
                ["lightblue"] = (173, 216, 230),
                ["lightcoral"] = (240, 128, 128),
                ["lightcyan"] = (224, 255, 255),
                ["lightgoldenrodyellow"] = (250, 250, 210),
                ["lightgray"] = (211, 211, 211),
                ["lightgreen"] = (144, 238, 144),
                ["lightgrey"] = (211, 211, 211),
                ["lightpink"] = (255, 182, 193),
                ["lightsalmon"] = (255, 160, 122),
                ["lightseagreen"] = (32, 178, 170),
                ["lightskyblue"] = (135, 206, 250),
                ["lightslategray"] = (119, 136, 153),
                ["lightslategrey"] = (119, 136, 153),
                ["lightsteelblue"] = (176, 196, 222),
                ["lightyellow"] = (255, 255, 224),
                ["lime"] = (0, 255, 0),
                ["limegreen"] = (50, 205, 50),
                ["linen"] = (250, 240, 230),
                ["magenta"] = (255, 0, 255),
                ["maroon"] = (128, 0, 0),
                ["mediumaquamarine"] = (102, 205, 170),
                ["mediumblue"] = (0, 0, 205),
                ["mediumorchid"] = (186, 85, 211),
                ["mediumpurple"] = (147, 112, 219),
                ["mediumseagreen"] = (60, 179, 113),
                ["mediumslateblue"] = (123, 104, 238),
                ["mediumspringgreen"] = (0, 250, 154),
                ["mediumturquoise"] = (72, 209, 204),
                ["mediumvioletred"] = (199, 21, 133),
                ["midnightblue"] = (25, 25, 112),
                ["mintcream"] = (245, 255, 250),
                ["mistyrose"] = (255, 228, 225),
                ["moccasin"] = (255, 228, 181),
                ["navajowhite"] = (255, 222, 173),
                ["navy"] = (0, 0, 128),
                ["oldlace"] = (253, 245, 230),
                ["olive"] = (128, 128, 0),
                ["olivedrab"] = (107, 142, 35),
                ["orange"] = (255, 165, 0),
                ["orangered"] = (255, 69, 0),
                ["orchid"] = (218, 112, 214),
                ["palegoldenrod"] = (238, 232, 170),
                ["palegreen"] = (152, 251, 152),
                ["paleturquoise"] = (175, 238, 238),
                ["palevioletred"] = (219, 112, 147),
                ["papayawhip"] = (255, 239, 213),
                ["peachpuff"] = (255, 218, 185),
                ["peru"] = (205, 133, 63),
                ["pink"] = (255, 192, 203),
                ["plum"] = (221, 160, 221),
                ["powderblue"] = (176, 224, 230),
                ["purple"] = (128, 0, 128),
                ["rebeccapurple"] = (102, 51, 153),
                ["red"] = (255, 0, 0),
                ["rosybrown"] = (188, 143, 143),
                ["royalblue"] = (65, 105, 225),
                ["saddlebrown"] = (139, 69, 19),
                ["salmon"] = (250, 128, 114),
                ["sandybrown"] = (244, 164, 96),
                ["seagreen"] = (46, 139, 87),
                ["seashell"] = (255, 245, 238),
                ["sienna"] = (160, 82, 45),
                ["silver"] = (192, 192, 192),
                ["skyblue"] = (135, 206, 235),
                ["slateblue"] = (106, 90, 205),
                ["slategray"] = (112, 128, 144),
                ["slategrey"] = (112, 128, 144),
                ["snow"] = (255, 250, 250),
                ["springgreen"] = (0, 255, 127),
                ["steelblue"] = (70, 130, 180),
                ["tan"] = (210, 180, 140),
                ["teal"] = (0, 128, 128),
                ["thistle"] = (216, 191, 216),
                ["tomato"] = (255, 99, 71),
                ["turquoise"] = (64, 224, 208),
                ["violet"] = (238, 130, 238),
                ["wheat"] = (245, 222, 179),
                ["white"] = (255, 255, 255),
                ["whitesmoke"] = (245, 245, 245),
                ["yellow"] = (255, 255, 0),
                ["yellowgreen"] = (154, 205, 50),
            };

            if (namedColors.TryGetValue(lower, out var color))
            {
                result = CreateCssColorValue(color.r, color.g, color.b, 1d);
                return true;
            }

            if (TryParseHexColor(cssText, out var r, out var g, out var b, out var a))
            {
                result = CreateCssColorValue(r, g, b, a);
                return true;
            }

            if (TryParseRgbFunction(cssText, out var cr, out var cg, out var cb, out var ca))
            {
                result = CreateCssColorValue(cr, cg, cb, ca);
                return true;
            }

            return false;
        }

        private static FenObject CreateCssColorValue(byte r, byte g, byte b, double alpha)
        {
            var obj = new FenObject();
            obj.InternalClass = "CSSRGB";
            obj.Set("r", FenValue.FromNumber(r));
            obj.Set("g", FenValue.FromNumber(g));
            obj.Set("b", FenValue.FromNumber(b));
            obj.Set("alpha", FenValue.FromNumber(alpha));
            obj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                var o = thisVal.AsObject();
                var vr = (int)(o?.Get("r").ToNumber() ?? 0);
                var vg = (int)(o?.Get("g").ToNumber() ?? 0);
                var vb = (int)(o?.Get("b").ToNumber() ?? 0);
                var va = o?.Get("alpha").ToNumber() ?? 1;

                if (va >= 0.9999)
                    return FenValue.FromString($"rgb({vr}, {vg}, {vb})");
                return FenValue.FromString($"rgba({vr}, {vg}, {vb}, {va.ToString("F3", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')})");
            })));
            return obj;
        }

        private static bool TryParseHexColor(string text, out byte r, out byte g, out byte b, out double a)
        {
            r = g = b = 0;
            a = 1d;

            if (text.Length < 4 || text[0] != '#')
                return false;

            var hex = text.Substring(1);

            if (hex.Length == 3)
            {
                if (!TryParseHexByte(hex[0].ToString() + hex[0], out r)) return false;
                if (!TryParseHexByte(hex[1].ToString() + hex[1], out g)) return false;
                if (!TryParseHexByte(hex[2].ToString() + hex[2], out b)) return false;
                return true;
            }

            if (hex.Length == 4)
            {
                if (!TryParseHexByte(hex[0].ToString() + hex[0], out r)) return false;
                if (!TryParseHexByte(hex[1].ToString() + hex[1], out g)) return false;
                if (!TryParseHexByte(hex[2].ToString() + hex[2], out b)) return false;
                if (!TryParseHexAlpha(hex[3].ToString() + hex[3], out a)) return false;
                return true;
            }

            if (hex.Length == 6)
            {
                if (!TryParseHexByte(hex.Substring(0, 2), out r)) return false;
                if (!TryParseHexByte(hex.Substring(2, 2), out g)) return false;
                if (!TryParseHexByte(hex.Substring(4, 2), out b)) return false;
                return true;
            }

            if (hex.Length == 8)
            {
                if (!TryParseHexByte(hex.Substring(0, 2), out r)) return false;
                if (!TryParseHexByte(hex.Substring(2, 2), out g)) return false;
                if (!TryParseHexByte(hex.Substring(4, 2), out b)) return false;
                if (!TryParseHexAlpha(hex.Substring(6, 2), out a)) return false;
                return true;
            }

            return false;
        }

        private static bool TryParseHexByte(string hex, out byte value)
        {
            return byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseHexAlpha(string hex, out double alpha)
        {
            alpha = 1d;
            if (!byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return false;

            alpha = Math.Round(value / 255d, 3);
            return true;
        }

        private static bool TryParseRgbFunction(string text, out byte r, out byte g, out byte b, out double a)
        {
            r = g = b = 0;
            a = 1d;

            var isRgba = text.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase);
            var isRgb = !isRgba && text.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase);

            if (!isRgb && !isRgba)
                return false;

            if (!text.EndsWith(")"))
                return false;

            var startIdx = isRgba ? 5 : 4;
            var inner = text.Substring(startIdx, text.Length - startIdx - 1);
            var parts = SplitCommaSeparatedTopLevel(inner);

            if (parts.Count < 3)
                return false;

            if (!TryExtractByte(parts[0], out r)) return false;
            if (!TryExtractByte(parts[1], out g)) return false;
            if (!TryExtractByte(parts[2], out b)) return false;

            if (parts.Count >= 4)
            {
                if (!double.TryParse(parts[3].Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out a))
                    return false;
                if (a < 0 || a > 1)
                    return false;
            }

            return true;
        }

        private static bool TryExtractByte(string text, out byte value)
        {
            value = 0;
            text = text.Trim();

            if (text.EndsWith("%", StringComparison.Ordinal))
            {
                var pctText = text.Substring(0, text.Length - 1);
                if (!double.TryParse(pctText, NumberStyles.Float | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var pct))
                    return false;
                value = (byte)Math.Max(0, Math.Min(255, Math.Round(pct * 2.55)));
                return true;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                return false;

            value = (byte)Math.Max(0, Math.Min(255, intVal));
            return true;
        }

        #endregion

        #region Serialization

        private static string SerializeCssStyleValue(IObject cssValue)
        {
            if (cssValue == null)
                return string.Empty;

            var toStringFn = cssValue.Get("toString");
            if (toStringFn.IsFunction)
                return toStringFn.AsFunction().Invoke(Array.Empty<FenValue>(), null, FenValue.FromObject(cssValue)).ToString();

            var internalClass = (cssValue as FenObject)?.InternalClass ?? "";

            switch (internalClass)
            {
                case "CSSUnitValue":
                {
                    var val = cssValue.Get("value").ToNumber();
                    var unit = cssValue.Get("unit").ToString();
                    if (unit == "number")
                        return val.ToString("G", CultureInfo.InvariantCulture);
                    return val.ToString("G", CultureInfo.InvariantCulture) + unit;
                }
                case "CSSKeywordValue":
                    return cssValue.Get("value").ToString();
                default:
                    return cssValue.ToString();
            }
        }

        #endregion

        #region Internal Helpers

        private static FenFunction MakeInternalGetter(Func<string, string> getStyle)
        {
            return new FenFunction("__internal_getStyle", (args, _) =>
            {
                var prop = args.Length > 0 ? args[0].ToString() : "";
                return FenValue.FromString(getStyle?.Invoke(prop) ?? "");
            });
        }

        private static FenFunction MakeInternalSetter(Action<string, string> setStyle)
        {
            return new FenFunction("__internal_setStyle", (args, _) =>
            {
                var prop = args.Length > 0 ? args[0].ToString() : "";
                var value = args.Length > 1 ? args[1].ToString() : "";
                setStyle?.Invoke(prop, value);
                return FenValue.Undefined;
            });
        }

        private static string GetContextKey(IExecutionContext context)
        {
            if (context?.DocumentUrl != null)
                return context.DocumentUrl.ToString();
            return null;
        }

        private static Dictionary<string, string> GetStyleMapDictionary(IObject mapObject)
        {
            return (mapObject as FenObject)?.NativeObject as Dictionary<string, string>;
        }

        #endregion

        #region CSS Math Helper Functions (exposed on CSSMathValue prototype)

        /// <summary>
        /// Creates CSSMathSum(values), CSSMathProduct(values), CSSMathNegate(value),
        /// CSSMathInvert(value), CSSMathMin(values), CSSMathMax(values), CSSMathClamp(values).
        /// These are factory helpers that wrap the raw CSSMathValue objects.
        /// </summary>
        public static FenObject CreateCssMathConstructorGroup()
        {
            var group = new FenObject();

            group.Set("CSSMathSum", FenValue.FromFunction(CreateMathFactory("CSSMathSum", "sum", (nums) =>
            {
                var sum = 0d;
                foreach (var n in nums) sum += n;
                return sum;
            })));

            group.Set("CSSMathProduct", FenValue.FromFunction(CreateMathFactory("CSSMathProduct", "product", (nums) =>
            {
                var prod = 1d;
                foreach (var n in nums) prod *= n;
                return prod;
            })));

            group.Set("CSSMathNegate", FenValue.FromFunction(CreateUnaryMathFactory("CSSMathNegate", "negate", v => -v)));
            group.Set("CSSMathInvert", FenValue.FromFunction(CreateUnaryMathFactory("CSSMathInvert", "invert", v => 1d / v)));
            group.Set("CSSMathMin", FenValue.FromFunction(CreateVariadicMathFactory("CSSMathMin", "min", nums => nums.Count > 0 ? Min(nums) : 0d)));
            group.Set("CSSMathMax", FenValue.FromFunction(CreateVariadicMathFactory("CSSMathMax", "max", nums => nums.Count > 0 ? Max(nums) : 0d)));
            group.Set("CSSMathClamp", FenValue.FromFunction(CreateClampMathFactory()));

            return group;
        }

        private static FenFunction CreateMathFactory(string name, string op, Func<List<double>, double> compute)
        {
            var ctor = new FenFunction(name, (args, thisVal) =>
            {
                var obj = new FenObject();
                obj.InternalClass = name;
                obj.Set("operator", FenValue.FromString(op));

                var numericArgs = new List<double>();
                var valuesArray = FenObject.CreateArray();

                if (args.Length > 0 && args[0].IsObject)
                {
                    var valsObj = args[0].AsObject();
                    var len = (int)(valsObj.Get("length").ToNumber());
                    for (var i = 0; i < len; i++)
                    {
                        var item = valsObj.Get(i.ToString());
                        var num = ExtractNumericFromCssValue(item);
                        numericArgs.Add(num);
                        valuesArray.Set(i.ToString(), item);
                    }
                }
                else
                {
                    for (var i = 0; i < args.Length; i++)
                    {
                        var num = ExtractNumericFromCssValue(args[i]);
                        numericArgs.Add(num);
                        valuesArray.Set(i.ToString(), args[i]);
                    }
                }

                valuesArray.Set("length", FenValue.FromNumber(numericArgs.Count));
                obj.Set("values", FenValue.FromObject(valuesArray));
                obj.Set("computedValue", FenValue.FromNumber(compute(numericArgs)));

                obj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (_a, _t) =>
                {
                    var o = _t.AsObject();
                    var vOp = o?.Get("operator").ToString() ?? "";
                    var vVals = o?.Get("values") ?? FenValue.Undefined;
                    var parts = new List<string>();
                    if (vVals.IsObject)
                    {
                        var vArr = vVals.AsObject();
                        var vLen = (int)(vArr.Get("length").ToNumber());
                        for (var i = 0; i < vLen; i++)
                        {
                            var item = vArr.Get(i.ToString());
                            parts.Add(SerializeCssStyleValue(item.IsObject ? item.AsObject() : null));
                        }
                    }
                    return FenValue.FromString(vOp + "(" + string.Join(", ", parts) + ")");
                })));

                return FenValue.FromObject(obj);
            })
            {
                IsConstructor = true,
                NativeLength = 1
            };

            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(ctor));
            ctor.Set("prototype", FenValue.FromObject(prototype));
            return ctor;
        }

        private static FenFunction CreateUnaryMathFactory(string name, string op, Func<double, double> compute)
        {
            var ctor = new FenFunction(name, (args, thisVal) =>
            {
                if (args.Length < 1)
                    throw new InvalidOperationException($"{name} requires at least one argument.");

                var obj = new FenObject();
                obj.InternalClass = name;
                obj.Set("operator", FenValue.FromString(op));

                var num = ExtractNumericFromCssValue(args[0]);
                var valuesArray = FenObject.CreateArray();
                valuesArray.Set("0", args[0]);

                valuesArray.Set("length", FenValue.FromNumber(1));
                obj.Set("values", FenValue.FromObject(valuesArray));
                obj.Set("computedValue", FenValue.FromNumber(compute(num)));

                obj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (_a, _t) =>
                {
                    var o = _t.AsObject();
                    var vOp = o?.Get("operator").ToString() ?? "";
                    var inner = SerializeCssStyleValue(o?.Get("values").AsObject()?.Get("0").AsObject());
                    return FenValue.FromString(vOp + "(" + inner + ")");
                })));

                return FenValue.FromObject(obj);
            })
            {
                IsConstructor = true,
                NativeLength = 1
            };

            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(ctor));
            ctor.Set("prototype", FenValue.FromObject(prototype));
            return ctor;
        }

        private static FenFunction CreateVariadicMathFactory(string name, string op, Func<List<double>, double> compute)
        {
            return CreateMathFactory(name, op, compute);
        }

        private static FenFunction CreateClampMathFactory()
        {
            var ctor = new FenFunction("CSSMathClamp", (args, thisVal) =>
            {
                if (args.Length < 3)
                    throw new InvalidOperationException("CSSMathClamp requires min, val, and max arguments.");

                var obj = new FenObject();
                obj.InternalClass = "CSSMathClamp";
                obj.Set("operator", FenValue.FromString("clamp"));

                var min = ExtractNumericFromCssValue(args[0]);
                var val = ExtractNumericFromCssValue(args[1]);
                var max = ExtractNumericFromCssValue(args[2]);

                var valuesArray = FenObject.CreateArray();
                valuesArray.Set("0", args[0]);
                valuesArray.Set("1", args[1]);
                valuesArray.Set("2", args[2]);
                valuesArray.Set("length", FenValue.FromNumber(3));
                obj.Set("values", FenValue.FromObject(valuesArray));
                obj.Set("computedValue", FenValue.FromNumber(Math.Max(min, Math.Min(val, max))));

                obj.Set("toString", FenValue.FromFunction(new FenFunction("toString", (_a, _t) =>
                {
                    var o = _t.AsObject();
                    var vals = o?.Get("values") ?? FenValue.Undefined;
                    var parts = new List<string>();
                    if (vals.IsObject)
                    {
                        var vArr = vals.AsObject();
                        var len = (int)(vArr.Get("length").ToNumber());
                        for (var i = 0; i < len; i++)
                            parts.Add(SerializeCssStyleValue(vArr.Get(i.ToString()).AsObject()));
                    }
                    return FenValue.FromString("clamp(" + string.Join(", ", parts) + ")");
                })));

                return FenValue.FromObject(obj);
            })
            {
                IsConstructor = true,
                NativeLength = 3
            };

            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(ctor));
            ctor.Set("prototype", FenValue.FromObject(prototype));
            return ctor;
        }

        private static double ExtractNumericFromCssValue(FenValue value)
        {
            if (value.IsNumber)
                return value.ToNumber();

            if (value.IsObject)
            {
                var obj = value.AsObject();
                var computed = obj?.Get("computedValue");
                if (computed.HasValue && computed.Value.IsNumber)
                    return computed.Value.ToNumber();

                var valProp = obj?.Get("value");
                if (valProp.HasValue && valProp.Value.IsNumber)
                    return valProp.Value.ToNumber();

                return 0d;
            }

            if (double.TryParse(value.ToString(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            return 0d;
        }

        private static double Min(List<double> nums)
        {
            var min = double.MaxValue;
            foreach (var n in nums)
                if (n < min) min = n;
            return min;
        }

        private static double Max(List<double> nums)
        {
            var max = double.MinValue;
            foreach (var n in nums)
                if (n > max) max = n;
            return max;
        }

        #endregion
    }
}
