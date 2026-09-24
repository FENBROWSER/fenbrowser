using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Same-origin access from a page to the globals of a frame it embeds - the other half of
/// <see cref="CreateSameOriginParentWindowProxy"/>.
/// </summary>
/// <remarks>
/// HTML 7.2.3: <c>iframe.contentWindow</c> is the frame's WindowProxy, so a name the
/// frame's script defined reads through it, and a property the page sets on it is set on
/// the frame's global. A frame runs in its own realm on its own heap, and the page's
/// contentWindow is a stand-in with a fixed set of properties - so
/// <c>frame.contentWindow.flag = 1</c> never reached the frame, and testdriver, which
/// labels a window that way, could not find the frame it had labelled. The stand-in is a
/// <see cref="ForwardingObject"/>: its own properties read as before, and any other name
/// is read from, or written to, the frame realm's global. Functions come back as wrappers
/// that call into the frame; everything else crosses as a copy, like parent access.
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    private sealed class ChildWindowSource : IForwardedPropertySource
    {
        private readonly FenJsBrowserScriptEngine _owner;
        private readonly Element _frame;
        private readonly Dictionary<string, JsValue> _wrappers = new(System.StringComparer.Ordinal);

        public ChildWindowSource(FenJsBrowserScriptEngine owner, Element frame)
        {
            _owner = owner;
            _frame = frame;
        }

        private FenJsBrowserScriptEngine Child =>
            _owner.CanCurrentContextAccessIFrameDocument(_frame) && _owner._iframeRealms.TryGetValue(_frame, out var realm)
                ? realm
                : null;

        public bool TryRead(string key, out JsValue value)
        {
            value = JsValue.Undefined;
            if (string.IsNullOrEmpty(key) || key.StartsWith("__fen", System.StringComparison.Ordinal) || Child is not { } child)
            {
                return false;
            }

            object exported = null;
            var isFunction = WithRealmLocked(
                child,
                realm =>
                {
                    var global = realm.ReadGlobalValueOrUndefined(key);
                    if (realm._interpreter.CanCallValue(global))
                    {
                        return true;
                    }

                    exported = realm.ExportCrossRealmValue(global);
                    return false;
                },
                false);

            if (isFunction)
            {
                if (!_wrappers.TryGetValue(key, out value))
                {
                    var name = key;
                    value = _owner._interpreter.AllocateNativeFunction(
                        name,
                        (_, args) => _owner.CallGlobalFunctionAcrossRealms(
                            "contentWindow",
                            name,
                            _owner._interpreter.AllocateArray(System.Linq.Enumerable.ToArray(args)),
                            work => WithRealmLocked(Child, work, false)),
                        length: 0);
                    _wrappers[key] = value;
                }

                return true;
            }

            if (exported is JsValue { Tag: JsValueTag.Undefined } or null or CrossRealmFunction)
            {
                return false;
            }

            value = _owner.ImportCrossRealmValue(exported);
            return true;
        }

        public bool TryWrite(string key, JsValue value)
        {
            if (string.IsNullOrEmpty(key) || key.StartsWith("__fen", System.StringComparison.Ordinal) || Child is not { } child)
            {
                return false;
            }

            var exported = _owner.ExportCrossRealmValue(value);
            if (exported is CrossRealmFunction)
            {
                return false;
            }

            return WithRealmLocked(
                child,
                realm =>
                {
                    realm._interpreter.SetObjectProperty(realm._fenJsGlobalThis, key, realm.ImportCrossRealmValue(exported));
                    return true;
                },
                false);
        }
    }

    /// <summary>
    /// From now on, names the contentWindow stand-in of <paramref name="frame"/> does not
    /// own are read from and written to the frame realm's global.
    /// </summary>
    private void ForwardContentWindowToFrameRealm(JsValue window, Element frame)
    {
        if (window.Tag == JsValueTag.Object &&
            _interpreter.Heap.GetObject(window.AsObjectHandle()) is ForwardingObject forwarding)
        {
            forwarding.Source = new ChildWindowSource(this, frame);
        }
    }
}
