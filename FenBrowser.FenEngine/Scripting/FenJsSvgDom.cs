using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Svg;
using FenBrowser.Js.Host;
using FenBrowser.Js.Runtime;
using SkiaSharp;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The SVG DOM transform interfaces (SVG 2 §8.13–8.16, §4.5.10 "List interfaces"):
/// SVGTransform, SVGTransformList, SVGAnimatedTransformList and SVGMatrix, the
/// transform, gradientTransform and patternTransform reflections, and the
/// SVGSVGElement factories. A list reflects its attribute both ways: it re-parses
/// the attribute (with the renderer's own transform grammar) whenever the attribute
/// changed since the list last looked, and every list or item mutation serializes
/// the whole list back into the attribute, so rendering only ever sees attributes.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private const int SvgTransformUnknown = 0;
    private const int SvgTransformMatrix = 1;
    private const int SvgTransformTranslate = 2;
    private const int SvgTransformScale = 3;
    private const int SvgTransformRotate = 4;
    private const int SvgTransformSkewX = 5;
    private const int SvgTransformSkewY = 6;

    /// <summary>Upper bound on list length, so a script cannot grow an attribute without limit.</summary>
    private const int MaxSvgTransformListItems = 4096;

    private static readonly HashSet<string> SvgTransformableElements = new(StringComparer.Ordinal)
    {
        "a", "circle", "clipPath", "ellipse", "foreignObject", "g", "image", "line", "path",
        "polygon", "polyline", "rect", "svg", "switch", "text", "textPath", "tspan", "use"
    };

    private readonly ConditionalWeakTable<Element, Dictionary<string, SvgAnimatedTransformListHost>> _svgTransformLists = new();

    private abstract class FenJsSvgDomHost
    {
        public abstract string InterfaceName { get; }
    }

    private sealed class SvgAnimatedTransformListHost : FenJsSvgDomHost
    {
        public SvgAnimatedTransformListHost(Element element, string attribute)
        {
            BaseVal = new SvgTransformListHost(element, attribute, readOnly: false);
            AnimVal = new SvgTransformListHost(element, attribute, readOnly: true);
        }

        public override string InterfaceName => "SVGAnimatedTransformList";

        public SvgTransformListHost BaseVal { get; }

        /// <summary>No transform animation is exposed to the DOM, so animVal mirrors the attribute.</summary>
        public SvgTransformListHost AnimVal { get; }
    }

    private sealed class SvgTransformListHost : FenJsSvgDomHost
    {
        private readonly List<SvgTransformHost> _items = new();
        private string _snapshot;
        private bool _synchronized;

        public SvgTransformListHost(Element element, string attribute, bool readOnly)
        {
            Element = element;
            Attribute = attribute;
            ReadOnly = readOnly;
        }

        public override string InterfaceName => "SVGTransformList";

        public Element Element { get; }

        public string Attribute { get; }

        public bool ReadOnly { get; }

        /// <summary>The items, re-read from the attribute when it changed since the last look.</summary>
        public List<SvgTransformHost> Items
        {
            get
            {
                Synchronize();
                return _items;
            }
        }

        private void Synchronize()
        {
            string current = Element.GetAttribute(Attribute);
            if (_synchronized && string.Equals(current, _snapshot, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var item in _items)
            {
                item.Owner = null;
            }
            _items.Clear();

            // An unparsable value is an empty list (SVG 2 §4.5.10: the list is
            // re-synchronized from the attribute; errors yield an empty list).
            var functions = new List<SvgTransformFunction>();
            if (current != null && SvgValues.TryParseTransformFunctions(current.AsSpan(), functions))
            {
                int count = Math.Min(functions.Count, MaxSvgTransformListItems);
                for (int i = 0; i < count; i++)
                {
                    _items.Add(new SvgTransformHost(functions[i]) { Owner = this });
                }
            }

            _snapshot = current;
            _synchronized = true;
        }

        /// <summary>Serializes the list into its attribute after any list or item change.</summary>
        public void Commit()
        {
            var text = new StringBuilder();
            foreach (var item in _items)
            {
                if (text.Length > 0)
                {
                    text.Append(' ');
                }
                item.AppendSerialization(text);
            }

            string serialized = text.ToString();
            _snapshot = serialized;
            _synchronized = true;
            Element.SetAttribute(Attribute, serialized);
        }
    }

    private sealed class SvgTransformHost : FenJsSvgDomHost
    {
        public SvgTransformHost(SvgTransformFunction function)
        {
            Function = function;
        }

        public override string InterfaceName => "SVGTransform";

        public SvgTransformFunction Function { get; private set; }

        /// <summary>The list this item belongs to, or null when detached.</summary>
        public SvgTransformListHost Owner { get; set; }

        public bool ReadOnly => Owner?.ReadOnly == true;

        public SvgMatrixHost MatrixView { get; set; }

        public int Type => Function.Kind switch
        {
            SvgTransformKind.Matrix => SvgTransformMatrix,
            SvgTransformKind.Translate => SvgTransformTranslate,
            SvgTransformKind.Scale => SvgTransformScale,
            SvgTransformKind.Rotate => SvgTransformRotate,
            SvgTransformKind.SkewX => SvgTransformSkewX,
            SvgTransformKind.SkewY => SvgTransformSkewY,
            _ => SvgTransformUnknown
        };

        public float Angle => Function.Kind is SvgTransformKind.Rotate or SvgTransformKind.SkewX or SvgTransformKind.SkewY
            ? Function.Arguments[0]
            : 0f;

        public SKMatrix Matrix => SvgValues.TryCreateTransformMatrix(Function, out SKMatrix matrix)
            ? matrix
            : SKMatrix.Identity;

        public void Set(SvgTransformKind kind, params float[] arguments)
        {
            Function = new SvgTransformFunction(kind, arguments);
            Owner?.Commit();
        }

        public void SetMatrix(SKMatrix m) =>
            Set(SvgTransformKind.Matrix, m.ScaleX, m.SkewY, m.SkewX, m.ScaleY, m.TransX, m.TransY);

        public SvgTransformHost Copy() => new(new SvgTransformFunction(Function.Kind, (float[])Function.Arguments.Clone()));

        public void AppendSerialization(StringBuilder text)
        {
            float[] a = Function.Arguments;
            text.Append(Function.Kind switch
            {
                SvgTransformKind.Matrix => "matrix(",
                SvgTransformKind.Translate => "translate(",
                SvgTransformKind.Scale => "scale(",
                SvgTransformKind.Rotate => "rotate(",
                SvgTransformKind.SkewX => "skewX(",
                _ => "skewY("
            });
            for (int i = 0; i < a.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(' ');
                }
                text.Append(a[i].ToString("R", CultureInfo.InvariantCulture));
            }
            text.Append(')');
        }
    }

    /// <summary>
    /// SVGMatrix. A matrix obtained from SVGTransform.matrix is a live view: writing
    /// a component turns the transform into a matrix transform. Any other matrix owns
    /// its value.
    /// </summary>
    private sealed class SvgMatrixHost : FenJsSvgDomHost
    {
        private SKMatrix _value;

        public SvgMatrixHost(SKMatrix value) => _value = value;

        public SvgMatrixHost(SvgTransformHost transform) => Transform = transform;

        public override string InterfaceName => "SVGMatrix";

        public SvgTransformHost Transform { get; }

        public bool ReadOnly => Transform?.ReadOnly == true;

        public SKMatrix Value
        {
            get => Transform?.Matrix ?? _value;
            set
            {
                if (Transform != null)
                {
                    Transform.SetMatrix(value);
                }
                else
                {
                    _value = value;
                }
            }
        }
    }

    /// <summary>
    /// The SVG DOM properties of an SVG element: the animated transform lists and the
    /// SVGSVGElement transform/matrix factories. False for anything else.
    /// </summary>
    private bool TryGetSvgElementProperty(Element element, string property, out JsValue value)
    {
        value = JsValue.Undefined;
        if (element?.NamespaceUri != Namespaces.Svg)
        {
            return false;
        }

        string localName = element.LocalName;
        string attribute = property switch
        {
            "transform" when SvgTransformableElements.Contains(localName) => "transform",
            "gradientTransform" when localName is "linearGradient" or "radialGradient" => "gradientTransform",
            "patternTransform" when localName == "pattern" => "patternTransform",
            _ => null
        };
        if (attribute != null)
        {
            var lists = _svgTransformLists.GetOrCreateValue(element);
            if (!lists.TryGetValue(attribute, out var animated))
            {
                animated = new SvgAnimatedTransformListHost(element, attribute);
                lists[attribute] = animated;
            }
            value = ToHostOrNull(animated, HostObjectKind.Other);
            return true;
        }

        if (localName != "svg")
        {
            return false;
        }

        switch (property)
        {
            case "createSVGTransform":
                value = GetOrCreateHostCallable(element, property,
                    (_, _) => ToHostOrNull(new SvgTransformHost(IdentityMatrixFunction()), HostObjectKind.Other));
                return true;
            case "createSVGTransformFromMatrix":
                value = GetOrCreateHostCallable(element, property,
                    (_, args) => ToHostOrNull(CreateTransformFromMatrixArgument(args), HostObjectKind.Other), length: 1);
                return true;
            case "createSVGMatrix":
                value = GetOrCreateHostCallable(element, property,
                    (_, _) => ToHostOrNull(new SvgMatrixHost(SKMatrix.Identity), HostObjectKind.Other));
                return true;
            default:
                return false;
        }
    }

    private bool TryGetSvgDomProperty(FenJsSvgDomHost host, string property, out JsValue value)
    {
        bool found = host switch
        {
            SvgAnimatedTransformListHost animated => TryGetAnimatedTransformListProperty(animated, property, out value),
            SvgTransformListHost list => TryGetTransformListProperty(list, property, out value),
            SvgTransformHost transform => TryGetTransformProperty(transform, property, out value),
            SvgMatrixHost matrix => TryGetMatrixProperty(matrix, property, out value),
            _ => Undefined(out value)
        };
        if (found)
        {
            return true;
        }

        value = GetStoredHostPropertyOrUndefined(host, property);
        return value.Tag != JsValueTag.Undefined;
    }

    /// <summary>
    /// Assignments to SVG DOM objects. Only SVGMatrix components are writable; other
    /// names are expandos. A read-only object rejects writes (NoModificationAllowedError).
    /// </summary>
    private bool TrySetSvgDomProperty(FenJsSvgDomHost host, string property, JsValue value)
    {
        if (host is SvgMatrixHost matrix && property.Length == 1 && property[0] is >= 'a' and <= 'f')
        {
            if (matrix.ReadOnly)
            {
                ThrowDomException("NoModificationAllowedError", "The matrix is read-only.");
            }

            float component = ToSvgFloat(value);
            SKMatrix m = matrix.Value;
            switch (property[0])
            {
                case 'a': m.ScaleX = component; break;
                case 'b': m.SkewY = component; break;
                case 'c': m.SkewX = component; break;
                case 'd': m.ScaleY = component; break;
                case 'e': m.TransX = component; break;
                default: m.TransY = component; break;
            }
            matrix.Value = m;
            return true;
        }

        SetStoredHostProperty(host, property, value);
        return true;
    }

    private bool TryGetAnimatedTransformListProperty(SvgAnimatedTransformListHost animated, string property, out JsValue value)
    {
        switch (property)
        {
            case "baseVal":
                value = ToHostOrNull(animated.BaseVal, HostObjectKind.Other);
                return true;
            case "animVal":
                value = ToHostOrNull(animated.AnimVal, HostObjectKind.Other);
                return true;
            default:
                value = JsValue.Undefined;
                return false;
        }
    }

    private bool TryGetTransformListProperty(SvgTransformListHost list, string property, out JsValue value)
    {
        switch (property)
        {
            case "numberOfItems":
            case "length":
                value = JsValue.FromNumber(list.Items.Count);
                return true;
            case "clear":
                value = GetOrCreateHostCallable(list, property, (_, _) =>
                {
                    RequireWritable(list);
                    foreach (var item in list.Items)
                    {
                        item.Owner = null;
                    }
                    list.Items.Clear();
                    list.Commit();
                    return JsValue.Undefined;
                });
                return true;
            case "initialize":
                value = GetOrCreateHostCallable(list, property, (_, args) =>
                {
                    RequireWritable(list);
                    var item = AdoptTransformArgument(args, 0);
                    foreach (var existing in list.Items)
                    {
                        existing.Owner = null;
                    }
                    list.Items.Clear();
                    return InsertTransform(list, item, 0);
                }, length: 1);
                return true;
            case "getItem":
                value = GetOrCreateHostCallable(list, property, (_, args) =>
                    ToHostOrNull(list.Items[RequireListIndex(list, args, 0, allowEnd: false)], HostObjectKind.Other), length: 1);
                return true;
            case "insertItemBefore":
                value = GetOrCreateHostCallable(list, property, (_, args) =>
                {
                    RequireWritable(list);
                    var item = AdoptTransformArgument(args, 0);
                    // An index past the end appends (SVG 2 §4.5.10 insertItemBefore step 3).
                    double index = args.Count > 1 ? CoerceToFiniteNumber(args[1], 0) : 0;
                    int position = index < 0 || index > list.Items.Count ? list.Items.Count : (int)index;
                    return InsertTransform(list, item, position);
                }, length: 2);
                return true;
            case "replaceItem":
                value = GetOrCreateHostCallable(list, property, (_, args) =>
                {
                    RequireWritable(list);
                    var item = AdoptTransformArgument(args, 0);
                    int index = RequireListIndex(list, args, 1, allowEnd: false);
                    list.Items[index].Owner = null;
                    item.Owner = list;
                    list.Items[index] = item;
                    list.Commit();
                    return ToHostOrNull(item, HostObjectKind.Other);
                }, length: 2);
                return true;
            case "removeItem":
                value = GetOrCreateHostCallable(list, property, (_, args) =>
                {
                    RequireWritable(list);
                    int index = RequireListIndex(list, args, 0, allowEnd: false);
                    var removed = list.Items[index];
                    list.Items.RemoveAt(index);
                    removed.Owner = null;
                    list.Commit();
                    return ToHostOrNull(removed, HostObjectKind.Other);
                }, length: 1);
                return true;
            case "appendItem":
                value = GetOrCreateHostCallable(list, property, (_, args) =>
                {
                    RequireWritable(list);
                    var item = AdoptTransformArgument(args, 0);
                    return InsertTransform(list, item, list.Items.Count);
                }, length: 1);
                return true;
            case "createSVGTransformFromMatrix":
                value = GetOrCreateHostCallable(list, property,
                    (_, args) => ToHostOrNull(CreateTransformFromMatrixArgument(args), HostObjectKind.Other), length: 1);
                return true;
            case "consolidate":
                value = GetOrCreateHostCallable(list, property, (_, _) =>
                {
                    RequireWritable(list);
                    var items = list.Items;
                    if (items.Count == 0)
                    {
                        return JsValue.Null;
                    }

                    var functions = new List<SvgTransformFunction>(items.Count);
                    foreach (var item in items)
                    {
                        functions.Add(item.Function);
                        item.Owner = null;
                    }
                    SvgValues.TryComposeTransformFunctions(functions, out SKMatrix composed);
                    var consolidated = new SvgTransformHost(IdentityMatrixFunction());
                    consolidated.SetMatrix(composed);
                    items.Clear();
                    return InsertTransform(list, consolidated, 0);
                });
                return true;
            default:
                if (TryParseArrayIndex(property, out int itemIndex))
                {
                    value = itemIndex < list.Items.Count
                        ? ToHostOrNull(list.Items[itemIndex], HostObjectKind.Other)
                        : JsValue.Undefined;
                    return itemIndex < list.Items.Count;
                }
                value = JsValue.Undefined;
                return false;
        }
    }

    private bool TryGetTransformProperty(SvgTransformHost transform, string property, out JsValue value)
    {
        switch (property)
        {
            case "type":
                value = JsValue.FromNumber(transform.Type);
                return true;
            case "angle":
                value = JsValue.FromNumber(transform.Angle);
                return true;
            case "matrix":
                transform.MatrixView ??= new SvgMatrixHost(transform);
                value = ToHostOrNull(transform.MatrixView, HostObjectKind.Other);
                return true;
            case "SVG_TRANSFORM_UNKNOWN": value = JsValue.FromNumber(SvgTransformUnknown); return true;
            case "SVG_TRANSFORM_MATRIX": value = JsValue.FromNumber(SvgTransformMatrix); return true;
            case "SVG_TRANSFORM_TRANSLATE": value = JsValue.FromNumber(SvgTransformTranslate); return true;
            case "SVG_TRANSFORM_SCALE": value = JsValue.FromNumber(SvgTransformScale); return true;
            case "SVG_TRANSFORM_ROTATE": value = JsValue.FromNumber(SvgTransformRotate); return true;
            case "SVG_TRANSFORM_SKEWX": value = JsValue.FromNumber(SvgTransformSkewX); return true;
            case "SVG_TRANSFORM_SKEWY": value = JsValue.FromNumber(SvgTransformSkewY); return true;
            case "setMatrix":
                value = GetOrCreateHostCallable(transform, property, (_, args) =>
                {
                    RequireWritable(transform);
                    transform.SetMatrix(ReadMatrixArgument(args, 0));
                    return JsValue.Undefined;
                }, length: 1);
                return true;
            case "setTranslate":
                value = GetOrCreateHostCallable(transform, property, (_, args) =>
                {
                    RequireWritable(transform);
                    transform.Set(SvgTransformKind.Translate, FloatArgument(args, 0), FloatArgument(args, 1));
                    return JsValue.Undefined;
                }, length: 2);
                return true;
            case "setScale":
                value = GetOrCreateHostCallable(transform, property, (_, args) =>
                {
                    RequireWritable(transform);
                    transform.Set(SvgTransformKind.Scale, FloatArgument(args, 0), FloatArgument(args, 1));
                    return JsValue.Undefined;
                }, length: 2);
                return true;
            case "setRotate":
                value = GetOrCreateHostCallable(transform, property, (_, args) =>
                {
                    RequireWritable(transform);
                    transform.Set(SvgTransformKind.Rotate,
                        FloatArgument(args, 0), FloatArgument(args, 1), FloatArgument(args, 2));
                    return JsValue.Undefined;
                }, length: 3);
                return true;
            case "setSkewX":
                value = GetOrCreateHostCallable(transform, property, (_, args) =>
                {
                    RequireWritable(transform);
                    transform.Set(SvgTransformKind.SkewX, FloatArgument(args, 0));
                    return JsValue.Undefined;
                }, length: 1);
                return true;
            case "setSkewY":
                value = GetOrCreateHostCallable(transform, property, (_, args) =>
                {
                    RequireWritable(transform);
                    transform.Set(SvgTransformKind.SkewY, FloatArgument(args, 0));
                    return JsValue.Undefined;
                }, length: 1);
                return true;
            default:
                value = JsValue.Undefined;
                return false;
        }
    }

    private bool TryGetMatrixProperty(SvgMatrixHost matrix, string property, out JsValue value)
    {
        SKMatrix m = matrix.Value;
        switch (property)
        {
            case "a": value = JsValue.FromNumber(m.ScaleX); return true;
            case "b": value = JsValue.FromNumber(m.SkewY); return true;
            case "c": value = JsValue.FromNumber(m.SkewX); return true;
            case "d": value = JsValue.FromNumber(m.ScaleY); return true;
            case "e": value = JsValue.FromNumber(m.TransX); return true;
            case "f": value = JsValue.FromNumber(m.TransY); return true;
            case "multiply":
                value = MatrixMethod(matrix, property, 1, (current, args) => SKMatrix.Concat(current, ReadMatrixArgument(args, 0)));
                return true;
            case "inverse":
                value = MatrixMethod(matrix, property, 0, (current, _) =>
                {
                    if (!current.TryInvert(out SKMatrix inverse))
                    {
                        ThrowDomException("InvalidStateError", "The matrix is not invertible.");
                    }
                    return inverse;
                });
                return true;
            case "translate":
                value = MatrixMethod(matrix, property, 2, (current, args) =>
                    SKMatrix.Concat(current, SKMatrix.CreateTranslation(FloatArgument(args, 0), FloatArgument(args, 1))));
                return true;
            case "scale":
                value = MatrixMethod(matrix, property, 1, (current, args) =>
                    SKMatrix.Concat(current, SKMatrix.CreateScale(FloatArgument(args, 0), FloatArgument(args, 0))));
                return true;
            case "scaleNonUniform":
                value = MatrixMethod(matrix, property, 2, (current, args) =>
                    SKMatrix.Concat(current, SKMatrix.CreateScale(FloatArgument(args, 0), FloatArgument(args, 1))));
                return true;
            case "rotate":
                value = MatrixMethod(matrix, property, 1, (current, args) =>
                    ComposeFunction(current, SvgTransformKind.Rotate, FloatArgument(args, 0)));
                return true;
            case "rotateFromVector":
                value = MatrixMethod(matrix, property, 2, (current, args) =>
                {
                    float x = FloatArgument(args, 0);
                    float y = FloatArgument(args, 1);
                    if (x == 0f || y == 0f)
                    {
                        ThrowDomException("InvalidAccessError", "rotateFromVector needs non-zero coordinates.");
                    }
                    float degrees = (float)(Math.Atan2(y, x) * 180d / Math.PI);
                    return ComposeFunction(current, SvgTransformKind.Rotate, degrees);
                });
                return true;
            case "flipX":
                value = MatrixMethod(matrix, property, 0, (current, _) => SKMatrix.Concat(current, SKMatrix.CreateScale(-1f, 1f)));
                return true;
            case "flipY":
                value = MatrixMethod(matrix, property, 0, (current, _) => SKMatrix.Concat(current, SKMatrix.CreateScale(1f, -1f)));
                return true;
            case "skewX":
                value = MatrixMethod(matrix, property, 1, (current, args) =>
                    ComposeFunction(current, SvgTransformKind.SkewX, FloatArgument(args, 0)));
                return true;
            case "skewY":
                value = MatrixMethod(matrix, property, 1, (current, args) =>
                    ComposeFunction(current, SvgTransformKind.SkewY, FloatArgument(args, 0)));
                return true;
            default:
                value = JsValue.Undefined;
                return false;
        }
    }

    /// <summary>The SVGMatrix operations all return a new, independent matrix.</summary>
    private JsValue MatrixMethod(
        SvgMatrixHost matrix, string name, int length, Func<SKMatrix, IReadOnlyList<JsValue>, SKMatrix> operation) =>
        GetOrCreateHostCallable(matrix, name,
            (_, args) => ToHostOrNull(new SvgMatrixHost(operation(matrix.Value, args)), HostObjectKind.Other),
            length);

    private SKMatrix ComposeFunction(SKMatrix current, SvgTransformKind kind, float argument)
    {
        if (!SvgValues.TryCreateTransformMatrix(new SvgTransformFunction(kind, new[] { argument }), out SKMatrix t))
        {
            ThrowDomException("TypeError", "The transform is not finite.");
        }
        return SKMatrix.Concat(current, t);
    }

    private JsValue InsertTransform(SvgTransformListHost list, SvgTransformHost item, int index)
    {
        if (list.Items.Count >= MaxSvgTransformListItems)
        {
            ThrowDomException("QuotaExceededError", "The transform list is too long.");
        }

        item.Owner = list;
        list.Items.Insert(index, item);
        list.Commit();
        return ToHostOrNull(item, HostObjectKind.Other);
    }

    /// <summary>
    /// The SVGTransform argument to insert. An item that already belongs to a list
    /// (this one or another) is copied (SVG 2 §4.5.10, "If newItem is ... in a list").
    /// </summary>
    private SvgTransformHost AdoptTransformArgument(IReadOnlyList<JsValue> args, int index)
    {
        var item = args.Count > index ? ResolveHostObjectOrNull<SvgTransformHost>(args[index]) : null;
        if (item == null)
        {
            ThrowDomException("TypeError", "The argument is not an SVGTransform.");
        }
        return item.Owner != null ? item.Copy() : item;
    }

    private int RequireListIndex(SvgTransformListHost list, IReadOnlyList<JsValue> args, int argument, bool allowEnd)
    {
        double index = args.Count > argument ? CoerceToFiniteNumber(args[argument], -1) : -1;
        int limit = allowEnd ? list.Items.Count : list.Items.Count - 1;
        if (index < 0 || index > limit)
        {
            ThrowDomException("IndexSizeError", "The index is out of range.");
        }
        return (int)index;
    }

    private void RequireWritable(SvgTransformListHost list)
    {
        if (list.ReadOnly)
        {
            ThrowDomException("NoModificationAllowedError", "The list is read-only.");
        }
    }

    private void RequireWritable(SvgTransformHost transform)
    {
        if (transform.ReadOnly)
        {
            ThrowDomException("NoModificationAllowedError", "The transform is read-only.");
        }
    }

    private SvgTransformHost CreateTransformFromMatrixArgument(IReadOnlyList<JsValue> args)
    {
        var transform = new SvgTransformHost(IdentityMatrixFunction());
        transform.SetMatrix(ReadMatrixArgument(args, 0));
        return transform;
    }

    /// <summary>
    /// Reads a DOMMatrix2DInit: an SVGMatrix, or any object with a–f members
    /// (missing members take the identity's values).
    /// </summary>
    private SKMatrix ReadMatrixArgument(IReadOnlyList<JsValue> args, int index)
    {
        JsValue argument = args.Count > index ? args[index] : JsValue.Undefined;
        var host = ResolveHostObjectOrNull<SvgMatrixHost>(argument);
        if (host != null)
        {
            return host.Value;
        }
        if (argument.Tag != JsValueTag.Object)
        {
            ThrowDomException("TypeError", "The argument is not a matrix.");
        }

        float Member(string name, float fallback)
        {
            JsValue member = ReadJsProperty(argument, name);
            return member.Tag == JsValueTag.Undefined ? fallback : ToSvgFloat(member);
        }

        return new SKMatrix(
            Member("a", 1f), Member("c", 0f), Member("e", 0f),
            Member("b", 0f), Member("d", 1f), Member("f", 0f),
            0f, 0f, 1f);
    }

    private float FloatArgument(IReadOnlyList<JsValue> args, int index) =>
        ToSvgFloat(args.Count > index ? args[index] : JsValue.Undefined);

    /// <summary>WebIDL float: a non-finite value is a TypeError.</summary>
    private float ToSvgFloat(JsValue value)
    {
        double number = CoerceToFiniteNumber(value, double.NaN);
        float result = (float)number;
        if (!float.IsFinite(result))
        {
            ThrowDomException("TypeError", "The value is not a finite floating-point number.");
        }
        return result;
    }

    private static SvgTransformFunction IdentityMatrixFunction() =>
        new(SvgTransformKind.Matrix, new[] { 1f, 0f, 0f, 1f, 0f, 0f });

    private static bool TryParseArrayIndex(string property, out int index)
    {
        index = -1;
        return property.Length > 0 &&
               (property.Length == 1 || property[0] != '0') &&
               int.TryParse(property, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static bool Undefined(out JsValue value)
    {
        value = JsValue.Undefined;
        return false;
    }
}
