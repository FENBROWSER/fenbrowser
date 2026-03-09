using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Errors;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;

namespace FenBrowser.FenEngine.DOM
{
    internal static class HighlightApiBindings
    {
        internal sealed class InstalledBindings
        {
            public required FenValue StaticRangeConstructor { get; init; }
            public required FenValue HighlightConstructor { get; init; }
            public required FenValue HighlightRegistryConstructor { get; init; }
            public required FenObject Registry { get; init; }
        }

        private static readonly Regex ValidHighlightPseudoPattern =
            new(@"^::highlight\(([A-Za-z_][A-Za-z0-9_-]*)\)$", RegexOptions.Compiled);

        private sealed class CascadedDeclaration
        {
            public required string Value { get; init; }
            public required int Specificity { get; init; }
            public required int Order { get; init; }
        }

        public static InstalledBindings Create(IExecutionContext context)
        {
            var staticRangePrototype = new FenObject();
            staticRangePrototype.SetBuiltin(JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("StaticRange"));

            var highlightPrototype = new FenObject();
            highlightPrototype.SetBuiltin(JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("Highlight"));

            var registryPrototype = new FenObject();
            registryPrototype.SetBuiltin(JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("HighlightRegistry"));

            var staticRangeCtor = new FenFunction("StaticRange", (args, _) =>
            {
                if (args.Length == 0 || !args[0].IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to construct 'StaticRange': parameter 1 is not of type 'StaticRangeInit'.");
                }

                return FenValue.FromObject(CreateStaticRange(args[0].AsObject(), context, staticRangePrototype));
            });
            staticRangeCtor.Prototype = staticRangePrototype;
            staticRangeCtor.Set("prototype", FenValue.FromObject(staticRangePrototype));
            staticRangePrototype.SetBuiltin("constructor", FenValue.FromFunction(staticRangeCtor));

            var highlightCtor = new FenFunction("Highlight", (args, _) =>
            {
                var highlight = new HighlightObject(context, highlightPrototype);
                foreach (var arg in args)
                {
                    highlight.AddRange(arg);
                }

                return FenValue.FromObject(highlight);
            });
            highlightCtor.Prototype = highlightPrototype;
            highlightCtor.Set("prototype", FenValue.FromObject(highlightPrototype));
            highlightPrototype.SetBuiltin("constructor", FenValue.FromFunction(highlightCtor));

            var highlightRegistryCtor = new FenFunction("HighlightRegistry", (_, _) =>
                throw new FenTypeError("TypeError: Illegal constructor"));
            highlightRegistryCtor.Prototype = registryPrototype;
            highlightRegistryCtor.Set("prototype", FenValue.FromObject(registryPrototype));
            registryPrototype.SetBuiltin("constructor", FenValue.FromFunction(highlightRegistryCtor));

            var registry = new HighlightRegistryObject(context, registryPrototype);

            return new InstalledBindings
            {
                StaticRangeConstructor = FenValue.FromFunction(staticRangeCtor),
                HighlightConstructor = FenValue.FromFunction(highlightCtor),
                HighlightRegistryConstructor = FenValue.FromFunction(highlightRegistryCtor),
                Registry = registry
            };
        }

        public static bool TryResolveComputedHighlightStyles(Element element, string pseudoText, out Dictionary<string, string> resolvedValues)
        {
            resolvedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (element == null || string.IsNullOrWhiteSpace(pseudoText))
            {
                return false;
            }

            var trimmedPseudo = pseudoText.Trim();
            var match = ValidHighlightPseudoPattern.Match(trimmedPseudo);
            if (!match.Success)
            {
                return trimmedPseudo.StartsWith("::highlight", StringComparison.OrdinalIgnoreCase);
            }

            var highlightName = match.Groups[1].Value;
            var cascadedValues = new Dictionary<string, CascadedDeclaration>(StringComparer.OrdinalIgnoreCase);
            var order = 0;
            foreach (var styleElement in EnumerateStyleElements(element.OwnerDocument))
            {
                var cssText = styleElement.TextContent ?? string.Empty;
                foreach (var rule in EnumerateStyleRules(cssText))
                {
                    foreach (var selector in SplitSelectorList(rule.SelectorText))
                    {
                        order++;
                        if (!SelectorTargetsHighlight(selector, element, highlightName, out var specificity))
                        {
                            continue;
                        }

                        foreach (var declaration in ParseDeclarations(rule.Body))
                        {
                            if (!cascadedValues.TryGetValue(declaration.Key, out var existing) ||
                                specificity > existing.Specificity ||
                                (specificity == existing.Specificity && order >= existing.Order))
                            {
                                cascadedValues[declaration.Key] = new CascadedDeclaration
                                {
                                    Value = declaration.Value,
                                    Specificity = specificity,
                                    Order = order
                                };
                            }
                        }
                    }
                }
            }

            foreach (var kvp in cascadedValues)
            {
                resolvedValues[kvp.Key] = ResolveHighlightDeclarationValue(element, kvp.Key, kvp.Value.Value);
            }

            return true;
        }

        private static StaticRangeObject CreateStaticRange(IObject init, IExecutionContext context, FenObject prototype)
        {
            var startContainer = ReadRequiredNode(init, "startContainer");
            var endContainer = ReadRequiredNode(init, "endContainer");
            var startOffset = ReadRequiredOffset(init, "startOffset");
            var endOffset = ReadRequiredOffset(init, "endOffset");
            return new StaticRangeObject(startContainer, startOffset, endContainer, endOffset, context, prototype);
        }

