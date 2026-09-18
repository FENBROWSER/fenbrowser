using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Media.Text;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Text tracks (HTML §4.8.12) and the <c>track</c> element (§4.8.12.11): the object
/// model — TextTrackList, TextTrack, TextTrackCueList, TextTrackCue, VTTCue, VTTRegion,
/// TrackEvent and the audio/video track lists — lives in the realm as script, installed by
/// <see cref="InstallFenJsTextTracks"/>. The pieces that need the engine are native: the
/// WebVTT parsers in <c>FenBrowser.Media</c>, the track fetch with its CORS rules, the media
/// element's playback state, and the media element task queue. The media element notifies
/// the script model through <see cref="OnTextTrackPlaybackPositionChanged"/> and the DOM
/// mutation walk tells it when <c>track</c> elements come and go.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private static readonly string[] s_trackElementAttributes = ["src", "kind", "srclang", "label", "default"];

    internal static bool IsTrackElement(Element element) =>
        element != null &&
        string.Equals(element.LocalName, "track", StringComparison.OrdinalIgnoreCase) &&
        HtmlElementInterfaceCatalog.IsHtmlNamespace(element.NamespaceUri);

    private static bool IsTrackElementAttribute(string name)
    {
        foreach (var attribute in s_trackElementAttributes)
        {
            if (string.Equals(attribute, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void InstallFenJsTextTracks()
    {
        _interpreter.RegisterGlobalValue(
            "__fenVttParse",
            _interpreter.AllocateNativeFunction("__fenVttParse", (_, args) => ParseWebVttForScript(args), length: 1));
        _interpreter.RegisterGlobalValue(
            "__fenVttCueTree",
            _interpreter.AllocateNativeFunction("__fenVttCueTree", (_, args) => ParseWebVttCueTextForScript(args), length: 2));
        _interpreter.RegisterGlobalValue(
            "__fenTrackFetch",
            _interpreter.AllocateNativeFunction("__fenTrackFetch", (_, args) => StartTrackFetch(args), length: 3));
        _interpreter.RegisterGlobalValue(
            "__fenMediaState",
            _interpreter.AllocateNativeFunction("__fenMediaState", (_, args) => ReadMediaStateForTextTracks(args), length: 1));
        _interpreter.RegisterGlobalValue(
            "__fenQueueMediaTask",
            _interpreter.AllocateNativeFunction("__fenQueueMediaTask", (_, args) => QueueMediaTaskFromScript(args), length: 2));

        try
        {
            EvaluateWithFenJsRaw(TextTracksPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] text track prelude failed: {ex.Message}", LogCategory.JavaScript);
        }
    }

    // -- native helpers the prelude calls ------------------------------------------------

    /// <summary>WebVTT §6.1 over the fetched text; null when the file has no WebVTT signature.</summary>
    private JsValue ParseWebVttForScript(IReadOnlyList<JsValue> args)
    {
        var text = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
        var file = WebVttParser.Parse(text ?? string.Empty);
        if (file == null)
        {
            return JsValue.Null;
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("regions");
            writer.WriteStartArray();
            foreach (var region in file.Regions)
            {
                writer.WriteStartObject();
                writer.WriteString("id", region.Id);
                writer.WriteNumber("width", region.Width);
                writer.WriteNumber("lines", region.Lines);
                writer.WriteNumber("regionAnchorX", region.RegionAnchorX);
                writer.WriteNumber("regionAnchorY", region.RegionAnchorY);
                writer.WriteNumber("viewportAnchorX", region.ViewportAnchorX);
                writer.WriteNumber("viewportAnchorY", region.ViewportAnchorY);
                writer.WriteString("scroll", region.Scroll);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("cues");
            writer.WriteStartArray();
            foreach (var cue in file.Cues)
            {
                writer.WriteStartObject();
                writer.WriteString("id", cue.Id);
                writer.WriteNumber("startTime", cue.Start.TotalSeconds);
                writer.WriteNumber("endTime", cue.End.TotalSeconds);
                writer.WriteString("text", cue.Text);
                if (cue.Region != null)
                {
                    writer.WriteNumber("region", file.Regions.IndexOf(cue.Region));
                }

                writer.WriteString("vertical", cue.Vertical switch
                {
                    VttVertical.RightToLeft => "rl",
                    VttVertical.LeftToRight => "lr",
                    _ => string.Empty,
                });
                writer.WriteBoolean("snapToLines", cue.SnapToLines);
                if (cue.Line is { } line)
                {
                    writer.WriteNumber("line", line);
                }

                writer.WriteString("lineAlign", cue.LineAlign switch
                {
                    VttLineAlign.Center => "center",
                    VttLineAlign.End => "end",
                    _ => "start",
                });
                if (cue.Position is { } position)
                {
                    writer.WriteNumber("position", position);
                }

                writer.WriteString("positionAlign", cue.PositionAlign switch
                {
                    VttPositionAlign.LineLeft => "line-left",
                    VttPositionAlign.Center => "center",
                    VttPositionAlign.LineRight => "line-right",
                    _ => "auto",
                });
                writer.WriteNumber("size", cue.Size);
                writer.WriteString("align", cue.Align switch
                {
                    VttAlign.Start => "start",
                    VttAlign.End => "end",
                    VttAlign.Left => "left",
                    VttAlign.Right => "right",
                    _ => "center",
                });
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("stylesheets");
            writer.WriteStartArray();
            foreach (var sheet in file.Stylesheets)
            {
                writer.WriteStringValue(sheet);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsValue.FromString(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>WebVTT §6.4 cue text as a JSON tree the prelude turns into a DocumentFragment.</summary>
    private JsValue ParseWebVttCueTextForScript(IReadOnlyList<JsValue> args)
    {
        var text = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
        var language = args.Count > 1 && args[1].Tag == JsValueTag.String ? CoerceToHostString(args[1]) : string.Empty;
        var root = WebVttCueText.Parse(text ?? string.Empty, language ?? string.Empty);

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteNode(writer, root);
        }

        return JsValue.FromString(Encoding.UTF8.GetString(stream.ToArray()));

        static void WriteNode(Utf8JsonWriter writer, VttNode node)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", node.Kind switch
            {
                VttNodeKind.Class => "c",
                VttNodeKind.Italic => "i",
                VttNodeKind.Bold => "b",
                VttNodeKind.Underline => "u",
                VttNodeKind.Ruby => "ruby",
                VttNodeKind.RubyText => "rt",
                VttNodeKind.Voice => "v",
                VttNodeKind.Language => "lang",
                VttNodeKind.Text => "text",
                VttNodeKind.Timestamp => "timestamp",
                _ => "root",
            });
            if (node.Kind == VttNodeKind.Text || node.Kind == VttNodeKind.Timestamp)
            {
                writer.WriteString("text", node.Text);
                writer.WriteEndObject();
                return;
            }

            if (node.Classes.Count > 0)
            {
                writer.WriteString("classes", string.Join(' ', node.Classes));
            }

            if (node.Kind is VttNodeKind.Voice or VttNodeKind.Language)
            {
                writer.WriteString("annotation", node.Annotation);
            }

            writer.WriteString("lang", node.Language);
            writer.WritePropertyName("children");
            writer.WriteStartArray();
            foreach (var child in node.Children)
            {
                WriteNode(writer, child);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// HTML §4.8.12.11.3 track fetch: a potentially-CORS-enabled fetch in the media element's
    /// CORS mode. Without a <c>crossorigin</c> attribute a cross-origin track is a network
    /// error (the response would be opaque); with one, the response must pass the CORS
    /// check. WebVTT is always decoded as UTF-8 (WebVTT §6.1 step 1).
    /// </summary>
    private JsValue StartTrackFetch(IReadOnlyList<JsValue> args)
    {
        var urlText = args.Count > 0 ? CoerceToHostString(args[0]) : string.Empty;
        var crossOrigin = args.Count > 1 && args[1].Tag == JsValueTag.String ? CoerceToHostString(args[1]) : null;
        var callback = args.Count > 2 ? args[2] : JsValue.Undefined;
        if (!_interpreter.CanCallValue(callback))
        {
            return JsValue.Undefined;
        }

        var pin = PinFenJsValues(callback);

        void Deliver(bool ok, string text)
        {
            QueueOnDeliveryTail(() =>
            {
                try
                {
                    using (ScriptEngineLockProbe.Hold(_fenJsLock))
                    {
                        _ = _interpreter.InvokeFunction(callback, new[] { JsValue.FromBoolean(ok), JsValue.FromString(text ?? string.Empty) }, JsValue.Undefined);
                        _interpreter.PumpMicrotasks();
                    }
                }
                catch (JsThrownException ex)
                {
                    EngineLogCompat.Warn($"[FenJsBridge] track load completion failed: {ex.Description ?? ex.Message}", LogCategory.JavaScript);
                }
                finally
                {
                    pin.Dispose();
                }
            }, "track fetch completion failed");
        }

        if (string.IsNullOrEmpty(urlText) || !TryResolveUri(urlText, _currentBaseUri, out var requestUri))
        {
            Deliver(false, string.Empty);
            return JsValue.Undefined;
        }

        if (string.Equals(requestUri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
        {
            // A data: URL is same-origin-ish for this purpose (its response is not opaque).
            var decoded = TryDecodeDataUrl(requestUri.OriginalString, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
            Deliver(decoded != null, decoded);
            return JsValue.Undefined;
        }

        var sameOrigin = _currentBaseUri != null && CorsHandler.IsSameOrigin(requestUri, _currentBaseUri);
        if (!sameOrigin && crossOrigin == null)
        {
            EngineLogCompat.Debug($"[FenJsBridge] track '{requestUri}' is cross-origin without crossorigin: not loaded", LogCategory.JavaScript);
            Deliver(false, string.Empty);
            return JsValue.Undefined;
        }

        var handler = FetchHandler;
        if (handler == null)
        {
            Deliver(false, string.Empty);
            return JsValue.Undefined;
        }

        HttpRequestMessage request;
        try
        {
            request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "track");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Accept", "text/vtt, */*;q=0.8");
            if (_currentBaseUri != null)
            {
                request.Headers.Referrer = _currentBaseUri;
                if (!sameOrigin)
                {
                    var origin = CorsHandler.SerializeOrigin(new UriBuilder(
                        _currentBaseUri.Scheme,
                        _currentBaseUri.Host,
                        _currentBaseUri.IsDefaultPort ? -1 : _currentBaseUri.Port).Uri);
                    if (!string.IsNullOrWhiteSpace(origin))
                    {
                        request.Headers.TryAddWithoutValidation("Origin", origin);
                    }
                }
            }

            request.Options.Set(
                new HttpRequestOptionsKey<string>(CorsHandler.CredentialsModeOptionKey),
                string.Equals(crossOrigin, "use-credentials", StringComparison.OrdinalIgnoreCase) ? "include" : "same-origin");
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] track fetch could not build request for '{requestUri}': {ex.Message}", LogCategory.JavaScript);
            Deliver(false, string.Empty);
            return JsValue.Undefined;
        }

        var documentOrigin = _currentBaseUri;
        _ = Task.Run(async () =>
        {
            HttpResponseMessage response = null;
            try
            {
                response = await handler(request).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status < 200 || status > 299)
                {
                    Deliver(false, string.Empty);
                    return;
                }

                if (!sameOrigin && !CorsHandler.IsCorsAllowed(response, requestUri, documentOrigin))
                {
                    EngineLogCompat.Debug($"[FenJsBridge] track '{requestUri}' failed the CORS check", LogCategory.JavaScript);
                    Deliver(false, string.Empty);
                    return;
                }

                var bytes = response.Content != null
                    ? await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)
                    : Array.Empty<byte>();
                Deliver(true, Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[FenJsBridge] track fetch failed for '{requestUri}': {ex.Message}", LogCategory.JavaScript);
                Deliver(false, string.Empty);
            }
            finally
            {
                response?.Dispose();
                request.Dispose();
            }
        });

        return JsValue.Undefined;
    }

    /// <summary>RFC 2397: <c>data:[mediatype][;base64],data</c>, percent-decoded unless base64.</summary>
    private static bool TryDecodeDataUrl(string url, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var comma = url.IndexOf(',');
        if (comma < 5 || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var header = url.Substring(5, comma - 5);
        var payload = url.Substring(comma + 1);
        try
        {
            if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Convert.FromBase64String(Uri.UnescapeDataString(payload).Trim());
                return true;
            }

            bytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>What "time marches on" needs from the media element, read off its controller.</summary>
    private JsValue ReadMediaStateForTextTracks(IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0 || ResolveHostObjectOrNull(args[0]) is not Element element || !IsMediaElement(element))
        {
            return JsValue.Null;
        }

        var controller = GetOrCreateMediaBinding(element).Controller;
        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["currentTime"] = JsValue.FromNumber(controller.CurrentTime),
            ["showPoster"] = JsValue.FromBoolean(controller.ShowPoster),
            ["paused"] = JsValue.FromBoolean(controller.Paused),
            ["seeking"] = JsValue.FromBoolean(controller.Seeking),
            ["readyState"] = JsValue.FromInt32((int)controller.ReadyState),
        });
    }

    /// <summary>Queues a script callback on the media element event task source.</summary>
    private JsValue QueueMediaTaskFromScript(IReadOnlyList<JsValue> args)
    {
        if (args.Count < 2 || !_interpreter.CanCallValue(args[1]))
        {
            return JsValue.Undefined;
        }

        var document = ResolveHostObjectOrNull(args[0]) is Node node ? node.OwnerDocument : null;
        var callback = args[1];
        var pin = PinFenJsValues(callback);
        QueueMediaTask(document, () =>
        {
            try
            {
                _ = _interpreter.InvokeFunction(callback, Array.Empty<JsValue>(), JsValue.Undefined);
            }
            catch (JsThrownException ex)
            {
                EngineLogCompat.Warn($"[FenJsBridge] text track task failed: {ex.Description ?? ex.Message}", LogCategory.JavaScript);
            }
            finally
            {
                pin.Dispose();
            }
        });
        return JsValue.Undefined;
    }

    // -- calls from the engine into the prelude ----------------------------------------------

    private JsValue CallTextTrackHook(string name, params JsValue[] args)
    {
        if (_realmAbandoned || _interpreter == null)
        {
            return JsValue.Undefined;
        }

        var hook = ReadGlobalValueOrUndefined(name);
        if (!_interpreter.CanCallValue(hook))
        {
            return JsValue.Undefined;
        }

        try
        {
            return _interpreter.InvokeFunction(hook, args, JsValue.Undefined);
        }
        catch (JsThrownException ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] {name} failed: {ex.Description ?? ex.Message}", LogCategory.JavaScript);
            return JsValue.Undefined;
        }
    }

    /// <summary>The media element's current playback position moved: run "time marches on".</summary>
    private void OnTextTrackPlaybackPositionChanged(Element element, bool monotonic)
    {
        if (!_textTrackModelTouched.TryGetValue(element, out _))
        {
            // No script and no track element ever reached this element's text tracks, so
            // there is nothing to activate; the hook would only create the model.
            return;
        }

        _ = CallTextTrackHook("__fenTextTracksTimeMarchesOn", ToHostNodeOrNull(element), JsValue.FromBoolean(monotonic));
    }

    private void OnTextTracksReset(Element element)
    {
        if (_textTrackModelTouched.TryGetValue(element, out _))
        {
            _ = CallTextTrackHook("__fenTextTracksReset", ToHostNodeOrNull(element));
        }
    }

    // Media elements whose text track model exists; the position hook is skipped for others.
    private readonly ConditionalWeakTable<Element, object> _textTrackModelTouched = new();

    private void MarkTextTrackModel(Element element)
    {
        if (!_textTrackModelTouched.TryGetValue(element, out _))
        {
            _textTrackModelTouched.Add(element, string.Empty);
        }
    }

    private void OnTrackElementInserted(Element mediaElement, Element trackElement)
    {
        MarkTextTrackModel(mediaElement);
        _ = CallTextTrackHook("__fenTrackElementInserted", ToHostNodeOrNull(mediaElement), ToHostNodeOrNull(trackElement));
    }

    private void OnTrackElementRemoved(Element mediaElement, Element trackElement)
    {
        _ = CallTextTrackHook("__fenTrackElementRemoved", ToHostNodeOrNull(mediaElement), ToHostNodeOrNull(trackElement));
    }

    private void OnTrackElementAttributeChanged(Element trackElement, string attribute)
    {
        if (trackElement.ParentNode is Element parent && IsMediaElement(parent))
        {
            MarkTextTrackModel(parent);
        }

        _ = CallTextTrackHook("__fenTrackElementAttributeChanged", ToHostNodeOrNull(trackElement), JsValue.FromString(attribute.ToLowerInvariant()));
    }

    /// <summary>Runs <paramref name="action"/> on the JS worker of the realm owning <paramref name="element"/>.</summary>
    private void RunOnTextTrackThread(Element element, Action action)
    {
        var realm = MediaRealmFor(element);
        if (_onFenJsLargeStackThread)
        {
            action();
            return;
        }

        realm.QueueMediaTask(element.OwnerDocument, action);
    }

    /// <summary>The <c>track</c> children a media element already has when its binding starts.</summary>
    private void NotifyExistingTrackElements(Element mediaElement)
    {
        for (var child = mediaElement.FirstChild; child != null; child = child.NextSibling)
        {
            if (child is Element element && IsTrackElement(element))
            {
                OnTrackElementInserted(mediaElement, element);
            }
        }
    }

    // -- element properties --------------------------------------------------------------

    internal bool TryGetTrackElementProperty(Element element, string property, out JsValue value)
    {
        switch (property)
        {
            case "NONE": value = JsValue.FromInt32(0); return true;
            case "LOADING": value = JsValue.FromInt32(1); return true;
            case "LOADED": value = JsValue.FromInt32(2); return true;
            case "ERROR": value = JsValue.FromInt32(3); return true;
            case "track":
                if (element.ParentNode is Element parent && IsMediaElement(parent))
                {
                    MarkTextTrackModel(parent);
                }

                value = CallTextTrackHook("__fenTrackElementTrack", ToHostNodeOrNull(element));
                return value.Tag != JsValueTag.Undefined;
            case "readyState":
                value = CallTextTrackHook("__fenTrackElementReadyState", ToHostNodeOrNull(element));
                if (value.Tag == JsValueTag.Undefined)
                {
                    value = JsValue.FromInt32(0);
                }

                return true;
            case "src":
                value = JsValue.FromString(ReflectUrlAttribute(element, "src"));
                return true;
            case "kind":
                value = JsValue.FromString(ReflectEnumeratedAttribute(element, "kind", TrackKindKeywords, "subtitles", "metadata"));
                return true;
            case "srclang":
                value = JsValue.FromString(element.GetAttribute("srclang") ?? string.Empty);
                return true;
            case "label":
                value = JsValue.FromString(element.GetAttribute("label") ?? string.Empty);
                return true;
            case "default":
                value = JsValue.FromBoolean(element.HasAttribute("default"));
                return true;
            default:
                value = JsValue.Undefined;
                return false;
        }
    }

    internal bool TrySetTrackElementProperty(Element element, string property, JsValue value)
    {
        switch (property)
        {
            case "src":
            case "kind":
            case "srclang":
            case "label":
                element.SetAttribute(property, CoerceToHostString(value));
                return true;
            case "default":
                SetBooleanAttribute(element, "default", value);
                return true;
            default:
                return false;
        }
    }

    private static readonly string[] TrackKindKeywords = { "subtitles", "captions", "descriptions", "chapters", "metadata" };

    private bool TryGetMediaElementTextTrackProperty(Element element, string property, out JsValue value)
    {
        switch (property)
        {
            case "textTracks":
                MarkTextTrackModel(element);
                value = CallTextTrackHook("__fenMediaTextTracks", ToHostNodeOrNull(element));
                return value.Tag != JsValueTag.Undefined;
            case "audioTracks":
                value = CallTextTrackHook("__fenMediaAudioTracks", ToHostNodeOrNull(element));
                return value.Tag != JsValueTag.Undefined;
            case "videoTracks":
                value = CallTextTrackHook("__fenMediaVideoTracks", ToHostNodeOrNull(element));
                return value.Tag != JsValueTag.Undefined;
            case "addTextTrack":
                MarkTextTrackModel(element);
                value = GetOrCreateHostCallable(element, "addTextTrack", (_, args) =>
                {
                    var hookArgs = new JsValue[args.Count + 1];
                    hookArgs[0] = ToHostNodeOrNull(element);
                    for (var i = 0; i < args.Count; i++)
                    {
                        hookArgs[i + 1] = args[i];
                    }

                    var hook = ReadGlobalValueOrUndefined("__fenMediaAddTextTrack");
                    return _interpreter.CanCallValue(hook) ? _interpreter.InvokeFunction(hook, hookArgs, JsValue.Undefined) : JsValue.Undefined;
                }, length: 1);
                return true;
            default:
                value = JsValue.Undefined;
                return false;
        }
    }
}
