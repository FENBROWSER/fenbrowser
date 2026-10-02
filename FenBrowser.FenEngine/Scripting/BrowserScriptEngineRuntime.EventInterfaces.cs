using System;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The interface an event the user agent dispatches belongs to. DOM 2.2 makes every
/// dispatched event a platform object of its interface - a click is a PointerEvent, a
/// postMessage delivery a MessageEvent - with that interface's prototype. The engine built
/// its events as plain objects whose prototype was Object.prototype, so
/// <c>e instanceof Event</c> was false, and ShadyDOM's patchEvent, which caches a patched
/// prototype on the event's own prototype, wrote an enumerable
/// <c>__shady_patchedProto</c> onto Object.prototype. Every for-in on the page then saw
/// that key: YouTube's player iterated its format map, reached the patched prototype and
/// threw, and the video never buffered.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    /// <summary>
    /// Gives an engine-built event the prototype of its interface, chosen from its type.
    /// An event script constructed already has one and is left alone.
    /// </summary>
    private void BrandDispatchedEvent(JsValue eventValue, string type)
    {
        if (eventValue.Tag != JsValueTag.Object || _interpreter is null || _realmAbandoned)
        {
            return;
        }

        using (ScriptEngineLockProbe.Hold(_fenJsLock))
        {
            // Own data properties only: this runs for every callback the engine
            // delivers, fetch completions included, and must never run a getter the
            // page put on Object.prototype (WPT fetch/api/basic/stream-safe-creation).
            if (!TryReadOwnDataString(eventValue, "type", out var ownType))
            {
                return;
            }

            type ??= ownType;
            if (!string.Equals(ownType, type, StringComparison.Ordinal))
            {
                // Not an event of this dispatch (a fetch callback's result, say).
                return;
            }

            foreach (var interfaceName in EventInterfacesFor(type, eventValue))
            {
                if (_interpreter.TryBrandPlainObjectWithGlobalInterface(eventValue, interfaceName))
                {
                    return;
                }
            }
        }
    }

    /// <summary>The value of an own data property that holds a string; never calls a getter.</summary>
    private bool TryReadOwnDataString(JsValue target, string key, out string value)
    {
        value = null;
        if (target.Tag != JsValueTag.Object ||
            !_interpreter.Heap.GetObject(target.AsObjectHandle()).TryGetOwnProperty(key, out var descriptor) ||
            descriptor.IsAccessor ||
            descriptor.Value.Tag != JsValueTag.String)
        {
            return false;
        }

        value = descriptor.Value.AsString();
        return true;
    }

    /// <summary>
    /// The interface for an event type (UI Events, Pointer Events, HTML, CSS Transitions and
    /// Animations), most specific first; the caller falls back along the list when a
    /// constructor is missing, ending at Event.
    /// </summary>
    private string[] EventInterfacesFor(string type, JsValue eventValue)
    {
        switch (type)
        {
            case "click":
            case "auxclick":
            case "contextmenu":
            case "pointerdown":
            case "pointerup":
            case "pointermove":
            case "pointerover":
            case "pointerout":
            case "pointerenter":
            case "pointerleave":
            case "pointercancel":
            case "pointerrawupdate":
            case "gotpointercapture":
            case "lostpointercapture":
                return new[] { "PointerEvent", "MouseEvent", "UIEvent", "Event" };
            case "dblclick":
            case "mousedown":
            case "mouseup":
            case "mousemove":
            case "mouseover":
            case "mouseout":
            case "mouseenter":
            case "mouseleave":
                return new[] { "MouseEvent", "UIEvent", "Event" };
            case "wheel":
                return new[] { "WheelEvent", "MouseEvent", "UIEvent", "Event" };
            case "keydown":
            case "keyup":
            case "keypress":
                return new[] { "KeyboardEvent", "UIEvent", "Event" };
            case "focus":
            case "blur":
            case "focusin":
            case "focusout":
                return new[] { "FocusEvent", "UIEvent", "Event" };
            case "beforeinput":
            case "input":
                return new[] { "InputEvent", "UIEvent", "Event" };
            case "compositionstart":
            case "compositionupdate":
            case "compositionend":
                return new[] { "CompositionEvent", "UIEvent", "Event" };
            case "touchstart":
            case "touchmove":
            case "touchend":
            case "touchcancel":
                return new[] { "TouchEvent", "UIEvent", "Event" };
            case "drag":
            case "dragstart":
            case "dragend":
            case "dragenter":
            case "dragleave":
            case "dragover":
            case "drop":
                return new[] { "DragEvent", "MouseEvent", "UIEvent", "Event" };
            case "message":
            case "messageerror":
                return new[] { "MessageEvent", "Event" };
            case "hashchange":
                return new[] { "HashChangeEvent", "Event" };
            case "popstate":
                return new[] { "PopStateEvent", "Event" };
            case "pageshow":
            case "pagehide":
                return new[] { "PageTransitionEvent", "Event" };
            case "storage":
                return new[] { "StorageEvent", "Event" };
            case "submit":
                return new[] { "SubmitEvent", "Event" };
            case "unhandledrejection":
            case "rejectionhandled":
                return new[] { "PromiseRejectionEvent", "Event" };
            case "transitionrun":
            case "transitionstart":
            case "transitionend":
            case "transitioncancel":
                return new[] { "TransitionEvent", "Event" };
            case "animationstart":
            case "animationiteration":
            case "animationend":
            case "animationcancel":
                return new[] { "AnimationEvent", "Event" };
            case "error":
                // A script error reported at the window carries message/filename and is an
                // ErrorEvent (HTML 8.1.4.7); an element's failed load is a plain Event.
                return TryReadOwnDataString(eventValue, "message", out _)
                    ? new[] { "ErrorEvent", "Event" }
                    : new[] { "Event" };
            default:
                return new[] { "Event" };
        }
    }
}
