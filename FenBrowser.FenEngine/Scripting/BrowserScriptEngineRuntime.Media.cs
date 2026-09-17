using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.FenEngine.Media;
using FenBrowser.Js.Builtins;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;
using FenBrowser.Media;
using FenBrowser.Media.Element;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The <c>video</c> and <c>audio</c> elements (HTML §4.8.9, §4.8.10, §4.8.11).
/// </summary>
/// <remarks>
/// The element state machine lives in <see cref="HtmlMediaElementController"/>, which knows
/// nothing about the DOM or the script engine; this file is its host. One controller is
/// created per media element the first time script touches it or the DOM gives it a source,
/// and it is owned by the realm whose document holds the element. The controller is
/// single-threaded, so every DOM notification that arrives on another thread (the parser
/// inserting a <c>source</c>) is queued as a media element task and handled on the JS worker.
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    // Weak per element: a document's media elements must not outlive it through here.
    private readonly ConditionalWeakTable<Element, MediaElementBinding> _mediaElements = new();
    private bool _mediaObserverSubscribed;

    // HTML "sticky activation": set once the document has seen a trusted click and
    // never cleared. The autoplay policy is the only reader.
    private bool _stickyUserActivation;

    private FenJsBrowserScriptEngine MediaObserverOwner => _parentRealmOwner?.MediaObserverOwner ?? this;

    internal static bool IsMediaElement(Element element) =>
        IsVideoElement(element) || IsAudioElement(element);

    private static bool IsVideoElement(Element element) =>
        string.Equals(element?.TagName, "video", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudioElement(Element element) =>
        string.Equals(element?.TagName, "audio", StringComparison.OrdinalIgnoreCase);

    private static bool IsSourceElement(Node node) =>
        node is Element element && string.Equals(element.TagName, "source", StringComparison.OrdinalIgnoreCase);

    private bool HasUserActivation => _stickyUserActivation || _transientUserActivationDepth > 0;

    // ---- Observing the DOM ------------------------------------------------------------------

    private void EnsureMediaElementObserver()
    {
        var owner = MediaObserverOwner;
        if (!ReferenceEquals(owner, this))
        {
            owner.EnsureMediaElementObserver();
            return;
        }

        if (_mediaObserverSubscribed || _realmAbandoned)
        {
            return;
        }

        _mediaObserverSubscribed = true;
        Node.OnMutation += OnDomMutationForMedia;
    }

    private void UnsubscribeMediaElementObserver()
    {
        if (!_mediaObserverSubscribed)
        {
            return;
        }

        _mediaObserverSubscribed = false;
        Node.OnMutation -= OnDomMutationForMedia;
    }

    // Node.OnMutation is process-wide and fires on the mutating thread: only act on
    // documents this realm hosts, and only ever touch a controller on the JS worker.
    private void OnDomMutationForMedia(
        Node target,
        string type,
        string attributeName,
        string attributeNamespace,
        List<Node> addedNodes,
        List<Node> removedNodes)
    {
        if (_realmAbandoned)
        {
            return;
        }

        try
        {
            if (string.Equals(type, "attributes", StringComparison.Ordinal))
            {
                if (target is Element element &&
                    IsMediaElement(element) &&
                    string.Equals(attributeName, "src", StringComparison.OrdinalIgnoreCase) &&
                    OwnsDocumentForImageEvents(element.OwnerDocument))
                {
                    // HTML §4.8.11.2: "If a src attribute of a media element is set or
                    // changed, the user agent must invoke the media element's load
                    // algorithm."  Removing the attribute is not a change of that kind.
                    if (element.HasAttribute("src"))
                    {
                        RunOnMediaThread(element, binding => binding.Controller.OnSrcAttributeSet());
                    }
                }

                return;
            }

            if (!string.Equals(type, "childList", StringComparison.Ordinal))
            {
                return;
            }

            if (addedNodes != null)
            {
                for (var i = 0; i < addedNodes.Count; i++)
                {
                    var added = addedNodes[i];
                    if (added == null || !OwnsDocumentForImageEvents(added.OwnerDocument))
                    {
                        continue;
                    }

                    if (target is Element parent && IsMediaElement(parent))
                    {
                        // §4.8.11.2: a source inserted into a media element wakes a
                        // resource selection waiting for one, or starts it.
                        RunOnMediaThread(parent, binding => binding.Controller.OnChildInserted(added));
                    }

                    TrackMediaElementsInSubtree(added);
                }
            }

            if (removedNodes != null)
            {
                for (var i = 0; i < removedNodes.Count; i++)
                {
                    var removed = removedNodes[i];
                    if (removed == null)
                    {
                        continue;
                    }

                    if (target is Element parent && IsMediaElement(parent) && TryGetMediaBinding(parent, out var parentBinding))
                    {
                        // The static mutation event does not carry the previous sibling,
                        // so a pointer that sat on the removed node restarts at the
                        // beginning of the child list; a later candidate is retried,
                        // never skipped.
                        RunOnMediaThread(parent, binding => binding.Controller.OnChildRemoved(removed, previousSibling: null));
                    }

                    ForEachBoundMediaElement(removed, element =>
                    {
                        // §4.8.11.8: "When a media element is removed from a Document, the
                        // user agent must run the following steps: await a stable state
                        // ... if the media element is in a document, return; run the
                        // internal pause steps."
                        RunOnMediaThread(element, binding => binding.Controller.OnRemovedFromDocument(() => element.IsConnected));
                    });
                }
            }
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn(
                $"[FenJsBridge] Media mutation walk failed: {ex.GetType().Name}: {ex.Message}",
                LogCategory.JavaScript);
        }
    }

    /// <summary>
    /// Gives every media element in an inserted subtree a controller, so an element that
    /// arrives with a <c>src</c> attribute or <c>source</c> children starts selecting a
    /// resource (§4.8.11.2 "when a media element is created").
    /// </summary>
    private void TrackMediaElementsInSubtree(Node root)
    {
        if (root is Element rootElement && IsMediaElement(rootElement))
        {
            RunOnMediaThread(rootElement, static _ => { });
        }

        if (root is not ContainerNode)
        {
            return;
        }

        foreach (var descendant in root.Descendants())
        {
            if (descendant is Element element && IsMediaElement(element))
            {
                RunOnMediaThread(element, static _ => { });
            }
        }
    }

    private void ForEachBoundMediaElement(Node root, Action<Element> action)
    {
        if (root is Element rootElement && IsMediaElement(rootElement) && TryGetMediaBinding(rootElement, out _))
        {
            action(rootElement);
        }

        if (root is not ContainerNode)
        {
            return;
        }

        foreach (var descendant in root.Descendants())
        {
            if (descendant is Element element && IsMediaElement(element) && TryGetMediaBinding(element, out _))
            {
                action(element);
            }
        }
    }

    // ---- Bindings ---------------------------------------------------------------------------

    /// <summary>The realm that owns an element's controller: the frame realm for a framed document.</summary>
    private FenJsBrowserScriptEngine MediaRealmFor(Element element) =>
        TryGetFrameRealm(element.OwnerDocument, out var frameRealm) && !ReferenceEquals(frameRealm, this)
            ? frameRealm.MediaRealmFor(element)
            : this;

    private bool TryGetMediaBinding(Element element, out MediaElementBinding binding)
    {
        var realm = MediaRealmFor(element);
        if (!ReferenceEquals(realm, this))
        {
            return realm.TryGetMediaBinding(element, out binding);
        }

        return _mediaElements.TryGetValue(element, out binding);
    }

    /// <summary>Must be called on the JS worker of the owning realm.</summary>
    private MediaElementBinding GetOrCreateMediaBinding(Element element)
    {
        var realm = MediaRealmFor(element);
        if (!ReferenceEquals(realm, this))
        {
            return realm.GetOrCreateMediaBinding(element);
        }

        if (_mediaElements.TryGetValue(element, out var binding))
        {
            return binding;
        }

        binding = new MediaElementBinding(this, element);
        _mediaElements.Add(element, binding);
        EnsureMediaElementObserver();
        binding.Start();
        return binding;
    }

    /// <summary>
    /// Runs <paramref name="action"/> against the element's controller on the owning realm's
    /// JS worker: inline when already there (a script setting <c>src</c> observes
    /// <c>networkState</c> change synchronously, as the spec requires), otherwise as a queued
    /// media element task.
    /// </summary>
    private void RunOnMediaThread(Element element, Action<MediaElementBinding> action)
    {
        var realm = MediaRealmFor(element);
        if (_onFenJsLargeStackThread)
        {
            action(realm.GetOrCreateMediaBinding(element));
            return;
        }

        realm.QueueMediaTask(element.OwnerDocument, () => action(realm.GetOrCreateMediaBinding(element)));
    }

    /// <summary>
    /// Queues a task on the media element event task source. Like every other task it runs
    /// on the JS worker under the realm lock with the element's window active, and ends with
    /// a microtask checkpoint.
    /// </summary>
    private void QueueMediaTask(Document document, Action task)
    {
        if (_realmAbandoned)
        {
            return;
        }

        EnsureFenJsWorkerRunning();
        var workItem = CreateFenJsWorkItem(
            () =>
            {
                if (_realmAbandoned)
                {
                    return null;
                }

                using (ScriptEngineLockProbe.Hold(_fenJsLock))
                {
                    var currentDocument = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
                    var windowContext = _parentRealmOwner != null && ReferenceEquals(document, currentDocument)
                        ? null
                        : ActivateSubdocumentWindowContext(document, null);
                    using (windowContext)
                    {
                        try
                        {
                            task();
                        }
                        finally
                        {
                            _interpreter.PumpMicrotasks();
                            RecordMicrotaskCheckpoint("media-task");
                        }
                    }
                }

                return null;
            },
            FenJsBrowserInstructionBudget,
            "MediaTask");
        _fenJsWorkQueue.Enqueue(workItem);
        _fenJsWorkAvailable.Set();
        ObserveAbandonedCompletion(workItem.Completion.Task);
    }

    private void TraceMediaJsRoots(IHeapTracer tracer)
    {
        foreach (var entry in _mediaElements)
        {
            entry.Value.TraceRoots(tracer);
        }
    }

    // ---- The host the controller sees ---------------------------------------------------------

    /// <summary>
    /// One media element as <see cref="HtmlMediaElementController"/> sees it: attributes and
    /// children come from the DOM, tasks and events go through the realm, and promises are
    /// the realm's own promise objects kept alive here until they settle.
    /// </summary>
    private sealed class MediaElementBinding : IMediaElementHost
    {
        private readonly FenJsBrowserScriptEngine _realm;
        private readonly Element _element;
        private readonly List<MediaPromise> _pendingPromises = new();
        private JsValue _errorObject = JsValue.Undefined;
        private MediaElementError _errorObjectFor;

        public MediaElementBinding(FenJsBrowserScriptEngine realm, Element element)
        {
            _realm = realm;
            _element = element;
            Controller = new HtmlMediaElementController(this, MediaEngineServices.TypeSupport, MediaEngineServices.Log);
        }

        public HtmlMediaElementController Controller { get; }

        public Element Element => _element;

        /// <summary>
        /// §4.8.11.2: an element that already has a <c>src</c> attribute, or <c>source</c>
        /// children, when its controller is created starts resource selection right away.
        /// </summary>
        public void Start()
        {
            if (_element.HasAttribute("src"))
            {
                Controller.OnSrcAttributeSet();
                return;
            }

            for (var child = _element.FirstChild; child != null; child = child.NextSibling)
            {
                if (IsSourceElement(child))
                {
                    Controller.OnChildInserted(child);
                    return;
                }
            }
        }

        // -- attributes and tree --

        public bool IsVideo => IsVideoElement(_element);

        public string SrcAttribute => _element.GetAttribute("src");

        public bool HasAutoplayAttribute => _element.HasAttribute("autoplay");

        public bool HasLoopAttribute => _element.HasAttribute("loop");

        public bool HasMutedAttribute => _element.HasAttribute("muted");

        public string CrossOriginAttribute => _element.GetAttribute("crossorigin");

        public string PreloadAttribute => _element.GetAttribute("preload");

        public object FirstChild => _element.FirstChild;

        public object NextSibling(object child) => (child as Node)?.NextSibling;

        public bool IsSourceElement(object node) => node is Node n && FenJsBrowserScriptEngine.IsSourceElement(n);

        public string GetSourceAttribute(object source, string name) => (source as Element)?.GetAttribute(name);

        /// <summary>HTML "encoding-parse a URL" against the node document's base URL.</summary>
        public string ResolveUrl(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            try
            {
                var document = _element.OwnerDocument;
                var baseRaw = document?.BaseURI ?? document?.DocumentURI ?? document?.URL;
                WhatwgUrl baseUrl = null;
                if (!string.IsNullOrWhiteSpace(baseRaw))
                {
                    baseUrl = WhatwgUrl.Parse(baseRaw);
                }

                return WhatwgUrl.Parse(value, baseUrl)?.Href;
            }
            catch
            {
                return null;
            }
        }

        public bool MediaQueryMatches(string query) => Rendering.CssParser.EvaluateMediaQuery(query);

        // -- autoplay --

        public bool IsAllowedToPlay =>
            MediaEngineServices.Autoplay.IsAllowedToPlay(
                _realm.HasUserActivation,
                inaudible: Controller.Muted || Controller.Volume <= 0.0);

        /// <summary>
        /// The "sandboxed automatic features browsing context flag" is unset by
        /// <c>allow-scripts</c>; the "autoplay" permissions-policy feature has no separate
        /// evaluator yet and is treated as allowed.
        /// </summary>
        public bool DocumentAllowsAutoplay => _realm.AllowsEmbeddingSandboxFlag(IframeSandboxFlags.Scripts);

        // -- tasks and events --

        public void QueueTask(Action task) => _realm.QueueMediaTask(_element.OwnerDocument, task);

        /// <summary>
        /// HTML "await a stable state": the steps run as a microtask, at the end of the task
        /// that asked for them, after any script that is currently running.
        /// </summary>
        public void AwaitStableState(Action continuation)
        {
            var interpreter = _realm._interpreter;
            if (interpreter == null || _realm._realmAbandoned)
            {
                return;
            }

            var callback = interpreter.AllocateNativeFunction(
                "mediaStableState",
                (_, _) =>
                {
                    continuation();
                    return JsValue.Undefined;
                });
            ((IBuiltinContext)interpreter).EnqueueMicrotask(callback);
        }

        public void FireEvent(string type) => Fire(_element, type);

        public void FireEventAt(object node, string type)
        {
            if (node is Element element)
            {
                Fire(element, type);
            }
        }

        // Media events do not bubble and are not cancelable (§4.8.11.17).
        private void Fire(Element target, string type)
        {
            if (_realm._realmAbandoned || _realm._interpreter == null)
            {
                return;
            }

            var eventValue = _realm.CreateBrowserDomEventValue(
                target,
                type,
                new BrowserDomEventInit { Bubbles = false, Cancelable = false, Composed = false, IsTrusted = true },
                out var dispatchState);
            _ = _realm.DispatchEventFull(target, type, eventValue, dispatchState);
        }

        public void SetDelayingLoadEvent(bool delaying)
        {
            // The document load event is not yet delayed by media fetches; the flag is
            // recorded by the controller and read by tests, nothing more happens here.
        }

        public void InvalidateRendering(bool sizeChanged)
        {
            InvalidatePaintForElement(_element);
        }

        // -- promises --

        public object CreatePromise()
        {
            var (promise, resolve, reject) = ((IBuiltinContext)_realm._interpreter).CreatePromiseCapability();
            var pending = new MediaPromise(promise, resolve, reject);
            _pendingPromises.Add(pending);
            return pending;
        }

        public void ResolvePromise(object promise)
        {
            if (promise is not MediaPromise pending || !_pendingPromises.Remove(pending))
            {
                return;
            }

            _ = _realm._interpreter.InvokeFunction(pending.Resolve, Array.Empty<JsValue>(), JsValue.Undefined);
        }

        public void RejectPromise(object promise, PlayRejection reason)
        {
            if (promise is not MediaPromise pending || !_pendingPromises.Remove(pending))
            {
                return;
            }

            var (name, message) = reason switch
            {
                PlayRejection.NotAllowedError => ("NotAllowedError", "play() failed because the user didn't interact with the document first."),
                PlayRejection.NotSupportedError => ("NotSupportedError", "The element has no supported sources."),
                _ => ("AbortError", "The play() request was interrupted by a call to pause() or a new load."),
            };
            _ = _realm._interpreter.InvokeFunction(
                pending.Reject,
                new[] { _realm.CreateDomExceptionValue(name, message) },
                JsValue.Undefined);
        }

        public JsValue PromiseValue(object promise) => promise is MediaPromise pending ? pending.Promise : JsValue.Undefined;

        // -- the pipeline (M2) --

        /// <summary>No demuxer or decoder is wired in yet, so no resource can be started.</summary>
        public IMediaResource StartResource(MediaFetchRequest request, IMediaResourceClient client) => null;

        // -- the error object --

        /// <summary>The <c>MediaError</c> for the current error, the same object until the error changes.</summary>
        public JsValue ErrorValue
        {
            get
            {
                var error = Controller.Error;
                if (error == null)
                {
                    _errorObject = JsValue.Undefined;
                    _errorObjectFor = null;
                    return JsValue.Null;
                }

                if (_errorObject.Tag == JsValueTag.Object && ReferenceEquals(_errorObjectFor, error))
                {
                    return _errorObject;
                }

                _errorObjectFor = error;
                _errorObject = _realm._interpreter.AllocateObject(new Dictionary<string, JsValue>
                {
                    ["code"] = JsValue.FromInt32((int)error.Code),
                    ["message"] = JsValue.FromString(error.Message ?? string.Empty),
                    ["MEDIA_ERR_ABORTED"] = JsValue.FromInt32(1),
                    ["MEDIA_ERR_NETWORK"] = JsValue.FromInt32(2),
                    ["MEDIA_ERR_DECODE"] = JsValue.FromInt32(3),
                    ["MEDIA_ERR_SRC_NOT_SUPPORTED"] = JsValue.FromInt32(4),
                });
                return _errorObject;
            }
        }

        public void TraceRoots(IHeapTracer tracer)
        {
            foreach (var pending in _pendingPromises)
            {
                TraceJsRoot(tracer, pending.Promise);
                TraceJsRoot(tracer, pending.Resolve);
                TraceJsRoot(tracer, pending.Reject);
            }

            if (_errorObject.Tag == JsValueTag.Object)
            {
                TraceJsRoot(tracer, _errorObject);
            }
        }

        private sealed record MediaPromise(JsValue Promise, JsValue Resolve, JsValue Reject);
    }

    // ---- The IDL surface (HTML §4.8.9 HTMLVideoElement, §4.8.11 HTMLMediaElement) ---------------

    private JsValue CreateDomExceptionValue(string name, string message)
    {
        var ctor = ReadGlobalValueOrUndefined("DOMException");
        if (_interpreter.CanCallValue(ctor))
        {
            return _interpreter.ConstructValue(ctor, new[] { JsValue.FromString(message), JsValue.FromString(name) });
        }

        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["name"] = JsValue.FromString(name),
            ["message"] = JsValue.FromString(message),
        });
    }

    /// <summary>A fresh <c>TimeRanges</c> (§4.8.11.13) snapshot of <paramref name="ranges"/>.</summary>
    private JsValue CreateTimeRangesValue(MediaTimeRanges ranges)
    {
        var count = ranges.Count;
        var starts = new double[count];
        var ends = new double[count];
        for (var i = 0; i < count; i++)
        {
            starts[i] = ranges.Start(i).TotalSeconds;
            ends[i] = ranges.End(i).TotalSeconds;
        }

        JsValue At(double[] values, IReadOnlyList<JsValue> args)
        {
            var index = args.Count > 0 ? CoerceToHostNumber(args[0]) : double.NaN;
            if (!(index >= 0 && index < count) || Math.Floor(index) != index)
            {
                ThrowDomException("IndexSizeError", $"The index provided ({index}) is outside the range [0, {count}).");
            }

            return JsValue.FromNumber(values[(int)index]);
        }

        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["length"] = JsValue.FromInt32(count),
            ["start"] = _interpreter.AllocateNativeFunction("start", (_, args) => At(starts, args), length: 1),
            ["end"] = _interpreter.AllocateNativeFunction("end", (_, args) => At(ends, args), length: 1),
        });
    }

    private static double CoerceToHostNumber(JsValue value) => value.Tag switch
    {
        JsValueTag.Number => value.AsNumber(),
        JsValueTag.Int32 => value.AsInt32(),
        JsValueTag.Boolean => value.AsBoolean() ? 1 : 0,
        JsValueTag.Undefined => double.NaN,
        JsValueTag.Null => 0,
        _ => double.TryParse(CoerceToHostString(value), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN,
    };

    /// <summary>
    /// Reflects a limited-value enumerated attribute (HTML "reflect" for enumerated
    /// attributes): a known keyword as its canonical form, otherwise the invalid-value
    /// default, or the missing-value default when absent.
    /// </summary>
    private static string ReflectEnumeratedAttribute(Element element, string name, string[] keywords, string missingDefault, string invalidDefault)
    {
        var raw = element.GetAttribute(name);
        if (raw == null)
        {
            return missingDefault;
        }

        foreach (var keyword in keywords)
        {
            if (string.Equals(raw, keyword, StringComparison.OrdinalIgnoreCase))
            {
                return keyword;
            }
        }

        return invalidDefault;
    }

    private static readonly string[] CrossOriginKeywords = { "anonymous", "use-credentials" };
    private static readonly string[] PreloadKeywords = { "none", "metadata", "auto" };

    internal bool TryGetMediaElementProperty(Element element, string property, out JsValue value)
    {
        MediaElementBinding binding;
        switch (property)
        {
            // Constants live on the interface object and the prototype (WebIDL 3.7.4),
            // and script commonly reads them off the element itself.
            case "NETWORK_EMPTY": value = JsValue.FromInt32(0); return true;
            case "NETWORK_IDLE": value = JsValue.FromInt32(1); return true;
            case "NETWORK_LOADING": value = JsValue.FromInt32(2); return true;
            case "NETWORK_NO_SOURCE": value = JsValue.FromInt32(3); return true;
            case "HAVE_NOTHING": value = JsValue.FromInt32(0); return true;
            case "HAVE_METADATA": value = JsValue.FromInt32(1); return true;
            case "HAVE_CURRENT_DATA": value = JsValue.FromInt32(2); return true;
            case "HAVE_FUTURE_DATA": value = JsValue.FromInt32(3); return true;
            case "HAVE_ENOUGH_DATA": value = JsValue.FromInt32(4); return true;

            // Reflected content attributes need no controller.
            case "src":
                value = JsValue.FromString(ResolveElementUrlProperty(element, "src"));
                return true;
            case "crossOrigin":
                // Nullable enumerated reflection: null when absent, "anonymous" for
                // anything unrecognised, including the empty string.
                var crossOrigin = element.GetAttribute("crossorigin");
                value = crossOrigin == null
                    ? JsValue.Null
                    : JsValue.FromString(ReflectEnumeratedAttribute(element, "crossorigin", CrossOriginKeywords, "anonymous", "anonymous"));
                return true;
            case "preload":
                // Missing-value default is "metadata" here (autoplay elements aside), the
                // invalid-value default is "auto", as in Chromium and WebKit.
                value = JsValue.FromString(ReflectEnumeratedAttribute(element, "preload", PreloadKeywords, "metadata", "auto"));
                return true;
            case "autoplay": value = JsValue.FromBoolean(element.HasAttribute("autoplay")); return true;
            case "loop": value = JsValue.FromBoolean(element.HasAttribute("loop")); return true;
            case "controls": value = JsValue.FromBoolean(element.HasAttribute("controls")); return true;
            case "defaultMuted": value = JsValue.FromBoolean(element.HasAttribute("muted")); return true;

            case "poster" when IsVideoElement(element):
                value = JsValue.FromString(ResolveElementUrlProperty(element, "poster"));
                return true;
            case "playsInline" when IsVideoElement(element):
                value = JsValue.FromBoolean(element.HasAttribute("playsinline"));
                return true;
            case "width" when IsVideoElement(element):
                value = JsValue.FromInt32(ReflectUnsignedLong(element, "width"));
                return true;
            case "height" when IsVideoElement(element):
                value = JsValue.FromInt32(ReflectUnsignedLong(element, "height"));
                return true;

            // Everything else is state, so it needs the controller.
            case "error":
                value = GetOrCreateMediaBinding(element).ErrorValue;
                return true;
            case "currentSrc":
                value = JsValue.FromString(GetOrCreateMediaBinding(element).Controller.CurrentSrc);
                return true;
            case "networkState":
                value = JsValue.FromInt32((int)GetOrCreateMediaBinding(element).Controller.NetworkState);
                return true;
            case "readyState":
                value = JsValue.FromInt32((int)GetOrCreateMediaBinding(element).Controller.ReadyState);
                return true;
            case "buffered":
                value = CreateTimeRangesValue(GetOrCreateMediaBinding(element).Controller.Buffered);
                return true;
            case "seekable":
                value = CreateTimeRangesValue(GetOrCreateMediaBinding(element).Controller.Seekable);
                return true;
            case "played":
                value = CreateTimeRangesValue(GetOrCreateMediaBinding(element).Controller.Played);
                return true;
            case "seeking":
                value = JsValue.FromBoolean(GetOrCreateMediaBinding(element).Controller.Seeking);
                return true;
            case "currentTime":
                value = JsValue.FromNumber(GetOrCreateMediaBinding(element).Controller.CurrentTime);
                return true;
            case "duration":
                value = JsValue.FromNumber(GetOrCreateMediaBinding(element).Controller.Duration);
                return true;
            case "paused":
                value = JsValue.FromBoolean(GetOrCreateMediaBinding(element).Controller.Paused);
                return true;
            case "ended":
                value = JsValue.FromBoolean(GetOrCreateMediaBinding(element).Controller.Ended);
                return true;
            case "defaultPlaybackRate":
                value = JsValue.FromNumber(GetOrCreateMediaBinding(element).Controller.DefaultPlaybackRate);
                return true;
            case "playbackRate":
                value = JsValue.FromNumber(GetOrCreateMediaBinding(element).Controller.PlaybackRate);
                return true;
            case "preservesPitch":
                value = JsValue.FromBoolean(GetOrCreateMediaBinding(element).Controller.PreservesPitch);
                return true;
            case "volume":
                value = JsValue.FromNumber(GetOrCreateMediaBinding(element).Controller.Volume);
                return true;
            case "muted":
                value = JsValue.FromBoolean(GetOrCreateMediaBinding(element).Controller.Muted);
                return true;
            case "videoWidth" when IsVideoElement(element):
                value = JsValue.FromInt32(GetOrCreateMediaBinding(element).Controller.VideoWidth);
                return true;
            case "videoHeight" when IsVideoElement(element):
                value = JsValue.FromInt32(GetOrCreateMediaBinding(element).Controller.VideoHeight);
                return true;

            case "load":
                binding = GetOrCreateMediaBinding(element);
                value = GetOrCreateHostCallable(element, "load", (_, _) =>
                {
                    binding.Controller.Load();
                    return JsValue.Undefined;
                });
                return true;
            case "canPlayType":
                binding = GetOrCreateMediaBinding(element);
                value = GetOrCreateHostCallable(element, "canPlayType", (_, args) =>
                {
                    var type = args.Count > 0 ? CoerceToHostString(args[0]) ?? string.Empty : "undefined";
                    return JsValue.FromString(binding.Controller.CanPlayType(type));
                }, length: 1);
                return true;
            case "play":
                binding = GetOrCreateMediaBinding(element);
                value = GetOrCreateHostCallable(element, "play", (_, _) => binding.PromiseValue(binding.Controller.Play()));
                return true;
            case "pause":
                binding = GetOrCreateMediaBinding(element);
                value = GetOrCreateHostCallable(element, "pause", (_, _) =>
                {
                    binding.Controller.Pause();
                    return JsValue.Undefined;
                });
                return true;
            case "fastSeek":
                binding = GetOrCreateMediaBinding(element);
                value = GetOrCreateHostCallable(element, "fastSeek", (_, args) =>
                {
                    var time = args.Count > 0 ? CoerceToHostNumber(args[0]) : double.NaN;
                    if (double.IsNaN(time) || double.IsInfinity(time))
                    {
                        ThrowDomException("TypeError", "Failed to execute 'fastSeek': the provided double value is non-finite.");
                    }

                    binding.Controller.FastSeek(time);
                    return JsValue.Undefined;
                }, length: 1);
                return true;

            default:
                value = JsValue.Undefined;
                return false;
        }
    }

    internal bool TrySetMediaElementProperty(Element element, string property, JsValue value)
    {
        switch (property)
        {
            case "src":
                // Setting the reflected attribute is what runs the load algorithm, through
                // the attribute mutation the DOM reports.
                element.SetAttribute("src", CoerceToHostString(value) ?? string.Empty);
                return true;
            case "crossOrigin":
                if (value.Tag == JsValueTag.Null || value.Tag == JsValueTag.Undefined)
                {
                    element.RemoveAttribute("crossorigin");
                }
                else
                {
                    element.SetAttribute("crossorigin", CoerceToHostString(value) ?? string.Empty);
                }

                return true;
            case "preload":
                element.SetAttribute("preload", CoerceToHostString(value) ?? string.Empty);
                return true;
            case "autoplay":
                SetBooleanAttribute(element, "autoplay", value);
                return true;
            case "loop":
                SetBooleanAttribute(element, "loop", value);
                return true;
            case "controls":
                SetBooleanAttribute(element, "controls", value);
                return true;
            case "defaultMuted":
                SetBooleanAttribute(element, "muted", value);
                return true;
            case "poster" when IsVideoElement(element):
                element.SetAttribute("poster", CoerceToHostString(value) ?? string.Empty);
                InvalidatePaintForElement(element);
                return true;
            case "playsInline" when IsVideoElement(element):
                SetBooleanAttribute(element, "playsinline", value);
                return true;
            case "width" when IsVideoElement(element):
            case "height" when IsVideoElement(element):
                element.SetAttribute(property, ((uint)Math.Clamp(CoerceToHostNumber(value), 0, 2147483647)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                InvalidatePaintForElement(element);
                return true;

            case "currentTime":
                GetOrCreateMediaBinding(element).Controller.SetCurrentTime(RequireFiniteNumber(value, "currentTime"));
                return true;
            case "defaultPlaybackRate":
                GetOrCreateMediaBinding(element).Controller.DefaultPlaybackRate = RequireFiniteNumber(value, "defaultPlaybackRate");
                return true;
            case "playbackRate":
                if (!GetOrCreateMediaBinding(element).Controller.TrySetPlaybackRate(RequireFiniteNumber(value, "playbackRate")))
                {
                    ThrowDomException("NotSupportedError", "The provided playback rate is not supported.");
                }

                return true;
            case "preservesPitch":
                GetOrCreateMediaBinding(element).Controller.PreservesPitch = ToHostBoolean(value);
                return true;
            case "volume":
                if (!GetOrCreateMediaBinding(element).Controller.TrySetVolume(RequireFiniteNumber(value, "volume")))
                {
                    ThrowDomException("IndexSizeError", "The volume provided is outside the range [0, 1].");
                }

                return true;
            case "muted":
                GetOrCreateMediaBinding(element).Controller.SetMuted(ToHostBoolean(value));
                return true;

            default:
                return false;
        }
    }

    private double RequireFiniteNumber(JsValue value, string property)
    {
        var number = CoerceToHostNumber(value);
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            ThrowDomException("TypeError", $"Failed to set the '{property}' property: the provided double value is non-finite.");
        }

        return number;
    }

    private static bool ToHostBoolean(JsValue value) => value.Tag switch
    {
        JsValueTag.Boolean => value.AsBoolean(),
        JsValueTag.Undefined or JsValueTag.Null => false,
        JsValueTag.Number => value.AsNumber() != 0 && !double.IsNaN(value.AsNumber()),
        JsValueTag.Int32 => value.AsInt32() != 0,
        JsValueTag.String => !string.IsNullOrEmpty(CoerceToHostString(value)),
        _ => true,
    };

    private static void SetBooleanAttribute(Element element, string name, JsValue value)
    {
        if (ToHostBoolean(value))
        {
            element.SetAttribute(name, string.Empty);
        }
        else
        {
            element.RemoveAttribute(name);
        }
    }

    /// <summary>HTML "rules for parsing non-negative integers" for a reflected unsigned long, 0 when absent or invalid.</summary>
    private static int ReflectUnsignedLong(Element element, string name)
    {
        var raw = element.GetAttribute(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        var text = raw.Trim();
        var end = 0;
        if (end < text.Length && text[end] == '+')
        {
            end++;
        }

        var start = end;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        if (end == start || !long.TryParse(text.AsSpan(start, end - start), out var parsed))
        {
            return 0;
        }

        return parsed > 2147483647 ? 0 : (int)parsed;
    }
}
