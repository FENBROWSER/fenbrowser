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

    /// <summary>
    /// One canvas.captureStream() track (mediacapture-fromelement 3): the canvas it reads,
    /// the source its frames go to, and when it last took one.
    /// </summary>
    private sealed class CanvasCapture
    {
        public Element Canvas { get; init; }

        public VideoTrackSource Source { get; } = new();

        /// <summary>Frames a second: less than zero follows every change, zero only requestFrame().</summary>
        public double FrameRate { get; init; }

        /// <summary>
        /// The canvas's change count when it was last captured. It starts at the count when
        /// capture began: a frame is taken when the canvas is painted, not when capture starts.
        /// </summary>
        public long LastVersion { get; set; }

        public long LastFrameTicks { get; set; }

        public bool FrameRequested { get; set; }
    }

    private readonly Dictionary<string, CanvasCapture> _canvasCaptures = new(StringComparer.Ordinal);
    private System.Threading.Timer _canvasCaptureTimer;
    private int _canvasCaptureQueued;

    // The capture timer's tick: one pass on the realm's thread at a time.
    private void QueueCanvasCapture()
    {
        if (_realmAbandoned || System.Threading.Interlocked.Exchange(ref _canvasCaptureQueued, 1) == 1)
        {
            return;
        }

        Document document = null;
        lock (_canvasCaptures)
        {
            foreach (var capture in _canvasCaptures.Values)
            {
                document = capture.Canvas.OwnerDocument;
                break;
            }
        }

        if (document == null)
        {
            System.Threading.Volatile.Write(ref _canvasCaptureQueued, 0);
            return;
        }

        QueueMediaTask(document, () =>
        {
            System.Threading.Volatile.Write(ref _canvasCaptureQueued, 0);
            CaptureCanvases();
        });
    }

    /// <summary>
    /// mediacapture-fromelement 3: a frame is taken when the canvas was painted since the
    /// last one, no more often than the track's frame rate, or when requestFrame() asked.
    /// Runs on the realm's thread, where the bitmap is drawn.
    /// </summary>
    private void CaptureCanvases()
    {
        List<CanvasCapture> captures;
        lock (_canvasCaptures)
        {
            captures = new List<CanvasCapture>(_canvasCaptures.Values);
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var capture in captures)
        {
            if (!_canvasRenderingContexts.TryGetValue(capture.Canvas, out var context) || context.Bitmap is not { } bitmap ||
                bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                continue;
            }

            bool requested = capture.FrameRequested;
            bool changed = context.Version != capture.LastVersion;
            if (capture.FrameRate == 0 ? !requested : !(requested || changed))
            {
                continue;
            }

            if (capture.FrameRate > 0 && !requested &&
                System.Diagnostics.Stopwatch.GetElapsedTime(capture.LastFrameTicks, now).TotalMilliseconds < 1000.0 / capture.FrameRate)
            {
                continue;
            }

            var pixels = bitmap.GetPixelSpan();
            int stride = bitmap.RowBytes;
            if (bitmap.ColorType == SkiaSharp.SKColorType.Bgra8888)
            {
                capture.Source.Publish(pixels, bitmap.Width, bitmap.Height, stride);
            }
            else
            {
                // RGBA (the platform colour type off Windows): swap red and blue.
                var bgra = new byte[bitmap.Width * bitmap.Height * 4];
                for (int row = 0; row < bitmap.Height; row++)
                {
                    var source = pixels.Slice(row * stride, bitmap.Width * 4);
                    var target = bgra.AsSpan(row * bitmap.Width * 4, bitmap.Width * 4);
                    for (int i = 0; i < source.Length; i += 4)
                    {
                        target[i] = source[i + 2];
                        target[i + 1] = source[i + 1];
                        target[i + 2] = source[i];
                        target[i + 3] = source[i + 3];
                    }
                }

                capture.Source.Publish(bgra, bitmap.Width, bitmap.Height, bitmap.Width * 4);
            }

            capture.LastVersion = context.Version;
            capture.LastFrameTicks = now;
            capture.FrameRequested = false;
        }
    }

    private void InstallFenJsMediaStream()
    {
        // mediacapture-fromelement 3.1 captureStream(frameRate): the key the track carries.
        Native("__fenCanvasCaptureStream", 2, args =>
        {
            if (ResolveHostObjectOrNull(args.Count > 0 ? args[0] : JsValue.Undefined) is not Element canvas || !IsCanvasElement(canvas))
            {
                return JsValue.Null;
            }

            double rate = args.Count > 1 && args[1].Tag is JsValueTag.Number or JsValueTag.Int32
                ? (args[1].Tag == JsValueTag.Int32 ? args[1].AsInt32() : args[1].AsNumber())
                : -1;
            var key = Guid.NewGuid().ToString("N");
            lock (_canvasCaptures)
            {
                _canvasCaptures[key] = new CanvasCapture
                {
                    Canvas = canvas,
                    FrameRate = rate,
                    LastVersion = _canvasRenderingContexts.TryGetValue(canvas, out var context) ? context.Version : 0,
                };
                _canvasCaptureTimer ??= new System.Threading.Timer(_ => QueueCanvasCapture(), null, 16, 16);
            }

            return JsValue.FromString(key);
        });

        // 3.2 requestFrame(): the next capture takes a frame even if nothing changed.
        Native("__fenCanvasRequestFrame", 1, args =>
        {
            var key = args.Count > 0 && args[0].Tag == JsValueTag.String ? CoerceToHostString(args[0]) : null;
            lock (_canvasCaptures)
            {
                if (key != null && _canvasCaptures.TryGetValue(key, out var capture))
                    capture.FrameRequested = true;
            }

            return JsValue.Undefined;
        });

        // The track stopped: no more frames.
        Native("__fenCanvasCaptureStop", 1, args =>
        {
            var key = args.Count > 0 && args[0].Tag == JsValueTag.String ? CoerceToHostString(args[0]) : null;
            lock (_canvasCaptures)
            {
                if (key != null)
                    _canvasCaptures.Remove(key);
                if (_canvasCaptures.Count == 0)
                {
                    _canvasCaptureTimer?.Dispose();
                    _canvasCaptureTimer = null;
                }
            }

            return JsValue.Undefined;
        });

        // A MediaStream some element plays: its tracks, as that element's resource reads
        // them. (url, [{ id, kind, pipe, live, enabled }]).
        Native("__fenSyncMediaStream", 2, args =>
        {
            var url = args.Count > 0 && args[0].Tag == JsValueTag.String ? CoerceToHostString(args[0]) : null;
            if (string.IsNullOrEmpty(url) || args.Count < 2 || args[1].Tag != JsValueTag.Object)
            {
                return JsValue.Undefined;
            }

            var tracks = new List<LiveTrack>();
            _interpreter.TryGetObjectProperty(args[1], "length", out var lengthValue);
            int length = lengthValue.Tag == JsValueTag.Int32 ? lengthValue.AsInt32() : (int)(lengthValue.Tag == JsValueTag.Number ? lengthValue.AsNumber() : 0);
            for (int i = 0; i < length; i++)
            {
                if (!_interpreter.TryGetObjectProperty(args[1], i.ToString(System.Globalization.CultureInfo.InvariantCulture), out var entry) ||
                    entry.Tag != JsValueTag.Object)
                {
                    continue;
                }

                string Read(string name) =>
                    _interpreter.TryGetObjectProperty(entry, name, out var v) && v.Tag == JsValueTag.String ? CoerceToHostString(v) : string.Empty;
                bool Flag(string name) =>
                    _interpreter.TryGetObjectProperty(entry, name, out var v) && v.Tag == JsValueTag.Boolean && v.AsBoolean();

                var pipeKey = Read("pipe");
                AudioTrackPipe pipe = null;
                if (pipeKey.Length > 0)
                {
                    lock (_audioTrackPipes)
                        _audioTrackPipes.TryGetValue(pipeKey, out pipe);
                }

                VideoTrackSource video = null;
                var videoKey = Read("video");
                if (videoKey.Length > 0)
                {
                    lock (_canvasCaptures)
                    {
                        if (_canvasCaptures.TryGetValue(videoKey, out var capture))
                            video = capture.Source;
                    }
                }

                tracks.Add(new LiveTrack(
                    Read("id"),
                    string.Equals(Read("kind"), "video", StringComparison.Ordinal) ? FenBrowser.Media.MediaTrackKind.Video : FenBrowser.Media.MediaTrackKind.Audio,
                    pipe,
                    Flag("live"),
                    Flag("enabled"),
                    video));
            }

            LiveStreamRegistry.GetOrAdd(url).Update(tracks);
            return JsValue.Undefined;
        });

        // No element plays the stream any more.
        Native("__fenForgetMediaStream", 1, args =>
        {
            if (args.Count > 0 && args[0].Tag == JsValueTag.String)
            {
                LiveStreamRegistry.Remove(CoerceToHostString(args[0]));
            }

            return JsValue.Undefined;
        });

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
