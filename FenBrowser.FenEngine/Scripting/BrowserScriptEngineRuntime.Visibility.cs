using System.Collections.Generic;
using System.Threading;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.Js.Host;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// What is showing the page, and what is showing each media element in it. The host
/// pushes the page's state in; the document carries it
/// (<c>document.visibilityState</c>, <c>document.hidden</c>) and fires
/// <c>visibilitychange</c>. On top of that, a media element whose box has scrolled out
/// of the viewport counts as not being shown either, which is what the media engine's
/// background policy (MEDIA_ENGINE_DESIGN section 5) acts on.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private volatile bool _pageVisible = true;
    private int _mediaViewportCheckQueued;

    /// <summary>Whether the page itself is on screen (Page Visibility section 3.1).</summary>
    internal bool IsPageVisible => _pageVisible;

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
        _pageVisible = visible;
        if (_onFenJsLargeStackThread)
        {
            OnVisibilityChanged(document);
            return;
        }

        QueueMediaTask(document, () => OnVisibilityChanged(document));
    }

    private void OnVisibilityChanged(Document document)
    {
        if (_realmAbandoned)
        {
            return;
        }

        foreach (var pair in _mediaElements)
        {
            pair.Value.ApplyEffectiveVisibility();
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

    /// <summary>
    /// The viewport scrolled or changed size, or a media element's box may have. Answering
    /// needs layout, so the check runs as a media element task; several requests before it
    /// runs collapse into one.
    /// </summary>
    public void ScheduleMediaViewportCheck()
    {
        if (_realmAbandoned || Volatile.Read(ref _mediaElementCount) == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _mediaViewportCheckQueued, 1) != 0)
        {
            return;
        }

        var document = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
        QueueMediaTask(document, RunMediaViewportCheck);
    }

    private void RunMediaViewportCheck()
    {
        Volatile.Write(ref _mediaViewportCheckQueued, 0);
        if (_realmAbandoned)
        {
            return;
        }

        foreach (var pair in _mediaElements)
        {
            pair.Value.SetOnScreen(ReadElementVisibility(pair.Key));
        }
    }

    /// <summary>What the layout says about a media element's box.</summary>
    internal enum ElementVisibility
    {
        /// <summary>The box overlaps the viewport, or there is no layout to say otherwise.</summary>
        OnScreen,

        /// <summary>The box exists and lies outside the viewport: the page scrolled past it.</summary>
        OutsideViewport,

        /// <summary>Layout gave the element no box at all: it is not rendered.</summary>
        NotRendered,
    }

    /// <summary>
    /// Whether the element's border box overlaps the viewport. A realm with no layout at
    /// all - a headless one, or one whose renderer has not run - answers true, so nothing
    /// stops decoding for want of geometry; a realm that does have layout and no box for
    /// the element answers false, because the element is not rendered.
    /// </summary>
    internal ElementVisibility ReadElementVisibility(Element element)
    {
        var resolver = LayoutBoxResolver;
        if (resolver == null || element == null)
        {
            return ElementVisibility.OnScreen;
        }

        if (resolver.Invoke(element) is not BoxModel box)
        {
            return ElementVisibility.NotRendered;
        }

        var rect = box.BorderBox;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return ElementVisibility.NotRendered;
        }

        double viewportWidth = WindowWidth;
        double viewportHeight = WindowHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return ElementVisibility.OnScreen;
        }

        var scroll = ReadAncestorScrollOffset(element);
        double left = rect.Left - scroll.X;
        double top = rect.Top - scroll.Y;
        bool overlaps = left < viewportWidth && top < viewportHeight && left + rect.Width > 0 && top + rect.Height > 0;
        return overlaps ? ElementVisibility.OnScreen : ElementVisibility.OutsideViewport;
    }
}