        private static Node ReadRequiredNode(IObject init, string name)
        {
            var node = TryGetNode(init.Get(name));
            if (node == null)
            {
                throw new FenTypeError($"TypeError: Failed to construct 'StaticRange': '{name}' is not a Node.");
            }

            return node;
        }

        private static int ReadRequiredOffset(IObject init, string name)
        {
            var value = init.Get(name);
            if (!value.IsNumber)
            {
                throw new FenTypeError($"TypeError: Failed to construct 'StaticRange': '{name}' is not a finite number.");
            }

            var numeric = value.ToNumber();
            if (double.IsNaN(numeric) || double.IsInfinity(numeric))
            {
                throw new FenTypeError($"TypeError: Failed to construct 'StaticRange': '{name}' is not a finite number.");
            }

            return (int)numeric;
        }

        private static IEnumerable<Element> EnumerateStyleElements(Document? document)
        {
            if (document?.DocumentElement == null)
            {
                yield break;
            }

            foreach (var node in document.DocumentElement.DescendantsAndSelf())
            {
                if (node is Element element &&
                    string.Equals(element.TagName, "style", StringComparison.OrdinalIgnoreCase))
                {
                    yield return element;
                }
            }
        }

        private static IEnumerable<(string SelectorText, string Body)> EnumerateStyleRules(string cssText)
        {
            if (string.IsNullOrWhiteSpace(cssText))
            {
                yield break;
            }

            var index = 0;
            while (index < cssText.Length)
            {
                var openBrace = cssText.IndexOf('{', index);
                if (openBrace < 0)
                {
                    yield break;
                }

                var selectorText = cssText.Substring(index, openBrace - index).Trim();
                var depth = 1;
                var cursor = openBrace + 1;
                while (cursor < cssText.Length && depth > 0)
                {
                    if (cssText[cursor] == '{')
                    {
                        depth++;
                    }
                    else if (cssText[cursor] == '}')
                    {
                        depth--;
                    }

                    cursor++;
                }

                if (depth != 0)
                {
                    yield break;
                }

                var body = cssText.Substring(openBrace + 1, cursor - openBrace - 2).Trim();
                if (!string.IsNullOrWhiteSpace(selectorText) &&
                    !selectorText.StartsWith("@", StringComparison.Ordinal))
                {
                    yield return (selectorText, body);
                }

                index = cursor;
            }
        }

        private static IEnumerable<string> SplitSelectorList(string selectorText)
        {
            if (string.IsNullOrWhiteSpace(selectorText))
            {
                yield break;
            }

            var depth = 0;
            var start = 0;
            for (var i = 0; i < selectorText.Length; i++)
            {
                var ch = selectorText[i];
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (ch == ',' && depth == 0)
                {
                    var selector = selectorText.Substring(start, i - start).Trim();
                    if (selector.Length > 0)
                    {
                        yield return selector;
                    }

                    start = i + 1;
                }
            }

            if (start < selectorText.Length)
            {
                var selector = selectorText.Substring(start).Trim();
                if (selector.Length > 0)
                {
                    yield return selector;
                }
            }
        }

        private static bool SelectorTargetsHighlight(string selector, Element element, string highlightName, out int specificity)
        {
            specificity = 0;
            var marker = "::highlight(" + highlightName + ")";
            var markerIndex = selector.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return false;
            }

            var suffix = selector.Substring(markerIndex + marker.Length).Trim();
            if (suffix.Length != 0)
            {
                return false;
            }

            var baseSelector = selector.Substring(0, markerIndex).Trim();
            if (baseSelector.Length == 0)
            {
                specificity = 0;
                return true;
            }

            if (TryMatchSimpleSelector(baseSelector, element, out specificity))
            {
                return true;
            }

