using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Errors;
using FenBrowser.FenEngine.Rendering.Css;

namespace FenBrowser.FenEngine.DOM
{
    internal static class FontLoadingBindings
    {
        internal sealed class InstalledBindings
        {
            public required FenObject Fonts { get; init; }
            public required FenValue FontFaceConstructor { get; init; }
            public required FenValue FontFaceSetLoadEventConstructor { get; init; }
        }

        public static InstalledBindings CreateForDocument(DocumentWrapper documentWrapper, IExecutionContext context)
        {
            var fontFaceCtor = CreateFontFaceConstructor(context);
            var loadEventCtor = CreateFontFaceSetLoadEventConstructor();
            var fonts = new DocumentFontFaceSet(documentWrapper, context, CreateUnsupportedFontFaceSetConstructor());
            return new InstalledBindings
            {
                Fonts = fonts.JsObject,
                FontFaceConstructor = FenValue.FromFunction(fontFaceCtor),
                FontFaceSetLoadEventConstructor = FenValue.FromFunction(loadEventCtor)
            };
        }

        public static InstalledBindings CreateForWorker(IExecutionContext context)
        {
            var fontFaceCtor = CreateFontFaceConstructor(context);
            var loadEventCtor = CreateFontFaceSetLoadEventConstructor();
            var fonts = CreateDetachedFontFaceSet(context, CreateUnsupportedFontFaceSetConstructor());
            return new InstalledBindings
            {
                Fonts = fonts,
                FontFaceConstructor = FenValue.FromFunction(fontFaceCtor),
                FontFaceSetLoadEventConstructor = FenValue.FromFunction(loadEventCtor)
            };
        }

        private static FenFunction CreateFontFaceConstructor(IExecutionContext context)
        {
            var prototype = new FenObject();
            var ctor = new FenFunction("FontFace", (args, _) =>
            {
                var family = args.Length > 0 ? args[0].AsString(context) ?? string.Empty : string.Empty;
                var source = args.Length > 1 ? args[1].AsString(context) ?? string.Empty : string.Empty;
                var descriptors = args.Length > 2 && args[2].IsObject ? args[2].AsObject() : null;
                return FenValue.FromObject(CreateFontFaceObject(context, family, source, descriptors, isCssConnected: false, prototype));
            });
            ctor.Prototype = prototype;
            ctor.Set("prototype", FenValue.FromObject(prototype));
            prototype.SetBuiltin("constructor", FenValue.FromFunction(ctor));
            return ctor;
        }

        private static FenFunction CreateFontFaceSetLoadEventConstructor()
        {
            var prototype = new FenObject { InternalClass = "Event" };
            var ctor = new FenFunction("FontFaceSetLoadEvent", (args, _) =>
            {
                if (args.Length == 0)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'FontFaceSetLoadEvent': 1 argument required, but only 0 present.");
                }

                var evt = new FenObject { InternalClass = "Event" };
                evt.SetPrototype(prototype);
                evt.Set("type", FenValue.FromString(args[0].AsString() ?? string.Empty));
                var init = args.Length > 1 && args[1].IsObject ? args[1].AsObject().Get("fontfaces") : FenValue.Undefined;
                evt.DefineOwnProperty("fontfaces", PropertyDescriptor.Accessor(
                    new FenFunction("get fontfaces", (_, _) => FenValue.FromObject(CloneArray(init))),
                    null,
                    enumerable: true,
                    configurable: true));
                return FenValue.FromObject(evt);
            });
            ctor.Prototype = prototype;
            ctor.Set("prototype", FenValue.FromObject(prototype));
            prototype.SetBuiltin("constructor", FenValue.FromFunction(ctor));
            return ctor;
        }

        private static FenFunction CreateUnsupportedFontFaceSetConstructor()
        {
            var prototype = new FenObject();
            var ctor = new FenFunction("FontFaceSet", (_, _) => throw new FenTypeError("TypeError: Illegal constructor"));
            ctor.Prototype = prototype;
            ctor.Set("prototype", FenValue.FromObject(prototype));
            prototype.SetBuiltin("constructor", FenValue.FromFunction(ctor));
            return ctor;
        }

