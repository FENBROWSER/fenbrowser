using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Js.Host;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The Page Visibility API. The host pushes the state in from the tab and window state;
/// the document carries it (<c>document.visibilityState</c>, <c>document.hidden</c>),
/// <c>visibilitychange</c> fires at the document, and the media engine's background
/// policy (MEDIA_ENGINE_DESIGN section 5) follows it.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    /// <summary>
    /// Page Visibility section 3.4: the visibility of the document changed. The state
    /// reaches the document at once, then a task runs the steps that depend on it - here
    /// the media background policy - and fires <c>visibilitychange</c>.
    /// </summary>
    public void SetPageVisible(bool visible)
    {
        var document = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
        var state = visible ? DocumentVisibilityState.Visible : DocumentVisibilityState.Hidden;
        if (document == null || _realmAbandoned || document.VisibilityState == state)
        {
            return;
        }

        document.VisibilityState = state;
        if (_onFenJsLargeStackThread)
        {
            OnVisibilityChanged(document, visible);
            return;
        }

        QueueMediaTask(document, () => OnVisibilityChanged(document, visible));
    }

    private void OnVisibilityChanged(Document document, bool visible)
    {
        if (_realmAbandoned)
        {
            return;
        }

        // Every media element in this realm learns whether anything is showing its pictures.
        foreach (var pair in _mediaElements)
        {
            pair.Value.Controller.IsVideoVisible = visible;
        }

        var docHost = ToHostOrNull(document, HostObjectKind.DomDocument);
        var eventValue = _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["type"] = JsValue.FromString("visibilitychange"),
            ["target"] = docHost,
            ["currentTarget"] = docHost,
            ["bubbles"] = JsValue.FromBoolean(true),
            ["cancelable"] = JsValue.FromBoolean(false),
            ["isTrusted"] = JsValue.FromBoolean(true)
        });
        DispatchBrowserEvent(_documentEventListeners, "visibilitychange", docHost, eventValue);

        var handler = GetStoredHostPropertyOrUndefined(document, "onvisibilitychange");
        if (handler.Tag != JsValueTag.Undefined && _interpreter.CanCallValue(handler))
        {
            TryInvokeFenJsEventCallback(handler, docHost, eventValue, "visibilitychange");
        }
    }
}
