using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Js.Runtime;
using FenBrowser.Media.Element;
using FenBrowser.Media.Streams;
using FenBrowser.Media.WebAudio;

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

    /// <summary>The rate and layout of a captured audio track's pipe; a reader converts from it.</summary>
    private const float CapturedAudioRate = 48000f;
    private const int CapturedAudioChannels = 2;

    /// <summary>What carries an element's audio into its captured audio tracks.</summary>
    private sealed class CapturedAudio
    {
        public AudioCaptureWriter Writer { get; } = new(CapturedAudioRate, CapturedAudioChannels);

        /// <summary>Per live audio track: the key a MediaStreamAudioSourceNode finds its pipe by.</summary>
        public Dictionary<string, (string Key, AudioTrackPipe Pipe)> Pipes { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<Element, CapturedAudio> _capturedAudio = new();

    private void InstallFenJsMediaStream()
    {
        // HTML 4.8.11.2 srcObject setter: assign the provider object, then run the media
        // element load algorithm. The prelude keeps the object and passes the URL the
        // pipeline loads it from (null when the object was cleared).
        Native("__fenSetMediaProvider", 2, args =>
        {
            if (ResolveHostObjectOrNull(args.Count > 0 ? args[0] : JsValue.Undefined) is not Element element ||
                !(IsVideoElement(element) || IsAudioElement(element)))
            {
                return JsValue.Undefined;
            }

            var binding = GetOrCreateMediaBinding(element);
            binding.ProviderObjectUrl = args.Count > 1 && args[1].Tag == JsValueTag.String
                ? CoerceToHostString(args[1])
                : null;
            binding.Controller.Load();
            return JsValue.Undefined;
        });

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
            var audio = FollowCapturedAudio(element, controller, stream);

            return _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["id"] = JsValue.FromString(stream.Id),
                ["active"] = JsValue.FromBoolean(stream.Active),
                ["tracks"] = TrackList(stream.Tracks, audio),
                ["added"] = TrackList(change.Added, audio),
                ["removed"] = TrackList(change.Removed, audio),
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

    /// <summary>
    /// Gives every live captured audio track a pipe and points the element's audio capture
    /// at them; a track that ended, or left, loses its pipe, and with none left the element
    /// stops copying its audio.
    /// </summary>
    private CapturedAudio FollowCapturedAudio(Element element, HtmlMediaElementController controller, MediaStreamModel stream)
    {
        _capturedAudio.TryGetValue(element, out var audio);
        var live = new List<MediaStreamTrackModel>();
        foreach (var track in stream.Tracks)
        {
            if (track.Kind == MediaStreamTrackKind.Audio && track.Live)
                live.Add(track);
        }

        if (live.Count == 0 && audio == null)
        {
            return null;
        }

        if (audio == null)
        {
            audio = new CapturedAudio();
            _capturedAudio[element] = audio;
        }

        bool changed = false;
        foreach (var track in live)
        {
            if (audio.Pipes.ContainsKey(track.Id))
                continue;
            var pipe = new AudioTrackPipe(CapturedAudioRate, CapturedAudioChannels);
            string key = Guid.NewGuid().ToString("N");
            lock (_audioTrackPipes)
                _audioTrackPipes[key] = pipe;
            audio.Pipes[track.Id] = (key, pipe);
            changed = true;
        }

        foreach (var id in new List<string>(audio.Pipes.Keys))
        {
            if (live.Exists(t => string.Equals(t.Id, id, StringComparison.Ordinal)))
                continue;
            lock (_audioTrackPipes)
                _audioTrackPipes.Remove(audio.Pipes[id].Key);
            audio.Pipes.Remove(id);
            changed = true;
        }

        if (changed)
        {
            var pipes = new List<AudioTrackPipe>();
            foreach (var entry in audio.Pipes.Values)
                pipes.Add(entry.Pipe);
            audio.Writer.SetPipes(pipes);
            controller.SetAudioCapture(pipes.Count > 0 ? audio.Writer : null);
        }

        return audio;
    }

    private JsValue TrackList(IReadOnlyList<MediaStreamTrackModel> tracks, CapturedAudio audio)
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
                ["pipe"] = audio != null && audio.Pipes.TryGetValue(track.Id, out var captured)
                    ? JsValue.FromString(captured.Key)
                    : JsValue.Null,
            });
        }

        return _interpreter.AllocateArray(values);
    }
}