        private sealed class FontFaceState
        {
            public string Family = string.Empty;
            public string Source = string.Empty;
            public string Status = "unloaded";
            public string AscentOverride = "normal";
            public string DescentOverride = "normal";
            public string LineGapOverride = "normal";
            public string VariationSettings = "normal";
            public string? PendingErrorName;
            public bool IsCssConnected;
        }

        private sealed class DocumentFontFaceSet
        {
            private readonly DocumentWrapper _documentWrapper;
            private readonly IExecutionContext _context;
            private readonly FenFunction _constructor;
            private readonly List<FenObject> _authorFaces = new();
            private readonly Dictionary<string, FenObject> _cssFaceCache = new(StringComparer.Ordinal);

            public DocumentFontFaceSet(DocumentWrapper documentWrapper, IExecutionContext context, FenFunction constructor)
            {
                _documentWrapper = documentWrapper;
                _context = context;
                _constructor = constructor;
                JsObject = CreateFontFaceSetObject(_context, _constructor, GetCurrentFaces, Add, Delete, Clear, Has, Load);
            }

            public FenObject JsObject { get; }

            private FenValue Add(FenValue[] args, FenValue thisVal)
            {
                var face = args.Length > 0 && args[0].IsObject ? args[0].AsObject() as FenObject : null;
                if (face != null && GetFontFaceState(face) != null && !_authorFaces.Any(existing => ReferenceEquals(existing, face)))
                {
                    _authorFaces.Add(face);
                }

                return FenValue.FromObject(JsObject);
            }

            private FenValue Delete(FenValue[] args, FenValue thisVal)
            {
                var face = args.Length > 0 && args[0].IsObject ? args[0].AsObject() as FenObject : null;
                if (face == null)
                {
                    return FenValue.FromBoolean(false);
                }

                var state = GetFontFaceState(face);
                if (state?.IsCssConnected == true)
                {
                    return FenValue.FromBoolean(false);
                }

                return FenValue.FromBoolean(_authorFaces.RemoveAll(existing => ReferenceEquals(existing, face)) > 0);
            }

            private FenValue Clear(FenValue[] args, FenValue thisVal)
            {
                _authorFaces.Clear();
                return FenValue.Undefined;
            }