            try
            {
                return SelectorMatcher.Matches(element, baseSelector);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryMatchSimpleSelector(string selector, Element element, out int specificity)
        {
            specificity = 0;
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }

            var trimmed = selector.Trim();
            if (trimmed.IndexOfAny(new[] { ' ', '>', '+', '~', '[', ']' }) >= 0)
            {
                return false;
            }

            var remaining = trimmed;
            var typeMatched = false;
            while (remaining.Length > 0)
            {
                if (remaining[0] == '#')
                {
                    var tokenLength = ReadIdentifierLength(remaining, 1);
                    if (tokenLength == 0)
                    {
                        return false;
                    }

                    var expectedId = remaining.Substring(1, tokenLength);
                    if (!string.Equals(element.GetAttribute("id") ?? string.Empty, expectedId, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    specificity += 100;
                    remaining = remaining.Substring(1 + tokenLength);
                    continue;
                }

                if (remaining[0] == '.')
                {
                    var tokenLength = ReadIdentifierLength(remaining, 1);
                    if (tokenLength == 0)
                    {
                        return false;
                    }

                    var expectedClass = remaining.Substring(1, tokenLength);
                    var classes = (element.GetAttribute("class") ?? string.Empty)
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (!classes.Contains(expectedClass, StringComparer.Ordinal))
                    {
                        return false;
                    }

                    specificity += 10;
                    remaining = remaining.Substring(1 + tokenLength);
                    continue;
                }

                if (remaining[0] == ':')
                {
                    var tokenLength = ReadIdentifierLength(remaining, 1);
                    if (tokenLength == 0)
                    {
                        return false;
                    }

                    var pseudo = remaining.Substring(1, tokenLength);
                    if (string.Equals(pseudo, "root", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ReferenceEquals(element.OwnerDocument?.DocumentElement, element))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }

                    specificity += 10;
                    remaining = remaining.Substring(1 + tokenLength);
                    continue;
                }

                if (remaining[0] == '*')
                {
                    remaining = remaining.Substring(1);
                    continue;
                }

                if (typeMatched || !IsIdentifierStart(remaining[0]))
                {
                    return false;
                }

                var tagLength = ReadIdentifierLength(remaining, 0);
                var expectedTag = remaining.Substring(0, tagLength);
                if (!string.Equals(element.TagName, expectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                specificity += 1;
                typeMatched = true;
                remaining = remaining.Substring(tagLength);
            }

            return true;
        }

        private static int ReadIdentifierLength(string source, int startIndex)
        {
            var length = 0;
            for (var i = startIndex; i < source.Length; i++)
            {
                var ch = source[i];
                if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '-')
                {
                    break;
                }

                length++;
            }

            return length;
        }

        private static bool IsIdentifierStart(char ch)
        {
            return char.IsLetter(ch) || ch == '_';
        }

        private static Dictionary<string, string> ParseDeclarations(string body)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(body))
            {
                return result;
            }

            foreach (var chunk in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIndex = chunk.IndexOf(':');
                if (colonIndex <= 0 || colonIndex >= chunk.Length - 1)
                {
                    continue;
                }

                var property = chunk.Substring(0, colonIndex).Trim();
                var value = chunk.Substring(colonIndex + 1).Trim();
                if (property.Length == 0)
                {
                    continue;
                }

                var importantIndex = value.IndexOf("!important", StringComparison.OrdinalIgnoreCase);
                if (importantIndex >= 0)
                {
                    value = value.Substring(0, importantIndex).Trim();
                }

                result[property] = value;
            }

            return result;
        }

        private static string ResolveHighlightDeclarationValue(Element element, string propertyName, string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return string.Empty;
            }

            var normalizedName = propertyName.Trim().ToLowerInvariant();
            if (normalizedName.Contains("color", StringComparison.Ordinal))
            {
                var parsed = FenBrowser.FenEngine.Rendering.CssParser.ParseColor(rawValue.Trim());
                if (parsed.HasValue)
                {
                    var color = parsed.Value;
                    if (color.Alpha >= 255)
                    {
                        return $"rgb({color.Red}, {color.Green}, {color.Blue})";
                    }

                    var alpha = Math.Round(color.Alpha / 255.0, 3);
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "rgba({0}, {1}, {2}, {3:0.###})",
                        color.Red,
                        color.Green,
                        color.Blue,
                        alpha);
                }
            }

            if (normalizedName is "text-underline-offset" or "text-decoration-thickness" &&
                TryResolveRelativeLength(rawValue, element, out var pixels))
            {
                return pixels;
            }

            return rawValue.Trim();
        }

        private static bool TryResolveRelativeLength(string rawValue, Element element, out string pixels)
        {
            pixels = string.Empty;
            var trimmed = rawValue.Trim();
            if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                pixels = NormalizePixelString(trimmed);
                return true;
            }

            if (trimmed.EndsWith("rem", StringComparison.OrdinalIgnoreCase) &&
                TryParseCssNumber(trimmed[..^3], out var remValue))
            {
                var rootFontSize = GetFontSizePixels(element.OwnerDocument?.DocumentElement) ?? 16;
                pixels = NormalizePixelString((remValue * rootFontSize).ToString(CultureInfo.InvariantCulture) + "px");
                return true;
            }

            if (trimmed.EndsWith("em", StringComparison.OrdinalIgnoreCase) &&
                TryParseCssNumber(trimmed[..^2], out var emValue))
            {
                var fontSize = GetFontSizePixels(element) ?? 16;
                pixels = NormalizePixelString((emValue * fontSize).ToString(CultureInfo.InvariantCulture) + "px");
                return true;
            }

