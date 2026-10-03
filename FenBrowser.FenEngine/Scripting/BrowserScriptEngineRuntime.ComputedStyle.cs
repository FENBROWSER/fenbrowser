using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// CSSOM 6.7 getComputedStyle: a read-only declaration whose properties resolve
/// when they are read.
/// </summary>
/// <remarks>
/// The declaration used to be built in full on every call: a dictionary of every
/// longhand in both spellings, materialised as a script object of about 800 own
/// properties, after forcing style and layout for the whole document - when the
/// caller usually wanted one value (Polymer and YouTube read <c>direction</c> or
/// <c>display</c> thousands of times a load). Now each call allocates an empty
/// object over a per-realm prototype of accessors; the first read flushes style
/// (and layout only for the used-value properties) and builds the values once.
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    private sealed class ComputedStyleState
    {
        public ComputedStyleState(Element element) => Element = element;

        public Element Element { get; }

        public Dictionary<string, JsValue> Props;

        public Action ResolveLayoutDependent;
    }

    // Keyed by the declaration object itself, so the state dies with it.
    private readonly ConditionalWeakTable<JsObject, ComputedStyleState> _computedStyleStates = new();

    private JsValue _computedStylePrototype = JsValue.Undefined;

    private JsValue CreateComputedStyleObjectForElement(Element element)
    {
        var declaration = _interpreter.AllocateObject(EmptyComputedStyleProps);
        var declarationObject = _interpreter.Heap.GetObject(declaration.AsObjectHandle());
        declarationObject.SetPrototype(EnsureComputedStylePrototype().AsObjectHandle());
        _computedStyleStates.Add(declarationObject, new ComputedStyleState(element));
        return declaration;
    }

    private static readonly Dictionary<string, JsValue> EmptyComputedStyleProps = new();

    private bool TryGetComputedStyleState(JsValue declaration, out ComputedStyleState state)
    {
        state = null;
        return declaration.Tag == JsValueTag.Object &&
               _computedStyleStates.TryGetValue(_interpreter.Heap.GetObject(declaration.AsObjectHandle()), out state);
    }

    private JsValue ReadComputedStyleValue(ComputedStyleState state, string property)
    {
        if (state.Element == null)
        {
            return JsValue.FromString(string.Empty);
        }

        if (state.Props == null)
        {
            // Reading a computed value needs the element's style current, not its
            // layout (CSSOM 6.7.2 resolved values).
            (FlushPendingStyle ?? FlushPendingLayout)?.Invoke(state.Element);
            state.Props = BuildComputedStyleProps(state.Element, out state.ResolveLayoutDependent);
        }

        if (Array.IndexOf(LayoutDependentComputedProperties, property) >= 0 && state.ResolveLayoutDependent != null)
        {
            FlushPendingLayout?.Invoke(state.Element);
            state.ResolveLayoutDependent();
            state.ResolveLayoutDependent = null;
        }

        if (state.Props.TryGetValue(property, out var direct))
        {
            return direct;
        }

        var kebabName = CamelToCssProp(property);
        if (!string.Equals(kebabName, property, StringComparison.Ordinal) &&
            state.Props.TryGetValue(kebabName, out var kebabValue))
        {
            return kebabValue;
        }

        return JsValue.FromString(string.Empty);
    }

    private JsValue EnsureComputedStylePrototype()
    {
        if (_computedStylePrototype.Tag == JsValueTag.Object)
        {
            return _computedStylePrototype;
        }

        var prototype = _interpreter.AllocateObject(EmptyComputedStyleProps);
        // CSSOM 6.7.3: every supported property is an attribute, in both spellings.
        var names = new HashSet<string>(StringComparer.Ordinal) { "cssFloat" };
        var dashedNames = new List<string>(ComputedStyleInitialValues.Keys);
        dashedNames.AddRange(FenBrowser.Core.Css.CssPropertyNames.All);
        dashedNames.AddRange(SvgGeometryComputedProperties);
        foreach (var dashed in dashedNames)
        {
            names.Add(dashed);
            if (dashed.IndexOf('-') > 0)
            {
                names.Add(CssPropToCamel(dashed));
            }
        }

        foreach (var name in LayoutDependentComputedProperties)
        {
            names.Add(name);
        }

        foreach (var name in names)
        {
            var key = name == "cssFloat" ? "float" : name;
            _interpreter.DefineObjectAccessor(
                prototype,
                name,
                _interpreter.AllocateNativeFunction(
                    "get " + name,
                    (thisValue, _) => TryGetComputedStyleState(thisValue, out var state)
                        ? ReadComputedStyleValue(state, key)
                        : JsValue.Undefined),
                JsValue.Undefined);
        }

        _interpreter.SetObjectProperty(
            prototype,
            "getPropertyValue",
            _interpreter.AllocateNativeFunction(
                "getPropertyValue",
                (thisValue, args) =>
                {
                    var propertyName = args.Count > 0 ? CoerceToHostString(args[0]).Trim() : string.Empty;
                    if (propertyName.Length == 0 || !TryGetComputedStyleState(thisValue, out var state))
                    {
                        return JsValue.FromString(string.Empty);
                    }

                    return ReadComputedStyleValue(state, propertyName);
                },
                length: 1),
            enumerable: true);

        _computedStylePrototype = prototype;
        return prototype;
    }
}