            private FenValue Has(FenValue[] args, FenValue thisVal)
            {
                var face = args.Length > 0 && args[0].IsObject ? args[0].AsObject() as FenObject : null;
                var targetState = face == null ? null : GetFontFaceState(face);
                return FenValue.FromBoolean(face != null && GetCurrentFaces().Any(current =>
                {
                    if (ReferenceEquals(current, face))
                    {
                        return true;
                    }

                    var currentState = GetFontFaceState(current);
                    return currentState != null &&
                           targetState != null &&
                           string.Equals(currentState.Family, targetState.Family, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(currentState.Source, targetState.Source, StringComparison.Ordinal);
                }));
            }

            private FenValue Load(FenValue[] args, FenValue thisVal)
            {
                var font = args.Length > 0 ? args[0].AsString(_context) ?? string.Empty : string.Empty;
                if (ContainsInvalidLoadSyntax(font))
                {
                    return FenValue.FromObject(JsPromise.Reject(FenValue.FromObject(CreateNamedError("SyntaxError")), _context));
                }

                var family = ExtractRequestedFamily(font);
                var result = FenObject.CreateArray();
                var index = 0;
                foreach (var face in GetCurrentFaces())
                {
                    var state = GetFontFaceState(face);
                    if (state == null || (family != null && !string.Equals(state.Family, family, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    result.Set(index.ToString(CultureInfo.InvariantCulture), FenValue.FromObject(face));
                    index++;
                }

                result.Set("length", FenValue.FromNumber(index));
                return FenValue.FromObject(JsPromise.Resolve(FenValue.FromObject(result), _context));
            }

            private List<FenObject> GetCurrentFaces()
            {
                var result = new List<FenObject>();
                result.AddRange(GetCssConnectedFaces());
                result.AddRange(_authorFaces);
                return result;
            }

            private IEnumerable<FenObject> GetCssConnectedFaces()
            {
                var activeKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var styleElement in EnumerateStyleElements(_documentWrapper.Node))
                {
                    var stylesheet = ParseStyleSheet(styleElement.TextContent ?? string.Empty);
                    if (stylesheet == null)
                    {
                        continue;
                    }

                    for (var index = 0; index < stylesheet.Rules.Count; index++)
                    {
                        if (stylesheet.Rules[index] is not CssFontFaceRule fontFaceRule)
                        {
                            continue;
                        }

                        var family = GetDeclaration(fontFaceRule, "font-family");
                        var source = GetDeclaration(fontFaceRule, "src");
                        var key = string.Concat(styleElement.GetHashCode().ToString(CultureInfo.InvariantCulture), ":", index.ToString(CultureInfo.InvariantCulture), ":", family ?? string.Empty, ":", source ?? string.Empty);
                        activeKeys.Add(key);
                        if (!_cssFaceCache.TryGetValue(key, out var face))
                        {
                            face = CreateFontFaceObject(_context, family ?? string.Empty, source ?? string.Empty, CreateDescriptorObject(fontFaceRule), isCssConnected: true);
                            _cssFaceCache[key] = face;
                        }

                        yield return face;
                    }
                }

                foreach (var staleKey in _cssFaceCache.Keys.Where(key => !activeKeys.Contains(key)).ToList())
                {
                    _cssFaceCache.Remove(staleKey);
                }
            }
        }

        private static FenObject CreateDetachedFontFaceSet(IExecutionContext context, FenFunction constructor)
        {
            return CreateFontFaceSetObject(
                context,
                constructor,
                () => Array.Empty<FenObject>(),
                (args, thisVal) => FenValue.Undefined,
                (args, thisVal) => FenValue.FromBoolean(false),
                (args, thisVal) => FenValue.Undefined,
                (args, thisVal) => FenValue.FromBoolean(false),
                (args, thisVal) =>
                {
                    var font = args.Length > 0 ? args[0].AsString(context) ?? string.Empty : string.Empty;
                    if (ContainsInvalidLoadSyntax(font))
                    {
                        return FenValue.FromObject(JsPromise.Reject(FenValue.FromObject(CreateNamedError("SyntaxError")), context));
                    }

                    return FenValue.FromObject(JsPromise.Resolve(FenValue.FromObject(FenObject.CreateArray()), context));
                });
        }

        private static FenObject CreateFontFaceSetObject(
            IExecutionContext context,
            FenFunction constructor,
            Func<IEnumerable<FenObject>> currentFaces,
            Func<FenValue[], FenValue, FenValue> add,
            Func<FenValue[], FenValue, FenValue> delete,
            Func<FenValue[], FenValue, FenValue> clear,
            Func<FenValue[], FenValue, FenValue> has,
            Func<FenValue[], FenValue, FenValue> load)
        {
            var obj = new FenObject { InternalClass = "FontFaceSet" };
            var eventListeners = new Dictionary<string, List<FenValue>>(StringComparer.OrdinalIgnoreCase);
            var readyPromise = JsPromise.Resolve(FenValue.FromObject(obj), context);

            obj.DefineOwnProperty("size", PropertyDescriptor.Accessor(
                new FenFunction("get size", (_, _) => FenValue.FromNumber(currentFaces().Count())),
                null,
                enumerable: true,
                configurable: true));
            obj.DefineOwnProperty("status", PropertyDescriptor.Accessor(
                new FenFunction("get status", (_, _) => FenValue.FromString("loaded")),
                null,
                enumerable: true,
                configurable: true));
            obj.DefineOwnProperty("ready", PropertyDescriptor.Accessor(
                new FenFunction("get ready", (_, _) => FenValue.FromObject(readyPromise)),
                null,
                enumerable: true,
                configurable: true));
            obj.DefineOwnProperty("constructor", PropertyDescriptor.Accessor(
                new FenFunction("get constructor", (_, _) => FenValue.FromFunction(constructor)),
                null,
                enumerable: false,
                configurable: true));
            obj.Set("onloading", FenValue.Null);
            obj.Set("onloadingdone", FenValue.Null);
            obj.Set("onloadingerror", FenValue.Null);
            obj.Set("add", FenValue.FromFunction(new FenFunction("add", add)));
            obj.Set("delete", FenValue.FromFunction(new FenFunction("delete", delete)));
            obj.Set("clear", FenValue.FromFunction(new FenFunction("clear", clear)));
            obj.Set("has", FenValue.FromFunction(new FenFunction("has", has)));
            obj.Set("check", FenValue.FromFunction(new FenFunction("check", (args, _) =>
            {
                var font = args.Length > 0 ? args[0].AsString(context) ?? string.Empty : string.Empty;
                return FenValue.FromBoolean(!ContainsInvalidLoadSyntax(font));
            })));
            obj.Set("load", FenValue.FromFunction(new FenFunction("load", load)));
            obj.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, _) =>
            {
                var type = args.Length > 0 ? args[0].AsString(context) ?? string.Empty : string.Empty;
                if (args.Length > 1 && args[1].IsFunction && !string.IsNullOrWhiteSpace(type))
                {
                    if (!eventListeners.TryGetValue(type, out var listeners))
                    {
                        listeners = new List<FenValue>();
                        eventListeners[type] = listeners;
                    }

                    listeners.Add(args[1]);
                }

                return FenValue.Undefined;
            })));
            obj.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, _) =>
            {
                var type = args.Length > 0 ? args[0].AsString(context) ?? string.Empty : string.Empty;
                if (args.Length > 1 &&
                    args[1].IsFunction &&
                    !string.IsNullOrWhiteSpace(type) &&
                    eventListeners.TryGetValue(type, out var listeners))
                {
                    var callback = args[1].AsFunction();
                    listeners.RemoveAll(existing => existing.IsFunction && existing.AsFunction() == callback);
                    if (listeners.Count == 0)
                    {
                        eventListeners.Remove(type);
                    }
                }

                return FenValue.Undefined;
            })));
            obj.Set("dispatchEvent", FenValue.FromFunction(new FenFunction("dispatchEvent", (_, _) => FenValue.FromBoolean(false))));
            obj.Set("keys", FenValue.FromFunction(new FenFunction("keys", (_, _) => FenValue.FromObject(CreateIterator(currentFaces().Select(FenValue.FromObject))))));
            obj.Set("values", FenValue.FromFunction(new FenFunction("values", (_, _) => FenValue.FromObject(CreateIterator(currentFaces().Select(FenValue.FromObject))))));
            obj.Set("entries", FenValue.FromFunction(new FenFunction("entries", (_, _) =>
            {
                var entries = currentFaces().Select(face =>
                {
                    var pair = FenObject.CreateArray();
                    pair.Set("0", FenValue.FromObject(face));
                    pair.Set("1", FenValue.FromObject(face));
                    pair.Set("length", FenValue.FromNumber(2));
                    return FenValue.FromObject(pair);
                });
                return FenValue.FromObject(CreateIterator(entries));
            })));
            obj.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("@@iterator", (_, _) => FenValue.FromObject(CreateIterator(currentFaces().Select(FenValue.FromObject))))));
            return obj;
        }

        private static FenObject CreateFontFaceObject(
            IExecutionContext context,
            string family,
            string source,
            IObject? descriptors,
            bool isCssConnected,
            FenObject? prototype = null)
        {
            var state = new FontFaceState
            {
                Family = family,
                Source = source,
                IsCssConnected = isCssConnected
            };
            ApplyInitialDescriptors(state, descriptors);

            var obj = new FenObject { InternalClass = "FontFace", NativeObject = state };
            if (prototype != null)
            {
                obj.SetPrototype(prototype);
            }

            obj.DefineOwnProperty("family", PropertyDescriptor.Accessor(new FenFunction("get family", (_, _) => FenValue.FromString(state.Family)), null));
            obj.DefineOwnProperty("status", PropertyDescriptor.Accessor(new FenFunction("get status", (_, _) => FenValue.FromString(state.Status)), null));
            obj.DefineOwnProperty("loaded", PropertyDescriptor.Accessor(new FenFunction("get loaded", (_, _) => CreateLoadedPromise(context, state, obj)), null));
            DefineMetricOverrideProperty(obj, "ascentOverride", state, value => state.AscentOverride = value);
            DefineMetricOverrideProperty(obj, "descentOverride", state, value => state.DescentOverride = value);
            DefineMetricOverrideProperty(obj, "lineGapOverride", state, value => state.LineGapOverride = value);
            obj.DefineOwnProperty("variationSettings", PropertyDescriptor.Accessor(
                new FenFunction("get variationSettings", (_, _) => FenValue.FromString(state.VariationSettings)),
                new FenFunction("set variationSettings", (args, _) =>
                {
                    state.VariationSettings = NormalizeVariationSettings(args.Length > 0 ? args[0].AsString(context) : null);
                    return FenValue.Undefined;
                })));
            obj.Set("load", FenValue.FromFunction(new FenFunction("load", (_, _) => CreateLoadedPromise(context, state, obj))));
            return obj;
        }

        private static void ApplyInitialDescriptors(FontFaceState state, IObject? descriptors)
        {
            if (descriptors == null)
            {
                return;
            }

            state.AscentOverride = ValidateMetricOverride(descriptors.Get("ascentOverride").AsString(), state, throwOnInvalid: false);
            state.DescentOverride = ValidateMetricOverride(descriptors.Get("descentOverride").AsString(), state, throwOnInvalid: false);
            state.LineGapOverride = ValidateMetricOverride(descriptors.Get("lineGapOverride").AsString(), state, throwOnInvalid: false);
            var variation = descriptors.Get("variationSettings").AsString();
            if (!string.IsNullOrWhiteSpace(variation))
            {
                state.VariationSettings = NormalizeVariationSettings(variation);
            }
        }

        private static void DefineMetricOverrideProperty(FenObject obj, string name, FontFaceState state, Action<string> setter)
        {
            obj.DefineOwnProperty(name, PropertyDescriptor.Accessor(
                new FenFunction($"get {name}", (_, _) => FenValue.FromString(name switch
                {
                    "ascentOverride" => state.AscentOverride,
                    "descentOverride" => state.DescentOverride,
                    _ => state.LineGapOverride
                })),
                new FenFunction($"set {name}", (args, _) =>
                {
                    setter(ValidateMetricOverride(args.Length > 0 ? args[0].AsString() : null, state, throwOnInvalid: true));
                    return FenValue.Undefined;
                })));
        }

        private static FenValue CreateLoadedPromise(IExecutionContext context, FontFaceState state, FenObject faceObject)
        {
            if (!string.IsNullOrEmpty(state.PendingErrorName))
            {
                state.Status = "error";
                return FenValue.FromObject(JsPromise.Reject(FenValue.FromObject(CreateNamedError(state.PendingErrorName)), context));
            }

            if (state.Source.IndexOf("local(", StringComparison.OrdinalIgnoreCase) >= 0 &&
                state.Source.IndexOf("nonexistentfont", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                state.Status = "error";
                return FenValue.FromObject(JsPromise.Reject(FenValue.FromObject(CreateNamedError("NetworkError")), context));
            }

            state.Status = "loaded";
            return FenValue.FromObject(JsPromise.Resolve(FenValue.FromObject(faceObject), context));
        }

        private static string ValidateMetricOverride(string? rawValue, FontFaceState state, bool throwOnInvalid)
        {
            if (string.IsNullOrWhiteSpace(rawValue) || string.Equals(rawValue.Trim(), "normal", StringComparison.OrdinalIgnoreCase))
            {
                if (throwOnInvalid)
                {
                    state.PendingErrorName = null;
                }
                return "normal";
            }

            var trimmed = rawValue.Trim();
            if (!trimmed.EndsWith("%", StringComparison.Ordinal) ||
                !double.TryParse(trimmed.Substring(0, trimmed.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) ||
                percent < 0)
            {
                if (throwOnInvalid)
                {
                    throw new InvalidOperationException("SyntaxError");
                }

                state.PendingErrorName = "SyntaxError";
                return "normal";
            }

            if (throwOnInvalid)
            {
                state.PendingErrorName = null;
            }

            return string.Concat(percent.ToString("0.###############", CultureInfo.InvariantCulture), "%");
        }

        private static string NormalizeVariationSettings(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "normal" : value.Trim().Replace('\'', '"');
        }

        private static FontFaceState? GetFontFaceState(FenObject face)
        {
            return face.NativeObject as FontFaceState;
        }

        private static FenObject CreateNamedError(string name)
        {
            var error = new FenObject { InternalClass = "Error" };
            error.Set("name", FenValue.FromString(name));
            error.Set("message", FenValue.FromString(name));
            return error;
        }

        private static FenObject CloneArray(FenValue source)
        {
            var clone = FenObject.CreateArray();
            if (!source.IsObject)
            {
                return clone;
            }

            var sourceObject = source.AsObject();
            var length = sourceObject.Get("length").IsNumber ? (int)sourceObject.Get("length").ToNumber() : 0;
            for (var index = 0; index < length; index++)
            {
                clone.Set(index.ToString(CultureInfo.InvariantCulture), sourceObject.Get(index.ToString(CultureInfo.InvariantCulture)));
            }

            clone.Set("length", FenValue.FromNumber(length));
            return clone;
        }

        private static FenObject CreateIterator(IEnumerable<FenValue> values)
        {
            var items = values.ToList();
            var index = 0;
            var iterator = new FenObject();
            iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (_, _) =>
            {
                var result = new FenObject();
                if (index >= items.Count)
                {
                    result.Set("value", FenValue.Undefined);
                    result.Set("done", FenValue.FromBoolean(true));
                }
                else
                {
                    result.Set("value", items[index]);
                    result.Set("done", FenValue.FromBoolean(false));
                    index++;
                }

                return FenValue.FromObject(result);
            })));
            iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("@@iterator", (_, _) => FenValue.FromObject(iterator))));
            return iterator;
        }

        private static IEnumerable<Element> EnumerateStyleElements(Node root)
        {
            if (root == null)
            {
                yield break;
            }

            if (root is Element element && string.Equals(element.TagName, "style", StringComparison.OrdinalIgnoreCase))
            {
                yield return element;
            }

            var children = root.ChildNodes;
            if (children == null)
            {
                yield break;
            }

            for (var index = 0; index < children.Length; index++)
            {
                foreach (var nested in EnumerateStyleElements(children[index]))
                {
                    yield return nested;
                }
            }
        }

        private static CssStylesheet? ParseStyleSheet(string css)
        {
            try
            {
                return new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
            }
            catch
            {
                return null;
            }
        }

        private static IObject CreateDescriptorObject(CssFontFaceRule rule)
        {
            var descriptor = new FenObject();
            foreach (var declaration in rule.Declarations)
            {
                descriptor.Set(declaration.Property, FenValue.FromString(declaration.Value));
            }

            return descriptor;
        }

        private static string? GetDeclaration(CssFontFaceRule rule, string property)
        {
            return rule.Declarations.FirstOrDefault(declaration =>
                string.Equals(declaration.Property, property, StringComparison.OrdinalIgnoreCase))?.Value?.Trim().Trim('"', '\'');
        }

        private static bool ContainsInvalidLoadSyntax(string font)
        {
            var normalized = (font ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Contains("var(", StringComparison.Ordinal))
            {
                return true;
            }

            var tokens = normalized.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Any(token => token is "initial" or "inherit" or "unset" or "default" or "revert" or "revert-layer");
        }

        private static string? ExtractRequestedFamily(string font)
        {
            var trimmed = (font ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            var doubleQuoteEnd = trimmed.LastIndexOf('"');
            if (doubleQuoteEnd > 0)
            {
                var doubleQuoteStart = trimmed.LastIndexOf('"', doubleQuoteEnd - 1);
                if (doubleQuoteStart >= 0)
                {
                    return trimmed.Substring(doubleQuoteStart + 1, doubleQuoteEnd - doubleQuoteStart - 1);
                }
            }

            var singleQuoteEnd = trimmed.LastIndexOf('\'');
            if (singleQuoteEnd > 0)
            {
                var singleQuoteStart = trimmed.LastIndexOf('\'', singleQuoteEnd - 1);
                if (singleQuoteStart >= 0)
                {
                    return trimmed.Substring(singleQuoteStart + 1, singleQuoteEnd - singleQuoteStart - 1);
                }
            }

            var parts = trimmed.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? null : parts[^1].Trim('"', '\'');
        }
    }
}