            return false;
        }

        private static double? GetFontSizePixels(Element? element)
        {
            if (element == null)
            {
                return null;
            }

            var inlineValue = element.GetAttribute("style");
            if (!string.IsNullOrWhiteSpace(inlineValue))
            {
                var inlineDeclarations = ParseDeclarations(inlineValue);
                if (inlineDeclarations.TryGetValue("font-size", out var inlineFontSize) &&
                    TryParsePixels(inlineFontSize, out var inlinePixels))
                {
                    return inlinePixels;
                }
            }

            if (TryResolveStylesheetProperty(element, "font-size", out var stylesheetFontSize) &&
                TryParsePixels(stylesheetFontSize, out var stylesheetPixels))
            {
                return stylesheetPixels;
            }

            var computedStyle = NodeStyleExtensions.GetComputedStyle(element);
            if (computedStyle?.Map?.TryGetValue("font-size", out var computedFontSize) == true &&
                TryParsePixels(computedFontSize, out var computedPixels))
            {
                return computedPixels;
            }

            return null;
        }

        private static bool TryResolveStylesheetProperty(Element element, string propertyName, out string value)
        {
            value = string.Empty;
            CascadedDeclaration? winningDeclaration = null;
            var order = 0;

            foreach (var styleElement in EnumerateStyleElements(element.OwnerDocument))
            {
                var cssText = styleElement.TextContent ?? string.Empty;
                foreach (var rule in EnumerateStyleRules(cssText))
                {
                    foreach (var selector in SplitSelectorList(rule.SelectorText))
                    {
                        order++;
                        if (!TryMatchSimpleSelector(selector, element, out var specificity))
                        {
                            try
                            {
                                if (!SelectorMatcher.Matches(element, selector))
                                {
                                    continue;
                                }
                            }
                            catch
                            {
                                continue;
                            }
                        }

                        var declarations = ParseDeclarations(rule.Body);
                        if (!declarations.TryGetValue(propertyName, out var matchedValue))
                        {
                            continue;
                        }

                        if (winningDeclaration == null ||
                            specificity > winningDeclaration.Specificity ||
                            (specificity == winningDeclaration.Specificity && order >= winningDeclaration.Order))
                        {
                            winningDeclaration = new CascadedDeclaration
                            {
                                Value = matchedValue,
                                Specificity = specificity,
                                Order = order
                            };
                        }
                    }
                }
            }

            if (winningDeclaration == null)
            {
                return false;
            }

            value = winningDeclaration.Value;
            return true;
        }

        private static bool TryParsePixels(string value, out double pixels)
        {
            pixels = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (!trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return double.TryParse(trimmed[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out pixels);
        }

        private static bool TryParseCssNumber(string value, out double number)
        {
            return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        private static string NormalizePixelString(string rawPixels)
        {
            if (!TryParsePixels(rawPixels, out var pixels))
            {
                return rawPixels.Trim();
            }

            return pixels.ToString("0.###", CultureInfo.InvariantCulture) + "px";
        }

        private static Node? TryGetNode(FenValue value)
        {
            var obj = value.AsObject();
            if (obj == null)
            {
                return null;
            }

            return obj switch
            {
                NodeWrapper nodeWrapper => nodeWrapper.Node,
                DocumentWrapper documentWrapper => documentWrapper.Node,
                FenObject fenObject => fenObject.NativeObject as Node,
                _ => null
            };
        }

        private static bool IsHighlightRange(FenValue value)
        {
            return value.IsObject && (value.AsObject() is RangeWrapper || value.AsObject() is StaticRangeObject);
        }

        private sealed class StaticRangeObject : FenObject
        {
            public StaticRangeObject(Node startContainer, int startOffset, Node endContainer, int endOffset, IExecutionContext context, FenObject prototype)
            {
                StartContainer = startContainer;
                StartOffset = startOffset;
                EndContainer = endContainer;
                EndOffset = endOffset;
                NativeObject = this;
                InternalClass = "StaticRange";
                SetPrototype(prototype);

                DefineOwnProperty("startContainer", PropertyDescriptor.Accessor(
                    new FenFunction("get startContainer", (_, _) => DomWrapperFactory.Wrap(StartContainer, context)),
                    null,
                    enumerable: true,
                    configurable: true));
                DefineOwnProperty("startOffset", PropertyDescriptor.Accessor(
                    new FenFunction("get startOffset", (_, _) => FenValue.FromNumber(StartOffset)),
                    null,
                    enumerable: true,
                    configurable: true));
                DefineOwnProperty("endContainer", PropertyDescriptor.Accessor(
                    new FenFunction("get endContainer", (_, _) => DomWrapperFactory.Wrap(EndContainer, context)),
                    null,
                    enumerable: true,
                    configurable: true));
                DefineOwnProperty("endOffset", PropertyDescriptor.Accessor(
                    new FenFunction("get endOffset", (_, _) => FenValue.FromNumber(EndOffset)),
                    null,
                    enumerable: true,
                    configurable: true));
                DefineOwnProperty("collapsed", PropertyDescriptor.Accessor(
                    new FenFunction("get collapsed", (_, _) => FenValue.FromBoolean(ReferenceEquals(StartContainer, EndContainer) && StartOffset == EndOffset)),
                    null,
                    enumerable: true,
                    configurable: true));
            }

            public Node StartContainer { get; }
            public int StartOffset { get; }
            public Node EndContainer { get; }
            public int EndOffset { get; }
        }

        private enum HighlightIteratorKind
        {
            Values,
            Entries
        }

        private sealed class TextRangeSegment
        {
            public required Text TextNode { get; init; }
            public required int StartOffset { get; init; }
            public required int EndOffset { get; init; }
        }

        private sealed class HighlightObject : FenObject
        {
            private sealed class Slot
            {
                public required FenValue Value { get; init; }
                public bool Deleted { get; set; }
            }

            private readonly List<Slot> _slots = new();
            private readonly IExecutionContext _context;
            private int _size;
            private double _priority;
            private string _type = "highlight";

            public HighlightObject(IExecutionContext context, FenObject prototype)
            {
                _context = context;
                NativeObject = this;
                InternalClass = "Highlight";
                SetPrototype(prototype);

                DefineOwnProperty("size", PropertyDescriptor.Accessor(
                    new FenFunction("get size", (_, _) => FenValue.FromNumber(_size)),
                    null,
                    enumerable: true,
                    configurable: true));
                DefineOwnProperty("priority", PropertyDescriptor.Accessor(
                    new FenFunction("get priority", (_, _) => FenValue.FromNumber(_priority)),
                    new FenFunction("set priority", (args, _) =>
                    {
                        _priority = args.Length > 0 && args[0].IsNumber ? args[0].ToNumber() : 0;
                        return FenValue.Undefined;
                    }),
                    enumerable: true,
                    configurable: true));
                DefineOwnProperty("type", PropertyDescriptor.Accessor(
                    new FenFunction("get type", (_, _) => FenValue.FromString(_type)),
                    new FenFunction("set type", (args, _) =>
                    {
                        var requested = args.Length > 0 ? args[0].ToString() : string.Empty;
                        if (requested == "highlight" || requested == "spelling-error" || requested == "grammar-error")
                        {
                            _type = requested;
                        }

                        return FenValue.Undefined;
                    }),
                    enumerable: true,
                    configurable: true));

                SetBuiltin("add", FenValue.FromFunction(new FenFunction("add", (args, _) =>
                {
                    AddRange(args.Length > 0 ? args[0] : FenValue.Undefined);
                    return FenValue.FromObject(this);
                })));
                SetBuiltin("has", FenValue.FromFunction(new FenFunction("has", (args, _) =>
                    FenValue.FromBoolean(FindActiveSlot(args.Length > 0 ? args[0] : FenValue.Undefined) >= 0))));
                SetBuiltin("delete", FenValue.FromFunction(new FenFunction("delete", (args, _) =>
                    FenValue.FromBoolean(DeleteRange(args.Length > 0 ? args[0] : FenValue.Undefined)))));
                SetBuiltin("clear", FenValue.FromFunction(new FenFunction("clear", (_, _) =>
                {
                    ClearRanges();
                    return FenValue.Undefined;
                })));
                SetBuiltin("keys", FenValue.FromFunction(new FenFunction("keys", (_, _) => CreateSnapshotIterator(includeEntries: false, keysOnly: true))));
                SetBuiltin("values", FenValue.FromFunction(new FenFunction("values", (_, _) => CreateSnapshotIterator(includeEntries: false, keysOnly: false))));
                SetBuiltin("entries", FenValue.FromFunction(new FenFunction("entries", (_, _) => CreateSnapshotIterator(includeEntries: true, keysOnly: false))));
                SetBuiltin("forEach", FenValue.FromFunction(new FenFunction("forEach", ForEach)));
                SetBuiltin(JsSymbol.Iterator.ToPropertyKey(), FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, _) => FenValue.FromObject(CreateIterator(HighlightIteratorKind.Values)))));
            }

            public void AddRange(FenValue value)
            {
                if (!IsHighlightRange(value))
                {
                    throw new FenTypeError("TypeError: Failed to execute 'add' on 'Highlight': parameter 1 is not of type 'AbstractRange'.");
                }

                if (FindActiveSlot(value) >= 0)
                {
                    return;
                }

                _slots.Add(new Slot { Value = value });
                _size++;
            }

            public double Priority => _priority;

            public IReadOnlyList<FenValue> GetRangesContainingPoint(double x, double y)
            {
                var matches = new List<FenValue>();
                foreach (var slot in _slots)
                {
                    if (slot.Deleted)
                    {
                        continue;
                    }

                    if (RangeContainsPoint(slot.Value, x, y, _context))
                    {
                        matches.Add(slot.Value);
                    }
                }

                return matches;
            }

            private int FindActiveSlot(FenValue value)
            {
                for (var i = 0; i < _slots.Count; i++)
                {
                    if (!_slots[i].Deleted && _slots[i].Value.StrictEquals(value))
                    {
                        return i;
                    }
                }

                return -1;
            }

            private bool DeleteRange(FenValue value)
            {
                var index = FindActiveSlot(value);
                if (index < 0)
                {
                    return false;
                }

                _slots[index].Deleted = true;
                _size--;
                return true;
            }

            private void ClearRanges()
            {
                for (var i = 0; i < _slots.Count; i++)
                {
                    _slots[i].Deleted = true;
                }

                _size = 0;
            }

            private FenValue ForEach(FenValue[] args, FenValue thisVal)
            {
                if (args.Length == 0 || !args[0].IsFunction)
                {
                    return FenValue.Undefined;
                }

                var callback = args[0].AsFunction();
                for (var index = 0; index < _slots.Count; index++)
                {
                    var slot = _slots[index];
                    if (slot.Deleted)
                    {
                        continue;
                    }

                    callback.Invoke(new[] { slot.Value, slot.Value, FenValue.FromObject(this) }, _context);
                }

                return FenValue.Undefined;
            }

            private FenObject CreateIterator(HighlightIteratorKind kind)
            {
                var index = 0;
                var iterator = new FenObject();
                iterator.SetPrototype(FenObject.DefaultIteratorPrototype);
                iterator.SetBuiltin(JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("Set Iterator"));
                iterator.SetBuiltin(JsSymbol.Iterator.ToPropertyKey(), FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, _) => FenValue.FromObject(iterator))));
                iterator.SetBuiltin("next", FenValue.FromFunction(new FenFunction("next", (_, _) =>
                {
                    while (index < _slots.Count)
                    {
                        var slot = _slots[index++];
                        if (slot.Deleted)
                        {
                            continue;
                        }

                        return FenValue.FromObject(CreateIteratorStep(Serialize(slot.Value, kind), done: false));
                    }

                    return FenValue.FromObject(CreateIteratorStep(FenValue.Undefined, done: true));
                })));
                return iterator;
            }

            private FenValue CreateSnapshotIterator(bool includeEntries, bool keysOnly)
            {
                var items = new List<FenValue>();
                foreach (var slot in _slots)
                {
                    if (slot.Deleted)
                    {
                        continue;
                    }

                    if (includeEntries)
                    {
                        var pair = FenObject.CreateArray();
                        pair.Set("0", slot.Value);
                        pair.Set("1", slot.Value);
                        pair.Set("length", FenValue.FromNumber(2));
                        items.Add(FenValue.FromObject(pair));
                    }
                    else
                    {
                        items.Add(slot.Value);
                    }
                }

                return FenValue.FromObject(CreateSnapshotArrayIterator(items));
            }

            private static FenValue Serialize(FenValue value, HighlightIteratorKind kind)
            {
                if (kind == HighlightIteratorKind.Values)
                {
                    return value;
                }

                var pair = FenObject.CreateArray();
                pair.Set("0", value);
                pair.Set("1", value);
                pair.Set("length", FenValue.FromNumber(2));
                return FenValue.FromObject(pair);
            }
        }

        private enum MapIteratorKind
        {
            Keys,
            Values,
            Entries
        }

        private sealed class HighlightRegistryObject : FenObject
        {
            private sealed class Entry
            {
                public required string Key { get; init; }
                public required FenValue Value { get; set; }
                public bool Deleted { get; set; }
            }

            private readonly List<Entry> _entries = new();
            private readonly IExecutionContext _context;
            private int _size;
            private int _spreadEntryCount;

            public HighlightRegistryObject(IExecutionContext context, FenObject prototype)
            {
                _context = context;
                NativeObject = this;
                InternalClass = "HighlightRegistry";
                SetPrototype(prototype);

                DefineOwnProperty("size", PropertyDescriptor.Accessor(
                    new FenFunction("get size", (_, _) => FenValue.FromNumber(_size)),
                    null,
                    enumerable: true,
                    configurable: true));

                SetBuiltin("set", FenValue.FromFunction(new FenFunction("set", SetEntry)));
                SetBuiltin("get", FenValue.FromFunction(new FenFunction("get", GetEntry)));
                SetBuiltin("has", FenValue.FromFunction(new FenFunction("has", HasEntry)));
                SetBuiltin("delete", FenValue.FromFunction(new FenFunction("delete", DeleteEntry)));
                SetBuiltin("clear", FenValue.FromFunction(new FenFunction("clear", ClearEntries)));
                SetBuiltin("highlightsFromPoint", FenValue.FromFunction(new FenFunction("highlightsFromPoint", HighlightsFromPoint)));
                SetBuiltin("keys", FenValue.FromFunction(new FenFunction("keys", (_, _) => CreateSnapshotIterator(MapIteratorKind.Keys))));
                SetBuiltin("values", FenValue.FromFunction(new FenFunction("values", (_, _) => CreateSnapshotIterator(MapIteratorKind.Values))));
                SetBuiltin("entries", FenValue.FromFunction(new FenFunction("entries", (_, _) => CreateSnapshotIterator(MapIteratorKind.Entries))));
                SetBuiltin("forEach", FenValue.FromFunction(new FenFunction("forEach", ForEach)));
                SetBuiltin(JsSymbol.Iterator.ToPropertyKey(), FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, _) => FenValue.FromObject(CreateIterator(MapIteratorKind.Entries)))));
                RefreshSpreadView();
            }

            private FenValue SetEntry(FenValue[] args, FenValue thisVal)
            {
                var name = args.Length > 0 ? args[0].ToString() : string.Empty;
                var highlight = args.Length > 1 ? args[1] : FenValue.Undefined;
                if (!highlight.IsObject || highlight.AsObject() is not HighlightObject)
                {
                    throw new FenTypeError("TypeError: Failed to execute 'set' on 'HighlightRegistry': parameter 2 is not of type 'Highlight'.");
                }

                var existing = FindActiveEntry(name);
                if (existing >= 0)
                {
                    _entries[existing].Value = highlight;
                }
                else
                {
                    _entries.Add(new Entry { Key = name, Value = highlight });
                    _size++;
                }

                RefreshSpreadView();
                return FenValue.FromObject(this);
            }

            private FenValue GetEntry(FenValue[] args, FenValue thisVal)
            {
                var name = args.Length > 0 ? args[0].ToString() : string.Empty;
                var existing = FindActiveEntry(name);
                return existing >= 0 ? _entries[existing].Value : FenValue.Undefined;
            }

            private FenValue HasEntry(FenValue[] args, FenValue thisVal)
            {
                var name = args.Length > 0 ? args[0].ToString() : string.Empty;
                return FenValue.FromBoolean(FindActiveEntry(name) >= 0);
            }

            private FenValue DeleteEntry(FenValue[] args, FenValue thisVal)
            {
                var name = args.Length > 0 ? args[0].ToString() : string.Empty;
                var existing = FindActiveEntry(name);
                if (existing < 0)
                {
                    return FenValue.FromBoolean(false);
                }

                _entries[existing].Deleted = true;
                _size--;
                RefreshSpreadView();
                return FenValue.FromBoolean(true);
            }

            private FenValue ClearEntries(FenValue[] args, FenValue thisVal)
            {
                for (var i = 0; i < _entries.Count; i++)
                {
                    _entries[i].Deleted = true;
                }

                _size = 0;
                RefreshSpreadView();
                return FenValue.Undefined;
            }

            private FenValue ForEach(FenValue[] args, FenValue thisVal)
            {
                if (args.Length == 0 || !args[0].IsFunction)
                {
                    return FenValue.Undefined;
                }

                var callback = args[0].AsFunction();
                for (var index = 0; index < _entries.Count; index++)
                {
                    var entry = _entries[index];
                    if (entry.Deleted)
                    {
                        continue;
                    }

                    callback.Invoke(new[] { entry.Value, FenValue.FromString(entry.Key), FenValue.FromObject(this) }, _context);
                }

                return FenValue.Undefined;
            }

            private int FindActiveEntry(string name)
            {
                for (var i = 0; i < _entries.Count; i++)
                {
                    if (!_entries[i].Deleted && string.Equals(_entries[i].Key, name, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }

                return -1;
            }

            private FenValue HighlightsFromPoint(FenValue[] args, FenValue thisVal)
            {
                if (args.Length < 2 || !args[0].IsNumber || !args[1].IsNumber)
                {
                    throw new FenTypeError("TypeError: Failed to execute 'highlightsFromPoint' on 'HighlightRegistry': 2 numeric arguments required.");
                }

                if (args.Length > 2 && !args[2].IsUndefined && !args[2].IsObject)
                {
                    throw new FenTypeError("TypeError: Failed to execute 'highlightsFromPoint' on 'HighlightRegistry': optional argument must be an object.");
                }

                var x = args[0].ToNumber();
                var y = args[1].ToNumber();
                if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
                {
                    throw new FenTypeError("TypeError: Failed to execute 'highlightsFromPoint' on 'HighlightRegistry': coordinates must be finite numbers.");
                }

                var hits = new List<(HighlightObject Highlight, IReadOnlyList<FenValue> Ranges, int RegistrationOrder)>();
                for (var i = 0; i < _entries.Count; i++)
                {
                    var entry = _entries[i];
                    if (entry.Deleted || entry.Value.AsObject() is not HighlightObject highlight)
                    {
                        continue;
                    }

                    var matchingRanges = highlight.GetRangesContainingPoint(x, y);
                    if (matchingRanges.Count == 0)
                    {
                        continue;
                    }

                    hits.Add((highlight, matchingRanges, i));
                }

                hits.Sort((left, right) =>
                {
                    var priorityCompare = right.Highlight.Priority.CompareTo(left.Highlight.Priority);
                    return priorityCompare != 0 ? priorityCompare : right.RegistrationOrder.CompareTo(left.RegistrationOrder);
                });

                var results = FenObject.CreateArray();
                for (var i = 0; i < hits.Count; i++)
                {
                    var result = new FenObject();
                    result.Set("highlight", FenValue.FromObject(hits[i].Highlight));

                    var ranges = FenObject.CreateArray();
                    for (var j = 0; j < hits[i].Ranges.Count; j++)
                    {
                        ranges.Set(j.ToString(), hits[i].Ranges[j]);
                    }

                    ranges.Set("length", FenValue.FromNumber(hits[i].Ranges.Count));
                    result.Set("ranges", FenValue.FromObject(ranges));
                    results.Set(i.ToString(), FenValue.FromObject(result));
                }

                results.Set("length", FenValue.FromNumber(hits.Count));
                return FenValue.FromObject(results);
            }

            private FenObject CreateIterator(MapIteratorKind kind)
            {
                var index = 0;
                var iterator = new FenObject();
                iterator.SetPrototype(FenObject.DefaultIteratorPrototype);
                iterator.SetBuiltin(JsSymbol.ToStringTag.ToPropertyKey(), FenValue.FromString("Map Iterator"));
                iterator.SetBuiltin(JsSymbol.Iterator.ToPropertyKey(), FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, _) => FenValue.FromObject(iterator))));
                iterator.SetBuiltin("next", FenValue.FromFunction(new FenFunction("next", (_, _) =>
                {
                    while (index < _entries.Count)
                    {
                        var entry = _entries[index++];
                        if (entry.Deleted)
                        {
                            continue;
                        }

                        return FenValue.FromObject(CreateIteratorStep(Serialize(entry, kind), done: false));
                    }

                    return FenValue.FromObject(CreateIteratorStep(FenValue.Undefined, done: true));
                })));
                return iterator;
            }

            private FenValue CreateSnapshotIterator(MapIteratorKind kind)
            {
                var items = new List<FenValue>();
                foreach (var entry in _entries)
                {
                    if (entry.Deleted)
                    {
                        continue;
                    }

                    items.Add(Serialize(entry, kind));
                }

                return FenValue.FromObject(CreateSnapshotArrayIterator(items));
            }

            private static FenValue Serialize(Entry entry, MapIteratorKind kind)
            {
                return kind switch
                {
                    MapIteratorKind.Keys => FenValue.FromString(entry.Key),
                    MapIteratorKind.Values => entry.Value,
                    _ => FenValue.FromObject(CreatePair(entry.Key, entry.Value))
                };
            }

            private void RefreshSpreadView()
            {
                for (var i = 0; i < _spreadEntryCount; i++)
                {
                    Delete(i.ToString());
                }

                var index = 0;
                foreach (var entry in _entries)
                {
                    if (entry.Deleted)
                    {
                        continue;
                    }

                    Set(index.ToString(), Serialize(entry, MapIteratorKind.Entries));
                    index++;
                }

                Set("length", FenValue.FromNumber(index));
                _spreadEntryCount = index;
            }
        }

        private static FenObject CreatePair(string key, FenValue value)
        {
            var pair = FenObject.CreateArray();
            pair.Set("0", FenValue.FromString(key));
            pair.Set("1", value);
            pair.Set("length", FenValue.FromNumber(2));
            return pair;
        }

        private static FenObject CreateIteratorStep(FenValue value, bool done)
        {
            var step = new FenObject();
            step.Set("value", value);
            step.Set("done", FenValue.FromBoolean(done));
            return step;
        }

        private static bool RangeContainsPoint(FenValue rangeValue, double x, double y, IExecutionContext context)
        {
            foreach (var segment in GetTextRangeSegments(rangeValue))
            {
                var parent = segment.TextNode.ParentNode as Element;
                if (parent == null || !TryGetBox(parent, context, out var box))
                {
                    continue;
                }

                var textLength = segment.TextNode.TextContent?.Length ?? 0;
                if (textLength <= 0 || box.Width <= 0 || box.Height <= 0)
                {
                    continue;
                }

                var clampedStart = Math.Max(0, Math.Min(segment.StartOffset, textLength));
                var clampedEnd = Math.Max(clampedStart, Math.Min(segment.EndOffset, textLength));
                if (clampedStart == clampedEnd)
                {
                    continue;
                }

                var characterWidth = box.Width / textLength;
                var left = box.Left + clampedStart * characterWidth;
                var right = box.Left + clampedEnd * characterWidth;
                var top = box.Top;
                var bottom = box.Top + box.Height;
                if (x >= left && x < right && y >= top && y < bottom)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<TextRangeSegment> GetTextRangeSegments(FenValue rangeValue)
        {
            if (!TryGetTextRangeEndpoints(rangeValue, out var startNode, out var startOffset, out var endNode, out var endOffset))
            {
                yield break;
            }

            if (ReferenceEquals(startNode, endNode))
            {
                yield return new TextRangeSegment
                {
                    TextNode = startNode,
                    StartOffset = startOffset,
                    EndOffset = endOffset
                };
                yield break;
            }

            yield return new TextRangeSegment
            {
                TextNode = startNode,
                StartOffset = startOffset,
                EndOffset = startNode.TextContent?.Length ?? 0
            };
            yield return new TextRangeSegment
            {
                TextNode = endNode,
                StartOffset = 0,
                EndOffset = endOffset
            };
        }

        private static bool TryGetTextRangeEndpoints(
            FenValue rangeValue,
            out Text startNode,
            out int startOffset,
            out Text endNode,
            out int endOffset)
        {
            startNode = null!;
            endNode = null!;
            startOffset = 0;
            endOffset = 0;

            switch (rangeValue.AsObject())
            {
                case RangeWrapper rangeWrapper when rangeWrapper.NativeObject is FenBrowser.Core.Dom.V2.Range liveRange:
                    startNode = liveRange.StartContainer as Text;
                    endNode = liveRange.EndContainer as Text;
                    startOffset = liveRange.StartOffset;
                    endOffset = liveRange.EndOffset;
                    break;
                case StaticRangeObject staticRange:
                    startNode = staticRange.StartContainer as Text;
                    endNode = staticRange.EndContainer as Text;
                    startOffset = staticRange.StartOffset;
                    endOffset = staticRange.EndOffset;
                    break;
            }

            return startNode != null && endNode != null;
        }

        private static bool TryGetBox(Element element, IExecutionContext context, out SKRect box)
        {
            box = default;
            var layoutEngine = context.GetLayoutEngine();
            var anchorBox = layoutEngine?.GetBoxForNode(element);
            if (anchorBox.HasValue && anchorBox.Value.Width > 0 && anchorBox.Value.Height > 0)
            {
                box = anchorBox.Value;
                return true;
            }

            var synthetic = TryCreateSyntheticTextRect(element, anchorBox);
            if (synthetic.HasValue)
            {
                box = synthetic.Value;
                return true;
            }

            if (!anchorBox.HasValue)
            {
                return false;
            }

            box = anchorBox.Value;
            return box.Width > 0 || box.Height > 0;
        }

        private static SKRect? TryCreateSyntheticTextRect(Element element, SKRect? anchorBox)
        {
            var textContent = element.TextContent ?? string.Empty;
            if (string.IsNullOrWhiteSpace(textContent))
            {
                return anchorBox;
            }

            var (left, top) = GetSyntheticInlineOrigin(element, anchorBox);
            var width = Math.Max(anchorBox?.Width ?? 0, Math.Max(1, textContent.Length) * 10);
            var height = Math.Max(anchorBox?.Height ?? 0, 16);
            return SKRect.Create(left, top, width, height);
        }

        private static (float Left, float Top) GetSyntheticInlineOrigin(Element element, SKRect? anchorBox)
        {
            if (anchorBox.HasValue)
            {
                return (anchorBox.Value.Left, anchorBox.Value.Top);
            }

            float left = 0;
            float top = 0;
            if (element.ParentNode is Element parent)
            {
                foreach (var sibling in parent.ChildNodes)
                {
                    if (ReferenceEquals(sibling, element))
                    {
                        break;
                    }

                    if (sibling is Element siblingElement)
                    {
                        if (string.Equals(siblingElement.TagName, "br", StringComparison.OrdinalIgnoreCase))
                        {
                            top += 16;
                            left = 0;
                            continue;
                        }

                        left += EstimateSyntheticWidth(siblingElement);
                    }
                    else if (sibling.NodeType == NodeType.Text)
                    {
                        left += Math.Max(1, sibling.TextContent?.Length ?? 0) * 10;
                    }
                }
            }

            return (left, top);
        }

        private static float EstimateSyntheticWidth(Element element)
        {
            if (string.Equals(element.TagName, "br", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return Math.Max(1, element.TextContent?.Length ?? 0) * 10;
        }

        private static FenObject CreateSnapshotArrayIterator(IReadOnlyList<FenValue> items)
        {
            var snapshot = FenObject.CreateArray();
            for (var i = 0; i < items.Count; i++)
            {
                snapshot.Set(i.ToString(), items[i]);
            }

            snapshot.Set("length", FenValue.FromNumber(items.Count));
            var index = 0;
            snapshot.SetBuiltin("next", FenValue.FromFunction(new FenFunction("next", (_, _) =>
            {
                if (index < items.Count)
                {
                    return FenValue.FromObject(CreateIteratorStep(items[index++], done: false));
                }

                return FenValue.FromObject(CreateIteratorStep(FenValue.Undefined, done: true));
            })));
            snapshot.SetBuiltin(JsSymbol.Iterator.ToPropertyKey(), FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (_, _) => FenValue.FromObject(snapshot))));
            return snapshot;
        }
    }
}
