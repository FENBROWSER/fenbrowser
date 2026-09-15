using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Same-origin access from a frame to the properties its parent page defined.
/// </summary>
/// <remarks>
/// HTML 7.2.3 WindowProxy: a same-origin frame's <c>parent</c> is the parent's
/// WindowProxy, so <c>parent.notify("x")</c> calls a function the parent page
/// declared. A frame here runs in its own realm on its own heap, and its
/// <c>parent</c> is a stand-in carrying a fixed set of properties, so every
/// function the parent page defined read as undefined. Acid3's XHTML support
/// files report back exactly that way, and test 80 failed with "Script in XHTML
/// didn't execute".
///
/// The stand-in is wrapped in a Proxy. Its own properties read as before. Any
/// other name is first matched against the parent document's child frames,
/// then looked up on the parent realm's global: a function comes back as a
/// wrapper that calls into the parent, and a primitive or DOM node is copied.
/// Arguments and return values cross the same way, with plain objects copied
/// structurally, because a value on one heap can never be handed to another.
/// Each realm has its own lock, so reaching the parent's global waits a bounded
/// time for it and gives up instead of deadlocking against a parent that is
/// itself waiting on this frame.
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    private const int ParentWindowLockTimeoutMs = 2000;

    // The plain object behind a same-origin frame's parent Proxy. The frame
    // table is written here: SetObjectProperty on the Proxy would bypass its
    // traps and define the property on the Proxy object itself.
    private JsValue _embeddedParentWindowTarget = JsValue.Undefined;

    private const string ParentWindowProxyFactorySource = """
        (function (target, kindOf, readValue, callParent) {
            var wrappers = new Map();
            return new Proxy(target, {
                get: function (t, key, receiver) {
                    if (typeof key !== 'string' || key in t) {
                        return Reflect.get(t, key, receiver);
                    }
                    var kind = kindOf(key);
                    if (kind === 'function') {
                        var wrapper = wrappers.get(key);
                        if (wrapper === undefined) {
                            wrapper = function (...args) { return callParent(key, args); };
                            wrappers.set(key, wrapper);
                        }
                        return wrapper;
                    }
                    return kind === 'value' ? readValue(key) : undefined;
                },
                has: function (t, key) {
                    return (key in t) || (typeof key === 'string' && kindOf(key) !== 'missing');
                }
            });
        })
        """;

    // A structured copy of an object that is neither a function nor a DOM node.
    private sealed record CrossRealmGraph(object Value);

    // A function passed as an argument: it lives on this heap and cannot be called from another.
    private sealed record CrossRealmFunction;

    /// <summary>
    /// Wraps the stand-in parent window of a same-origin frame so that names the
    /// parent page defined resolve through the parent realm. Returns the stand-in
    /// unchanged when the Proxy cannot be built.
    /// </summary>
    private JsValue CreateSameOriginParentWindowProxy(JsValue target)
    {
        try
        {
            using (ScriptEngineLockProbe.Hold(_fenJsLock))
            {
                var compiled = _compiler.CompileScript(
                    new SourceText(ParentWindowProxyFactorySource, "<fenbrowser-parent-window-proxy>"));
                new BytecodeVerifier().Verify(compiled);
                // Each value is pinned before the next allocation can collect it.
                var factory = _interpreter.Execute(compiled);
                using var factoryPin = PinFenJsValues(factory, target);
                var kindOf = _interpreter.AllocateNativeFunction(
                    "kindOf",
                    (_, args) => JsValue.FromString(ReadParentGlobalKind(ReadPropertyKey(args))),
                    length: 1);
                using var kindOfPin = PinFenJsValues(kindOf);
                var readValue = _interpreter.AllocateNativeFunction(
                    "readValue",
                    (_, args) => ReadParentGlobalValue(ReadPropertyKey(args)),
                    length: 1);
                using var readValuePin = PinFenJsValues(readValue);
                var callParent = _interpreter.AllocateNativeFunction(
                    "callParent",
                    (_, args) => CallParentGlobalFunction(ReadPropertyKey(args), args.Count > 1 ? args[1] : JsValue.Undefined),
                    length: 2);
                using var callParentPin = PinFenJsValues(callParent);
                return _interpreter.InvokeFunction(
                    factory,
                    new[] { target, kindOf, readValue, callParent },
                    JsValue.Undefined);
            }
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] Parent window proxy unavailable, frame keeps the plain stand-in: {ex.Message}",
                LogCategory.JavaScript);
            return target;
        }
    }

    private static string ReadPropertyKey(IReadOnlyList<JsValue> args) =>
        args.Count > 0 && args[0].Tag == JsValueTag.String ? args[0].AsString() : string.Empty;

    /// <summary>
    /// HTML 7.2.3 WindowProxy named properties: the parent document's child
    /// frames are reachable on the parent window by name. The frame table
    /// publishes them from a queued refresh, so a sibling that has only just
    /// appeared would read as undefined until that refresh ran. Reading the
    /// parent document needs no parent lock.
    /// </summary>
    private Element FindParentChildFrameByName(string name)
    {
        var parentDocument = _embeddingFrameElement?.OwnerDocument;
        if (string.IsNullOrEmpty(name) || parentDocument == null)
        {
            return null;
        }

        foreach (var frame in EnumerateChildBrowsingContexts(parentDocument))
        {
            if (string.Equals(frame.GetAttribute("name"), name, StringComparison.Ordinal))
            {
                return frame;
            }
        }

        return null;
    }

    // A global is readable through the frame's parent when it is a function
    // (called through a wrapper) or a value that means the same on both heaps.
    // Objects stay unreadable: a structural copy of, say, the parent's window
    // for a named frame would be expensive and would not be that object.
    private string ReadParentGlobalKind(string name)
    {
        if (FindParentChildFrameByName(name) != null)
        {
            return "value";
        }

        return WithParentRealm(
            parent =>
            {
                var value = parent.ReadGlobalValueOrUndefined(name);
                if (parent._interpreter.CanCallValue(value))
                {
                    return "function";
                }

                return parent.ExportCrossRealmValue(value) is JsValue { Tag: not JsValueTag.Undefined } or Node
                    ? "value"
                    : "missing";
            },
            "missing");
    }

    private JsValue ReadParentGlobalValue(string name)
    {
        var frame = FindParentChildFrameByName(name);
        if (frame != null)
        {
            return ReferenceEquals(frame, _embeddingFrameElement)
                ? _fenJsGlobalThis
                : GetOrCreateSiblingWindowProxy(frame, _embeddedParentWindowProxy);
        }

        var exported = WithParentRealm(
            parent =>
            {
                var value = parent.ExportCrossRealmValue(parent.ReadGlobalValueOrUndefined(name));
                return value is JsValue or Node ? value : JsValue.Undefined;
            },
            (object)JsValue.Undefined);
        return ImportCrossRealmValue(exported);
    }

    private JsValue CallParentGlobalFunction(string name, JsValue argumentList)
    {
        var count = 0;
        if (argumentList.Tag == JsValueTag.Object)
        {
            var length = ReadJsProperty(argumentList, "length");
            count = length.Tag == JsValueTag.Int32
                ? length.AsInt32()
                : length.Tag == JsValueTag.Number ? (int)length.AsNumber() : 0;
        }

        var exportedArguments = new object[Math.Max(0, count)];
        for (var index = 0; index < exportedArguments.Length; index++)
        {
            exportedArguments[index] = ExportCrossRealmValue(
                ReadJsProperty(argumentList, index.ToString(CultureInfo.InvariantCulture)));
            if (exportedArguments[index] is CrossRealmFunction)
            {
                ThrowDomException(
                    "TypeError",
                    $"parent.{name}: a function cannot be passed to a window in another realm");
            }
        }

        object result = JsValue.Undefined;
        string errorName = null;
        string errorMessage = null;
        var entered = WithParentRealm(
            parent =>
            {
                var function = parent.ReadGlobalValueOrUndefined(name);
                if (!parent._interpreter.CanCallValue(function))
                {
                    errorName = "TypeError";
                    errorMessage = $"parent.{name} is not a function";
                    return true;
                }

                var pins = new List<IDisposable> { parent.PinFenJsValues(function) };
                try
                {
                    var importedArguments = new JsValue[exportedArguments.Length];
                    for (var index = 0; index < importedArguments.Length; index++)
                    {
                        importedArguments[index] = parent.ImportCrossRealmValue(exportedArguments[index]);
                        pins.Add(parent.PinFenJsValues(importedArguments[index]));
                    }

                    var value = parent._interpreter.InvokeFunction(function, importedArguments, parent._fenJsGlobalThis);
                    pins.Add(parent.PinFenJsValues(value));
                    result = parent.ExportCrossRealmValue(value);
                }
                catch (JsThrownException ex)
                {
                    (errorName, errorMessage) = parent.DescribeThrownValue(ex.Value);
                }
                finally
                {
                    foreach (var pin in pins)
                    {
                        pin?.Dispose();
                    }
                }

                return true;
            },
            false);

        if (!entered)
        {
            ThrowDomException("Error", $"parent.{name}: the parent window did not become available");
        }

        if (errorMessage != null)
        {
            // Only the ECMAScript error types this realm can construct keep their name.
            if (errorName is "TypeError" or "RangeError" or "Error")
            {
                ThrowDomException(errorName, errorMessage);
            }

            ThrowDomException("Error", string.IsNullOrEmpty(errorName) ? errorMessage : errorName + ": " + errorMessage);
        }

        if (result is CrossRealmFunction)
        {
            // A function returned by the parent cannot be called from this heap.
            return JsValue.Undefined;
        }

        return ImportCrossRealmValue(result);
    }

    /// <summary>
    /// Runs <paramref name="work"/> holding the parent realm's lock, waiting at
    /// most <see cref="ParentWindowLockTimeoutMs"/> for it.
    /// </summary>
    private T WithParentRealm<T>(Func<FenJsBrowserScriptEngine, T> work, T unavailable)
    {
        var parent = _parentRealmOwner;
        if (parent == null || parent._realmAbandoned || _embeddingFrameElement == null)
        {
            return unavailable;
        }

        if (!Monitor.TryEnter(parent._fenJsLock, ParentWindowLockTimeoutMs))
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] Parent window lock not available within {ParentWindowLockTimeoutMs}ms",
                LogCategory.JavaScript);
            return unavailable;
        }

        try
        {
            if (parent._interpreter == null || parent._realmAbandoned)
            {
                return unavailable;
            }

            return work(parent);
        }
        finally
        {
            Monitor.Exit(parent._fenJsLock);
        }
    }

    /// <summary>
    /// Captures a value of this realm in a form another realm can rebuild.
    /// Caller holds this realm's lock.
    /// </summary>
    private object ExportCrossRealmValue(JsValue value)
    {
        switch (value.Tag)
        {
            case JsValueTag.Undefined:
            case JsValueTag.Null:
            case JsValueTag.Boolean:
            case JsValueTag.Int32:
            case JsValueTag.Number:
                return value;
            case JsValueTag.String:
                return JsValue.FromString(value.AsString());
            case JsValueTag.HostObject:
                // A DOM node is shared by every realm's host table; any other
                // host object has no counterpart on the other heap.
                return ResolveHostObjectOrNull(value) is Node node ? node : JsValue.Undefined;
            case JsValueTag.Object:
                break;
            default:
                return JsValue.Undefined;
        }

        if (_interpreter.CanCallValue(value))
        {
            return new CrossRealmFunction();
        }

        return new CrossRealmGraph(ConvertJsValueToObject(value));
    }

    private JsValue ImportCrossRealmValue(object exported) => exported switch
    {
        JsValue primitive => primitive,
        Node node => ToHostNodeOrNull(node),
        CrossRealmGraph graph => ConvertObjectToJsValue(graph.Value),
        _ => JsValue.Undefined
    };

    private (string Name, string Message) DescribeThrownValue(JsValue thrown)
    {
        if (thrown.Tag == JsValueTag.Object)
        {
            var name = ReadJsProperty(thrown, "name");
            var message = ReadJsProperty(thrown, "message");
            return (
                name.Tag == JsValueTag.String ? name.AsString() : "Error",
                message.Tag == JsValueTag.String ? message.AsString() : string.Empty);
        }

        return ("Error", thrown.Tag == JsValueTag.String ? thrown.AsString() : thrown.Tag.ToString());
    }
}
