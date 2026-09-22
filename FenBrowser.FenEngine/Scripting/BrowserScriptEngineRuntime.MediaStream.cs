using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Js.Runtime;
using FenBrowser.Media.Element;
using FenBrowser.Media.Streams;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// <c>HTMLMediaElement.captureStream()</c> (Media Capture from DOM Elements): the stream
/// follows the element's tracks, so it gains one when the element grows one and loses it
/// when the element's resource is replaced or its playback ends. The
/// <c>MediaStream</c> and <c>MediaStreamTrack</c> objects a page sees live in the realm
/// (see the prelude); this owns the state behind them.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    /// <summary>One captured stream per element: captureStream() hands back the same one.</summary>
    private readonly Dictionary<Element, MediaStreamModel> _capturedStreams = new();

    private void InstallFenJsMediaStream()
    {
        // The stream an element is captured into, created on the first call. The prelude
        // builds the MediaStream object around the identifier this returns.
        Native("__fenCaptureStream", 1, args =>
        {
            if (ResolveHostObjectOrNull(args.Count > 0 ? args[0] : JsValue.Undefined) is not Element element ||
                !(IsVideoElement(element) || IsAudioElement(element)))
            {
                return JsValue.Null;
            }

            if (!_capturedStreams.TryGetValue(element, out var stream))
            {
                stream = new MediaStreamModel();
                _capturedStreams[element] = stream;
            }

            return JsValue.FromString(stream.Id);
        });

        // What the stream looks like now, after bringing it in line with the element: the
        // tracks it has, and the ones that just joined or left, so the prelude knows which
        // events are due. Reading it is what drives the capture forward.
        Native("__fenCaptureStreamPoll", 1, args =>
        {
            if (ResolveHostObjectOrNull(args.Count > 0 ? args[0] : JsValue.Undefined) is not Element element ||
                !_capturedStreams.TryGetValue(element, out var stream))
            {
                return JsValue.Null;
            }

            var controller = GetOrCreateMediaBinding(element).Controller;

            // A resource that has ended, or one the element no longer has, leaves the
            // stream with nothing: the capture is over, not paused.
            var tracks = controller.Ended || controller.ReadyState == MediaReadyState.HaveNothing
                ? []
                : controller.Tracks;

            // "Muted" for a captured track is the source not delivering right now, which
            // for an element is being paused or having nothing buffered to play.
            bool muted = controller.Paused || controller.ReadyState < MediaReadyState.HaveCurrentData;
            var change = stream.Follow(tracks, muted);

            return _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["id"] = JsValue.FromString(stream.Id),
                ["active"] = JsValue.FromBoolean(stream.Active),
                ["tracks"] = TrackList(stream.Tracks),
                ["added"] = TrackList(change.Added),
                ["removed"] = TrackList(change.Removed),
            });
        });

        // A page stopping one track by hand: it ends, and the stream may go inactive.
        Native("__fenCaptureStreamStopTrack", 2, args =>
        {
            if (ResolveHostObjectOrNull(args.Count > 0 ? args[0] : JsValue.Undefined) is not Element element ||
                !_capturedStreams.TryGetValue(element, out var stream))
            {
                return JsValue.FromBoolean(false);
            }

            string id = args.Count > 1 && args[1].Tag == JsValueTag.String ? CoerceToHostString(args[1]) : null;
            foreach (var track in stream.Tracks)
            {
                if (string.Equals(track.Id, id, StringComparison.Ordinal))
                    return JsValue.FromBoolean(track.End());
            }

            return JsValue.FromBoolean(false);
        });

        try
        {
            EvaluateWithFenJsRaw(MediaStreamPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] media stream prelude failed: {ex.Message}", LogCategory.JavaScript);
        }

        void Native(string name, int length, Func<IReadOnlyList<JsValue>, JsValue> body) =>
            _interpreter.RegisterGlobalValue(name, _interpreter.AllocateNativeFunction(name, (_, args) => body(args), length: length));
    }

    private JsValue TrackList(IReadOnlyList<MediaStreamTrackModel> tracks)
    {
        var values = new JsValue[tracks.Count];
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            values[i] = _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["id"] = JsValue.FromString(track.Id),
                ["kind"] = JsValue.FromString(track.Kind == MediaStreamTrackKind.Audio ? "audio" : "video"),
                ["label"] = JsValue.FromString(track.Label),
                ["live"] = JsValue.FromBoolean(track.Live),
                ["muted"] = JsValue.FromBoolean(track.Muted),
            });
        }

        return _interpreter.AllocateArray(values);
    }
}
