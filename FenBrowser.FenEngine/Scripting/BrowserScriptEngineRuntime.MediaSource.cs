using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Media;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Media;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
using FenBrowser.Media.Mse;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Media Source Extensions (https://w3c.github.io/media-source/): the <c>MediaSource</c>,
/// <c>SourceBuffer</c> and <c>SourceBufferList</c> objects live in the realm as script
/// (<see cref="MediaSourcePrelude"/>) and keep the DOM-visible state - <c>updating</c>,
/// the lists, the events. The algorithms that touch bytes run natively in
/// <see cref="MediaSourceModel"/> and <see cref="SourceBufferModel"/>, reached through the
/// <c>__fenMse*</c> natives below. A media element attaches to a MediaSource through a
/// blob URL: <see cref="TryStartMediaSourceResource"/> resolves the URL against the
/// realm's blob URL store and builds an <see cref="MseResource"/>.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private sealed class MediaSourceEntry
    {
        public MediaSourceModel Model;
        public MediaPipelineContext Context;
        public readonly Dictionary<int, SourceBufferModel> Buffers = new();
        public int NextBufferId = 1;
        public MseResource Resource;
        public Element Element;
    }

    private readonly Dictionary<int, MediaSourceEntry> _mediaSources = new();
    private int _nextMediaSourceId = 1;

    private void InstallFenJsMediaSource()
    {
        Native("__fenMseCreate", 0, _ => CreateMediaSource());
        Native("__fenMseReadyState", 1, args => Mse(() => JsValue.FromString(MseEntry(args).Model.ReadyState switch
        {
            MediaSourceReadyState.Open => "open",
            MediaSourceReadyState.Ended => "ended",
            _ => "closed",
        })));
        Native("__fenMseDuration", 1, args => Mse(() => JsValue.FromNumber(MseEntry(args).Model.DurationSeconds)));
        Native("__fenMseSetDuration", 2, args => Mse(() =>
        {
            var entry = MseEntry(args);
            double seconds = ToDouble(args, 1);
            entry.Model.SetDuration(ToMediaTime(args, 1), seconds);
            // §2.4.6 step 5: the media element's duration follows at once, before the
            // player's own report of the change lands as a task.
            if (entry.Element != null && entry.Model.Duration is { } duration)
            {
                GetOrCreateMediaBinding(entry.Element).Controller.ApplyDurationChange(duration, entry.Model.ExactDurationSeconds);
            }
        }));
        Native("__fenMseAddSourceBuffer", 2, args => Mse(() =>
        {
            var entry = MseEntry(args);
            var type = args.Count > 1 ? CoerceToHostString(args[1]) ?? string.Empty : string.Empty;
            var buffer = entry.Model.AddSourceBuffer(type, generateTimestamps: MpegAudioSegmentParser.GeneratesTimestamps(type));
            int id = entry.NextBufferId++;
            entry.Buffers[id] = buffer;
            return JsValue.FromInt32(id);
        }));
        // MSE byte stream format registry: the MPEG audio streams carry no timestamps, so the
        // SourceBuffer generates them (§3.1 "generate timestamps flag").
        Native("__fenMseTypeGeneratesTimestamps", 1, args =>
            JsValue.FromBoolean(args.Count > 0 && MpegAudioSegmentParser.GeneratesTimestamps(CoerceToHostString(args[0]) ?? string.Empty)));
        Native("__fenMseRemoveSourceBuffer", 2, args => Mse(() =>
        {
            var entry = MseEntry(args);
            var buffer = MseBuffer(entry, args, 1);
            entry.Model.RemoveSourceBuffer(buffer);
            entry.Buffers.Remove(ToInt(args, 1));
        }));
        Native("__fenMseEndOfStream", 2, args => Mse(() =>
        {
            var entry = MseEntry(args);
            var error = args.Count > 1 ? CoerceToHostString(args[1]) : null;
            var kind = error switch
            {
                "network" => EndOfStreamError.Network,
                "decode" => EndOfStreamError.Decode,
                _ => EndOfStreamError.None,
            };
            entry.Model.EndOfStream(kind);
            // §2.4.7 step 3: the duration change runs now, so the element's duration and
            // a playback position beyond it move before script goes on.
            if (entry.Element != null && entry.Model.Duration is { } duration)
                GetOrCreateMediaBinding(entry.Element).Controller.ApplyDurationChange(duration, entry.Model.ExactDurationSeconds);
            entry.Resource?.ReportEndOfStreamError(kind);
        }));
        Native("__fenMseSetLiveSeekableRange", 3, args => Mse(() => MseEntry(args).Model.SetLiveSeekableRange(ToMediaTime(args, 1), ToMediaTime(args, 2))));
        Native("__fenMseClearLiveSeekableRange", 1, args => Mse(() => MseEntry(args).Model.ClearLiveSeekableRange()));
        Native("__fenMseIsTypeSupported", 2, args =>
        {
            var type = args.Count > 0 && args[0].Tag != JsValueTag.Undefined && args[0].Tag != JsValueTag.Null ? CoerceToHostString(args[0]) : null;
            bool relaxed = args.Count > 1 && args[1].Tag == JsValueTag.Boolean && args[1].AsBoolean();
            return JsValue.FromBoolean(type != null && MediaEngineServices.TypeSupport.IsMediaSourceTypeSupported(type, relaxed));
        });
        Native("__fenMseReopen", 1, args => Mse(() => JsValue.FromBoolean(MseEntry(args).Model.Reopen())));
        Native("__fenMseRelease", 1, args =>
        {
            int id = ToInt(args, 0);
            if (_mediaSources.Remove(id, out var entry))
            {
                entry.Resource?.Dispose();
                entry.Model.Detach();
            }

            return JsValue.Undefined;
        });

        Native("__fenMseSbEvictToFit", 4, args => Mse(() =>
        {
            var entry = MseEntry(args);
            var buffer = MseBuffer(entry, args, 1);
            long bytes = (long)Math.Max(0, ToDouble(args, 2));
            var currentTime = ToMediaTime(args, 3);
            lock (entry.Model.Gate)
            {
                return JsValue.FromBoolean(buffer.EvictToFit(bytes, currentTime));
            }
        }));
        Native("__fenMseSbAppend", 3, args => Mse(() =>
        {
            var entry = MseEntry(args);
            var buffer = MseBuffer(entry, args, 1);
            var bytes = args.Count > 2 ? ExtractBytesFromArrayLike(args[2]) : Array.Empty<byte>();
            // The track buffers change under the model gate: the media task reads them
            // under it (the decode source's cursor, its frame copies).
            bool hadTracks;
            AppendOutcome outcome;
            bool parsing;
            JsValue tracks;
            lock (entry.Model.Gate)
            {
                hadTracks = buffer.HasTracks;
                outcome = buffer.Append(bytes);
                parsing = buffer.ParsingMediaSegment;
                tracks = DescribeTracks(buffer);
                entry.Model.NotifyChanged();
                if (outcome == AppendOutcome.Ok && entry.Element != null)
                {
                    ApplyAppendToElement(entry, buffer, first: !hadTracks && buffer.HasTracks);
                }
            }

            var result = new Dictionary<string, JsValue>
            {
                ["status"] = JsValue.FromString(outcome == AppendOutcome.Ok ? "ok" : "error"),
                ["initialization"] = JsValue.FromBoolean(!hadTracks && buffer.HasTracks),
                ["parsing"] = JsValue.FromBoolean(parsing),
                ["tracks"] = tracks,
            };
            return _interpreter.AllocateObject(result);
        }));
        Native("__fenMseSbBuffered", 2, args => Mse(() =>
        {
            var entry = MseEntry(args);
            var buffer = MseBuffer(entry, args, 1);
            MediaTimeRanges ranges;
            lock (entry.Model.Gate)
            {
                ranges = buffer.Buffered;
            }

            return CreateTimeRangesValue(ranges);
        }));
        Native("__fenMseSbSetMode", 3, args => Mse(() =>
        {
            var entry = MseEntry(args);
            var buffer = MseBuffer(entry, args, 1);
            lock (entry.Model.Gate)
            {
                buffer.SetMode(CoerceToHostString(args[2]) == "sequence" ? AppendMode.Sequence : AppendMode.Segments);
            }
        }));
        Native("__fenMseSbSetTimestampOffset", 3, args => Mse(() =>
        {
            var entry = MseEntry(args);
            lock (entry.Model.Gate)
            {
                MseBuffer(entry, args, 1).SetTimestampOffset(ToMediaTime(args, 2));
            }
        }));
        Native("__fenMseSbTimestampOffset", 2, args => Mse(() => JsValue.FromNumber(MseBuffer(MseEntry(args), args, 1).TimestampOffset.TotalSeconds)));
        Native("__fenMseSbSetAppendWindow", 4, args => Mse(() =>
        {
            var entry = MseEntry(args);
            var buffer = MseBuffer(entry, args, 1);
            lock (entry.Model.Gate)
            {
                buffer.AppendWindowStart = ToMediaTime(args, 2);
                buffer.AppendWindowEnd = ToMediaTime(args, 3);
            }
        }));
        Native("__fenMseSbRemove", 4, args => Mse(() =>
        {
            var entry = MseEntry(args);
            var buffer = MseBuffer(entry, args, 1);
            lock (entry.Model.Gate)
            {
                buffer.Remove(ToMediaTime(args, 2), ToMediaTime(args, 3));
                entry.Model.NotifyChanged();
            }
        }));
        Native("__fenMseSbResetParser", 2, args => Mse(() =>
        {
            var entry = MseEntry(args);
            lock (entry.Model.Gate)
            {
                MseBuffer(entry, args, 1).ResetParserState();
            }
        }));
        Native("__fenMseSbChangeType", 3, args => Mse(() =>
        {
            var entry = MseEntry(args);
            var buffer = MseBuffer(entry, args, 1);
            var type = CoerceToHostString(args[2]) ?? string.Empty;
            var parser = entry.Model.CreateParser(type)
                ?? throw new MseInvalidOperationException("NotSupportedError", $"'{type}' is not a supported byte stream format.");
            lock (entry.Model.Gate)
            {
                buffer.ChangeType(type, parser, generateTimestamps: MpegAudioSegmentParser.GeneratesTimestamps(type));
            }
        }));
        Native("__fenMseSbBytes", 2, args => Mse(() => JsValue.FromNumber(MseBuffer(MseEntry(args), args, 1).Bytes)));

        try
        {
            EvaluateWithFenJsRaw(MediaSourcePrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] media source prelude failed: {ex.Message}", LogCategory.JavaScript);
        }

        void Native(string name, int length, Func<IReadOnlyList<JsValue>, JsValue> body) =>
            _interpreter.RegisterGlobalValue(name, _interpreter.AllocateNativeFunction(name, (_, args) => body(args), length: length));
    }

    private JsValue CreateMediaSource()
    {
        var context = new MediaPipelineContext(PlayerId.Next(), MediaLimits.Default, MediaEngineServices.Log);
        int id = _nextMediaSourceId++;
        _mediaSources[id] = new MediaSourceEntry { Model = new MediaSourceModel(context), Context = context };
        return JsValue.FromInt32(id);
    }

    private MediaSourceEntry MseEntry(IReadOnlyList<JsValue> args)
    {
        if (_mediaSources.TryGetValue(ToInt(args, 0), out var entry))
        {
            return entry;
        }

        throw new MseInvalidOperationException("InvalidStateError", "The MediaSource is gone.");
    }

    private static SourceBufferModel MseBuffer(MediaSourceEntry entry, IReadOnlyList<JsValue> args, int index)
    {
        if (entry.Buffers.TryGetValue(ToInt(args, index), out var buffer))
        {
            return buffer;
        }

        throw new MseInvalidOperationException("InvalidStateError", "The SourceBuffer has been removed from its MediaSource.");
    }

    /// <summary>Runs an MSE algorithm, turning its DOMException request into a thrown DOMException in the realm.</summary>
    private JsValue Mse(Func<JsValue> body)
    {
        try
        {
            return body();
        }
        catch (MseInvalidOperationException ex)
        {
            ThrowDomException(ex.DomExceptionName, ex.Message);
            return JsValue.Undefined;
        }
    }

    private JsValue Mse(Action body) => Mse(() =>
    {
        body();
        return JsValue.Undefined;
    });

    private static bool IsNumber(JsValue value) => value.Tag is JsValueTag.Number or JsValueTag.Int32;

    private static int ToInt(IReadOnlyList<JsValue> args, int index)
    {
        if (index >= args.Count || !IsNumber(args[index]))
        {
            return -1;
        }

        double value = args[index].AsNumber();
        return double.IsFinite(value) ? (int)value : -1;
    }

    private static double ToDouble(IReadOnlyList<JsValue> args, int index) =>
        index < args.Count && IsNumber(args[index]) ? args[index].AsNumber() : double.NaN;

    private static MediaTime ToMediaTime(IReadOnlyList<JsValue> args, int index)
    {
        double seconds = ToDouble(args, index);
        if (double.IsNaN(seconds))
        {
            return MediaTime.Zero;
        }

        if (double.IsPositiveInfinity(seconds))
        {
            return MediaTime.PositiveInfinity;
        }

        if (double.IsNegativeInfinity(seconds))
        {
            return MediaTime.NegativeInfinity;
        }

        return MediaTime.FromSeconds(seconds);
    }

    /// <summary>
    /// What an append changes on the element before its update events (MSE §3.5.8 steps 6-8,
    /// §3.5.11 steps 22-24): metadata from the first initialization segment, then the
    /// readiness the buffered ranges now justify.
    /// </summary>
    private void ApplyAppendToElement(MediaSourceEntry entry, SourceBufferModel buffer, bool first)
    {
        var controller = GetOrCreateMediaBinding(entry.Element).Controller;
        MediaTimeRanges buffered;
        bool ended;
        MediaTime duration;
        List<MediaTrackInfo> tracks = new();
        int width = 0, height = 0;
        lock (entry.Model.Gate)
        {
            buffered = entry.Model.Buffered;
            ended = entry.Model.ReadyState == MediaSourceReadyState.Ended;
            duration = entry.Model.Duration ?? MediaTime.PositiveInfinity;
            if (first && entry.Model.SourceBuffers.All(b => b.HasTracks))
            {
                foreach (var source in entry.Model.SourceBuffers)
                {
                    foreach (var track in source.TrackBuffers)
                    {
                        if (track.Info is { } info)
                        {
                            tracks.Add(info);
                            if (info.Kind == MediaTrackKind.Video && width == 0)
                            {
                                width = info.Config.Width;
                                height = info.Config.Height;
                            }
                        }
                    }
                }
            }
        }

        if (tracks.Count > 0)
        {
            controller.ApplyMediaSourceMetadata(new MediaResourceMetadata(duration, width, height, tracks));
        }

        controller.ApplyMediaSourceReadiness(buffered, ended, entry.Model.NeedsMissingKey);
    }

    private JsValue DescribeTracks(SourceBufferModel buffer)
    {
        var tracks = new List<JsValue>();
        foreach (var track in buffer.TrackBuffers)
        {
            tracks.Add(_interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["id"] = JsValue.FromString(track.TrackId.ToString(CultureInfo.InvariantCulture)),
                ["kind"] = JsValue.FromString(track.Kind == MediaTrackKind.Audio ? "audio" : "video"),
                ["language"] = JsValue.FromString(track.Info?.Language ?? ""),
                ["label"] = JsValue.FromString(track.Info?.Label ?? ""),
            }));
        }

        return _interpreter.AllocateArray(tracks);
    }

    /// <summary>
    /// The media element's resource fetch algorithm for a <c>blob:</c> URL whose entry is a
    /// MediaSource (HTML §4.8.11.5 "media provider object", MSE §2.4.2). Returns false when
    /// the URL is not such a blob URL; null when it is but the MediaSource is already
    /// attached (§2.4.2 step 1: the fetch fails as if the media data cannot be fetched).
    /// </summary>
    private bool TryStartMediaSourceResource(Element element, MediaFetchRequest request, IMediaResourceClient client, Action<Action> postToElementThread, (string Url, int Id)? pinned, out IMediaResource resource)
    {
        resource = null;
        if (request.Url == null || !request.Url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var resolved = pinned is { } p && string.Equals(p.Url, request.Url, StringComparison.Ordinal) ? pinned : ResolveMediaSourceBlobUrl(request.Url);
        if (resolved is not { } target || !_mediaSources.TryGetValue(target.Id, out var entry))
        {
            return false;
        }

        int id = target.Id;
        try
        {
            entry.Model.Attach();
        }
        catch (MseInvalidOperationException)
        {
            // Already attached elsewhere: the element fails its load.
            return true;
        }

        var presenter = new FenBrowser.Media.Video.VideoPresenter();
        Action rangesChanged = null;
        var mse = new MseResource(entry.Model, client, postToElementThread, entry.Context, presenter, onDetached: () =>
        {
            if (entry.Resource != null)
            {
                entry.Model.Changed -= rangesChanged;
                entry.Resource = null;
                entry.Element = null;
                // §2.4.3 detaching runs now, on the element's thread inside the load
                // algorithm: the SourceBuffers go and their removetrack tasks are queued
                // ahead of whatever the load algorithm queues next (an error for an empty
                // src); sourceclose itself is queued.
                _ = CallTextTrackHook("__fenMseDispatch", JsValue.FromInt32(id), JsValue.FromString("sourceclose"), JsValue.Null);
            }
        });
        entry.Resource = mse;
        entry.Element = element;
        // Script-made changes (under the gate, on this thread) reach the element's
        // buffered and seekable attributes before the player's report lands as a task.
        rangesChanged = () =>
        {
            if (entry.Element is { } current && ReferenceEquals(current, element) && entry.Resource == mse)
            {
                GetOrCreateMediaBinding(current).Controller.ApplyMediaSourceRanges(entry.Model.Buffered, entry.Model.Seekable);
            }
        };
        entry.Model.Changed += rangesChanged;
        mse.Start();
        QueueMediaTask(element.OwnerDocument, () => _ = CallTextTrackHook("__fenMseDispatch", JsValue.FromInt32(id), JsValue.FromString("sourceopen"), ToHostNodeOrNull(element)));
        resource = mse;
        return true;
    }

    /// <summary>The MediaSource behind a blob URL in this realm's blob URL store, or null (File API §8.3 "resolve a blob URL").</summary>
    private (string Url, int Id)? ResolveMediaSourceBlobUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var found = CallTextTrackHook("__fenMseFromBlobUrl", JsValue.FromString(url));
        if (!IsNumber(found) || found.AsNumber() < 0)
        {
            return null;
        }

        return (url, (int)found.AsNumber());
    }

    private const string MediaSourcePrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.EventTarget !== 'function' || typeof g.MediaSource === 'function') return;

            function defineInterface(name, ctor, base) {
                ctor.prototype = Object.create((base || g.EventTarget).prototype);
                Object.defineProperty(ctor.prototype, 'constructor', { value: ctor, writable: true, configurable: true });
                Object.defineProperty(ctor.prototype, Symbol.toStringTag, { value: name, configurable: true });
                Object.defineProperty(ctor, 'name', { value: name, configurable: true });
                g[name] = ctor;
                return ctor;
            }
            function accessor(proto, name, get, set) {
                Object.defineProperty(proto, name, { get: get, set: set, enumerable: true, configurable: true });
            }
            function method(proto, name, fn, length) {
                if (length !== undefined) Object.defineProperty(fn, 'length', { value: length, configurable: true });
                Object.defineProperty(proto, name, { value: fn, writable: true, enumerable: true, configurable: true });
            }
            function handlerAttribute(proto, name) {
                var key = '_fenHandler_' + name;
                var type = name.slice(2);
                accessor(proto, name,
                    function () { return this[key] || null; },
                    function (v) {
                        this[key] = (typeof v === 'function' || (v !== null && typeof v === 'object')) ? v : null;
                        if (!this[key + '_installed'] && typeof this.addEventListener === 'function') {
                            this[key + '_installed'] = true;
                            var target = this;
                            this.addEventListener(type, function (ev) {
                                var h = target[key];
                                if (typeof h === 'function') h.call(target, ev);
                                else if (h && typeof h.handleEvent === 'function') h.handleEvent(ev);
                            });
                        }
                    });
            }
            function domException(message, name) { return new g.DOMException(message, name); }
            // Every SourceBuffer and MediaSource event is "queue a task to fire", so they
            // land in the order the algorithms queued them (updatestart before an abort).
            function queueFire(source, target, type) { queueTask(source, function () { fire(target, type); }); }
            function fire(target, type) {
                if (!target) return;
                var ev = new g.Event(type);
                var previousEvent = g.event;
                try { g.event = ev; } catch (e) {}
                try { target.dispatchEvent(ev); } catch (e) {}
                try { g.event = previousEvent; } catch (e) {}
            }
            function queueTask(source, fn) {
                var element = source && source._element;
                if (typeof g.__fenQueueMediaTask === 'function' && element) g.__fenQueueMediaTask(element, fn);
                else g.setTimeout(fn, 0);
            }
            function toDouble(v, what) {
                var n = Number(v);
                if (isNaN(n)) throw new TypeError(what + ' is not a number.');
                return n;
            }
            function toDomString(v) { return v === undefined ? '' : String(v); }
            function isBufferSource(v) {
                return typeof ArrayBuffer !== 'undefined' &&
                    (v instanceof ArrayBuffer || (typeof ArrayBuffer.isView === 'function' && ArrayBuffer.isView(v)));
            }
            function bufferBytes(v) {
                // A detached buffer (transferred away) has no bytes: the append is empty
                // (WebIDL BufferSource of a detached buffer reads as length 0).
                try {
                    if (v instanceof ArrayBuffer) return v.byteLength === 0 ? new Uint8Array(0) : new Uint8Array(v.slice(0));
                    if (v.byteLength === 0) return new Uint8Array(0);
                    return new Uint8Array(v.buffer.slice(v.byteOffset, v.byteOffset + v.byteLength));
                } catch (e) {
                    return new Uint8Array(0);
                }
            }

            var sources = new Map();

            // ---- SourceBufferList (§4) ----------------------------------------------------

            function SourceBufferList() { throw new TypeError('Illegal constructor'); }
            defineInterface('SourceBufferList', SourceBufferList);
            accessor(SourceBufferList.prototype, 'length', function () { return this._buffers.length; });
            handlerAttribute(SourceBufferList.prototype, 'onaddsourcebuffer');
            handlerAttribute(SourceBufferList.prototype, 'onremovesourcebuffer');
            if (typeof Symbol === 'function' && Symbol.iterator) {
                method(SourceBufferList.prototype, Symbol.iterator, function () { return this._buffers.slice()[Symbol.iterator](); });
            }
            function newList() {
                var list = Object.create(SourceBufferList.prototype);
                g.EventTarget.call(list);
                list._buffers = [];
                list._indexed = 0;
                return list;
            }
            function syncIndexed(list) {
                var buffers = list._buffers;
                for (var i = 0; i < buffers.length; i++) {
                    Object.defineProperty(list, i, { value: buffers[i], writable: false, enumerable: true, configurable: true });
                }
                for (var j = buffers.length; j < list._indexed; j++) delete list[j];
                list._indexed = buffers.length;
            }
            function listAdd(list, source, buffer) {
                list._buffers.push(buffer);
                syncIndexed(list);
                queueTask(source, function () { fire(list, 'addsourcebuffer'); });
            }
            // §3.1 activeSourceBuffers keeps the order of sourceBuffers, whichever buffer
            // became active first.
            function activeAdd(source, buffer) {
                var list = source._activeSourceBuffers;
                if (list._buffers.indexOf(buffer) >= 0) return;
                var order = source._sourceBuffers._buffers;
                var at = 0;
                while (at < list._buffers.length && order.indexOf(list._buffers[at]) < order.indexOf(buffer)) at++;
                list._buffers.splice(at, 0, buffer);
                syncIndexed(list);
                queueTask(source, function () { fire(list, 'addsourcebuffer'); });
            }
            function isActive(sb) {
                var a = sb._audioTracks._tracks, v = sb._videoTracks._tracks;
                for (var i = 0; i < a.length; i++) if (a[i]._enabled) return true;
                for (var j = 0; j < v.length; j++) if (v[j]._selected) return true;
                return false;
            }
            // §3.5.8 "changes to selected/enabled track state": the SourceBuffer joins or
            // leaves activeSourceBuffers with its tracks.
            g.__fenMseTrackStateChanged = function (sb) {
                if (!sb || sb._removed || !sb._source) return;
                var source = sb._source;
                if (isActive(sb)) activeAdd(source, sb);
                else listRemove(source._activeSourceBuffers, source, sb);
            };
            function listRemove(list, source, buffer) {
                var at = list._buffers.indexOf(buffer);
                if (at < 0) return false;
                list._buffers.splice(at, 1);
                syncIndexed(list);
                queueTask(source, function () { fire(list, 'removesourcebuffer'); });
                return true;
            }

            // ---- SourceBuffer (§3) --------------------------------------------------------

            function SourceBuffer() { throw new TypeError('Illegal constructor'); }
            defineInterface('SourceBuffer', SourceBuffer);
            ['onupdatestart', 'onupdate', 'onupdateend', 'onerror', 'onabort'].forEach(function (n) { handlerAttribute(SourceBuffer.prototype, n); });

            function sbCheckAttached(sb) {
                if (!sb._source || sb._removed) throw domException('The SourceBuffer has been removed from its MediaSource.', 'InvalidStateError');
            }
            function sbCheckNotUpdating(sb) {
                if (sb._updating) throw domException('The SourceBuffer is updating.', 'InvalidStateError');
            }
            function sourceEnsureOpen(source) {
                // §3.5.4 prepare append step 3 / §3.1 setters: an ended source reopens.
                if (source._readyState() === 'ended' && g.__fenMseReopen(source._id)) {
                    queueTask(source, function () { fire(source, 'sourceopen'); });
                }
            }

            accessor(SourceBuffer.prototype, 'mode',
                function () { return this._mode; },
                function (v) {
                    v = toDomString(v);
                    if (v !== 'segments' && v !== 'sequence') return; // WebIDL: an enum attribute ignores a value outside the enum
                    sbCheckAttached(this);
                    sbCheckNotUpdating(this);
                    if (this._generateTimestamps && v === 'segments') throw new TypeError('This SourceBuffer generates timestamps; its mode cannot be segments.');
                    if (this._source._readyState() === 'closed') throw domException('The MediaSource is closed.', 'InvalidStateError');
                    sourceEnsureOpen(this._source);
                    if (this._appendState === 'parsing') throw domException('A media segment is being parsed.', 'InvalidStateError');
                    g.__fenMseSbSetMode(this._source._id, this._bid, v);
                    this._mode = v;
                });
            accessor(SourceBuffer.prototype, 'updating', function () { return this._updating; });
            // §3.1 buffered: the same TimeRanges object as long as the ranges are the same.
            accessor(SourceBuffer.prototype, 'buffered', function () {
                sbCheckAttached(this);
                var ranges = g.__fenMseSbBuffered(this._source._id, this._bid);
                var cached = this._bufferedCache;
                if (cached && sameRanges(cached, ranges)) return cached;
                this._bufferedCache = ranges;
                return ranges;
            });
            function sameRanges(a, b) {
                if (a.length !== b.length) return false;
                for (var i = 0; i < a.length; i++) if (a.start(i) !== b.start(i) || a.end(i) !== b.end(i)) return false;
                return true;
            }
            accessor(SourceBuffer.prototype, 'timestampOffset',
                function () { return this._timestampOffset; },
                function (v) {
                    v = toDouble(v, 'timestampOffset');
                    if (!isFinite(v)) throw new TypeError('timestampOffset must be finite.'); // WebIDL double
                    sbCheckAttached(this);
                    sbCheckNotUpdating(this);
                    if (this._source._readyState() === 'closed') throw domException('The MediaSource is closed.', 'InvalidStateError');
                    sourceEnsureOpen(this._source);
                    if (this._appendState === 'parsing') throw domException('A media segment is being parsed.', 'InvalidStateError');
                    g.__fenMseSbSetTimestampOffset(this._source._id, this._bid, v);
                    this._timestampOffset = v;
                });
            accessor(SourceBuffer.prototype, 'appendWindowStart',
                function () { return this._appendWindowStart; },
                function (v) {
                    v = toDouble(v, 'appendWindowStart');
                    sbCheckAttached(this);
                    sbCheckNotUpdating(this);
                    if (v < 0 || v >= this._appendWindowEnd) throw new TypeError('appendWindowStart must lie in [0, appendWindowEnd).');
                    this._appendWindowStart = v;
                    g.__fenMseSbSetAppendWindow(this._source._id, this._bid, this._appendWindowStart, this._appendWindowEnd);
                });
            accessor(SourceBuffer.prototype, 'appendWindowEnd',
                function () { return this._appendWindowEnd; },
                function (v) {
                    v = Number(v);
                    sbCheckAttached(this);
                    sbCheckNotUpdating(this);
                    if (isNaN(v)) throw new TypeError('appendWindowEnd must be a number.');
                    if (v <= this._appendWindowStart) throw new TypeError('appendWindowEnd must be greater than appendWindowStart.');
                    this._appendWindowEnd = v;
                    g.__fenMseSbSetAppendWindow(this._source._id, this._bid, this._appendWindowStart, this._appendWindowEnd);
                });
            accessor(SourceBuffer.prototype, 'audioTracks', function () { return this._audioTracks; });
            accessor(SourceBuffer.prototype, 'videoTracks', function () { return this._videoTracks; });
            accessor(SourceBuffer.prototype, 'textTracks', function () { return this._textTracks; });

            method(SourceBuffer.prototype, 'appendBuffer', function (data) {
                if (!isBufferSource(data)) throw new TypeError("Failed to execute 'appendBuffer' on 'SourceBuffer': parameter 1 is not of type 'BufferSource'.");
                var sb = this;
                // §3.5.4 prepare append.
                sbCheckAttached(sb);
                sbCheckNotUpdating(sb);
                var source = sb._source;
                if (source._element && source._element.error) throw domException('The media element is in an error state.', 'InvalidStateError');
                sourceEnsureOpen(source);
                var bytes = bufferBytes(data);
                var currentTime = source._element ? Number(source._element.currentTime) || 0 : 0;
                if (!g.__fenMseSbEvictToFit(source._id, sb._bid, bytes.byteLength, currentTime)) {
                    throw typeof g.QuotaExceededError === 'function'
                        ? new g.QuotaExceededError('The SourceBuffer is full, and cannot free space to append additional buffers.')
                        : domException('The SourceBuffer is full, and cannot free space to append additional buffers.', 'QuotaExceededError');
                }
                // §3.2 appendBuffer steps 2-5.
                sb._updating = true;
                sb._pendingBytes = bytes;
                queueTask(source, function () { fire(sb, 'updatestart'); });
                queueTask(source, function () { bufferAppend(sb); });
            }, 1);

            // §3.5.4 buffer append (the asynchronous part).
            function bufferAppend(sb) {
                var bytes = sb._pendingBytes;
                sb._pendingBytes = null;
                if (!sb._updating || !bytes || sb._removed) return;
                var source = sb._source;
                var result;
                try {
                    result = g.__fenMseSbAppend(source._id, sb._bid, bytes);
                } catch (e) {
                    result = { status: 'error', initialization: false, tracks: [] };
                }
                if (result.initialization) initializationSegmentReceived(sb, result.tracks);
                if (result.status !== 'ok') {
                    // §3.5.3 append error.
                    resetParserState(sb);
                    sb._updating = false;
                    queueFire(source, sb, 'error');
                    queueFire(source, sb, 'updateend');
                    endOfStream(source, 'decode', true);
                    return;
                }
                sb._appendState = result.parsing ? 'parsing' : 'waiting';
                // §3.5.11 steps 1.2 and 21: sequence mode and timestamp-generating byte
                // streams move timestampOffset as frames are processed.
                // The model keeps microseconds; the double script assigned stays unless the
                // model moved.
                try { var moved = g.__fenMseSbTimestampOffset(source._id, sb._bid); if (Math.abs(moved - sb._timestampOffset) > 0.000001) sb._timestampOffset = moved; } catch (e) {}
                sb._updating = false;
                updateStreaming(source);
                queueFire(source, sb, 'update');
                queueFire(source, sb, 'updateend');
            }

            function resetParserState(sb) {
                try { g.__fenMseSbResetParser(sb._source._id, sb._bid); } catch (e) {}
                sb._appendState = 'waiting';
                if (sb._mode === 'sequence') {
                    try { sb._timestampOffset = g.__fenMseSbTimestampOffset(sb._source._id, sb._bid); } catch (e) {}
                }
            }

            // §3.5.8 initialization segment received: the track lists.
            function initializationSegmentReceived(sb, tracks) {
                var source = sb._source;
                var element = source._element;
                for (var i = 0; i < tracks.length; i++) {
                    var d = tracks[i];
                    var track = null;
                    if (element && typeof g.__fenMediaAddInbandTrack === 'function') {
                        track = g.__fenMediaAddInbandTrack(element, d.kind, d.id, sb, d.language || '', d.label || '');
                    }
                    if (track) {
                        // Steps 5.2.7 / 5.3.7: the SourceBuffer's own list gets the track and
                        // its addtrack too.
                        var list = d.kind === 'audio' ? sb._audioTracks : sb._videoTracks;
                        list._tracks.push(track);
                        if (typeof g.__fenSyncTrackList === 'function') g.__fenSyncTrackList(list);
                        (function (l, t) { queueTask(source, function () { if (typeof g.__fenFireTrackListEvent === 'function') g.__fenFireTrackListEvent(l, 'addtrack', t); }); })(list, track);
                    }
                }
                if (isActive(sb)) activeAdd(source, sb);
            }

            method(SourceBuffer.prototype, 'abort', function () {
                sbCheckAttached(this);
                if (this._source._readyState() !== 'open') throw domException('The MediaSource is not open.', 'InvalidStateError');
                if (this._removing) throw domException('A range removal is in progress.', 'InvalidStateError');
                if (this._updating) {
                    // Abort the buffer append.
                    this._pendingBytes = null;
                    this._updating = false;
                    queueFire(this._source, this, 'abort');
                    queueFire(this._source, this, 'updateend');
                }
                resetParserState(this);
                this._appendWindowStart = 0;
                this._appendWindowEnd = Infinity;
                g.__fenMseSbSetAppendWindow(this._source._id, this._bid, 0, Infinity);
            }, 0);

            method(SourceBuffer.prototype, 'remove', function (start, end) {
                start = Number(start);
                end = Number(end);
                sbCheckAttached(this);
                sbCheckNotUpdating(this);
                var source = this._source;
                var duration = source.duration;
                if (isNaN(duration)) throw new TypeError('The MediaSource duration is NaN.');
                if (isNaN(start) || start < 0 || start > duration) throw new TypeError('start must lie in [0, duration].');
                if (isNaN(end) || end <= start) throw new TypeError('end must be greater than start.');
                if (source._readyState() === 'ended') sourceEnsureOpen(source);
                // §3.5.7 range removal.
                var sb = this;
                sb._updating = true;
                sb._removing = true;
                queueTask(source, function () { fire(sb, 'updatestart'); });
                queueTask(source, function () {
                    if (!sb._removing) return;
                    try { g.__fenMseSbRemove(source._id, sb._bid, start, end); } catch (e) {}
                    sb._removing = false;
                    sb._updating = false;
                    updateStreaming(source);
                    queueFire(source, sb, 'update');
                    queueFire(source, sb, 'updateend');
                });
            }, 2);

            method(SourceBuffer.prototype, 'changeType', function (type) {
                type = toDomString(type);
                if (type === '') throw new TypeError('The type is empty.');
                sbCheckAttached(this);
                sbCheckNotUpdating(this);
                if (!g.__fenMseIsTypeSupported(type, true)) throw domException("'" + type + "' is not a supported type.", 'NotSupportedError');
                var source = this._source;
                if (source._readyState() === 'ended') sourceEnsureOpen(source);
                resetParserState(this);
                g.__fenMseSbChangeType(source._id, this._bid, type);
                this._type = type;
                // §3.2 changeType steps 9-10: the generate timestamps flag follows the new
                // byte stream, and a stream that generates them runs in sequence mode
                // (one that does not keeps its mode, unless it was sequence only for that).
                var generates = !!g.__fenMseTypeGeneratesTimestamps(type);
                if (generates !== this._generateTimestamps) {
                    this._generateTimestamps = generates;
                    this._mode = generates ? 'sequence' : 'segments';
                    try { g.__fenMseSbSetMode(source._id, this._bid, this._mode); } catch (e) {}
                }
            }, 1);

            function newSourceBuffer(source, bid, type) {
                var sb = Object.create(SourceBuffer.prototype);
                g.EventTarget.call(sb);
                sb._source = source;
                sb._bid = bid;
                sb._type = type;
                // §2.1 addSourceBuffer steps 8-9: a byte stream that generates timestamps
                // starts in sequence mode.
                sb._generateTimestamps = !!g.__fenMseTypeGeneratesTimestamps(type);
                sb._mode = sb._generateTimestamps ? 'sequence' : 'segments';
                sb._updating = false;
                sb._removing = false;
                sb._removed = false;
                sb._appendState = 'waiting';
                sb._timestampOffset = 0;
                sb._appendWindowStart = 0;
                sb._appendWindowEnd = Infinity;
                sb._pendingBytes = null;
                sb._bufferedCache = null;
                sb._audioTracks = newTrackList('AudioTrackList');
                sb._videoTracks = newTrackList('VideoTrackList');
                sb._textTracks = newTrackList('TextTrackList');
                return sb;
            }
            function newTrackList(name) {
                var ctor = g[name];
                var list = Object.create(typeof ctor === 'function' ? ctor.prototype : Object.prototype);
                try { g.EventTarget.call(list); } catch (e) {}
                list._tracks = [];
                list._indexed = 0;
                if (typeof ctor !== 'function') {
                    Object.defineProperty(list, 'length', { get: function () { return this._tracks.length; } });
                }
                return list;
            }

            // ---- MediaSource (§2) ---------------------------------------------------------

            function MediaSource() {
                if (!(this instanceof MediaSource)) throw new TypeError("Failed to construct 'MediaSource': Please use the 'new' operator.");
                g.EventTarget.call(this);
                this._id = g.__fenMseCreate();
                this._element = null;
                this._sourceBuffers = newList();
                this._activeSourceBuffers = newList();
                sources.set(this._id, this);
            }
            defineInterface('MediaSource', MediaSource);
            ['onsourceopen', 'onsourceended', 'onsourceclose'].forEach(function (n) { handlerAttribute(MediaSource.prototype, n); });
            MediaSource.prototype._readyState = function () { return g.__fenMseReadyState(this._id); };
            accessor(MediaSource.prototype, 'sourceBuffers', function () { return this._sourceBuffers; });
            accessor(MediaSource.prototype, 'activeSourceBuffers', function () { return this._activeSourceBuffers; });
            accessor(MediaSource.prototype, 'readyState', function () { return this._readyState(); });
            accessor(MediaSource.prototype, 'duration',
                function () { return g.__fenMseDuration(this._id); },
                function (v) {
                    v = Number(v);
                    if (isNaN(v) || v < 0) throw new TypeError('The duration must be a non-negative number.');
                    if (this._readyState() !== 'open') throw domException('The MediaSource is not open.', 'InvalidStateError');
                    if (anyUpdating(this)) throw domException('A SourceBuffer is updating.', 'InvalidStateError');
                    g.__fenMseSetDuration(this._id, v);
                });
            function anyUpdating(source) {
                var buffers = source._sourceBuffers._buffers;
                for (var i = 0; i < buffers.length; i++) if (buffers[i]._updating) return true;
                return false;
            }
            method(MediaSource.prototype, 'addSourceBuffer', function (type) {
                type = toDomString(type);
                if (type === '') throw new TypeError('The type is empty.');
                if (!g.__fenMseIsTypeSupported(type, true)) throw domException("'" + type + "' is not a supported type.", 'NotSupportedError');
                if (this._readyState() !== 'open') throw domException('The MediaSource is not open.', 'InvalidStateError');
                var bid = g.__fenMseAddSourceBuffer(this._id, type);
                var sb = newSourceBuffer(this, bid, type);
                listAdd(this._sourceBuffers, this, sb);
                return sb;
            }, 1);
            method(MediaSource.prototype, 'removeSourceBuffer', function (sb) {
                if (!(sb instanceof SourceBuffer)) throw new TypeError("parameter 1 is not of type 'SourceBuffer'.");
                if (this._sourceBuffers._buffers.indexOf(sb) < 0) throw domException('The SourceBuffer does not belong to this MediaSource.', 'NotFoundError');
                removeSourceBuffer(this, sb);
            }, 1);
            function removeSourceBuffer(source, sb) {
                if (sb._updating) {
                    // Abort the pending append or removal.
                    sb._pendingBytes = null;
                    sb._removing = false;
                    sb._updating = false;
                    queueFire(source, sb, 'abort');
                    queueFire(source, sb, 'updateend');
                }
                removeTracks(source, sb);
                listRemove(source._activeSourceBuffers, source, sb);
                listRemove(source._sourceBuffers, source, sb);
                sb._removed = true;
                try { g.__fenMseRemoveSourceBuffer(source._id, sb._bid); } catch (e) {}
            }
            // §2.4.4 removeSourceBuffer steps 3-8: every track goes from the element's list
            // (with change when it was the enabled/selected one) and from the SourceBuffer's
            // own list, each with removetrack, and its sourceBuffer attribute is cleared.
            function removeTracks(source, sb) {
                var element = source._element;
                [sb._audioTracks, sb._videoTracks].forEach(function (list) {
                    var tracks = list._tracks.slice();
                    list._tracks = [];
                    if (typeof g.__fenSyncTrackList === 'function') g.__fenSyncTrackList(list);
                    for (var i = 0; i < tracks.length; i++) {
                        var track = tracks[i];
                        if (element && typeof g.__fenMediaRemoveInbandTrack === 'function') g.__fenMediaRemoveInbandTrack(element, track);
                        track._sourceBuffer = null;
                        (function (l, t) { queueTask(source, function () { if (typeof g.__fenFireTrackListEvent === 'function') g.__fenFireTrackListEvent(l, 'removetrack', t); }); })(list, track);
                    }
                });
            }
            method(MediaSource.prototype, 'endOfStream', function (error) {
                if (error !== undefined) {
                    error = toDomString(error);
                    if (error !== 'network' && error !== 'decode') throw new TypeError("'" + error + "' is not a valid EndOfStreamError.");
                }
                if (this._readyState() !== 'open') throw domException('The MediaSource is not open.', 'InvalidStateError');
                if (anyUpdating(this)) throw domException('A SourceBuffer is updating.', 'InvalidStateError');
                endOfStream(this, error, false);
            }, 0);
            function endOfStream(source, error, fromAppendError) {
                if (source._readyState() !== 'open') return;
                try { g.__fenMseEndOfStream(source._id, error || ''); } catch (e) { return; }
                updateStreaming(source);
                queueTask(source, function () { fire(source, 'sourceended'); });
            }
            method(MediaSource.prototype, 'setLiveSeekableRange', function (start, end) {
                start = toDouble(start, 'start');
                end = toDouble(end, 'end');
                if (this._readyState() !== 'open') throw domException('The MediaSource is not open.', 'InvalidStateError');
                if (start < 0 || start > end) throw new TypeError('start must lie in [0, end].');
                g.__fenMseSetLiveSeekableRange(this._id, start, end);
            }, 2);
            method(MediaSource.prototype, 'clearLiveSeekableRange', function () {
                if (this._readyState() !== 'open') throw domException('The MediaSource is not open.', 'InvalidStateError');
                g.__fenMseClearLiveSeekableRange(this._id);
            }, 0);
            Object.defineProperty(MediaSource, 'isTypeSupported', {
                value: function isTypeSupported(type) {
                    if (arguments.length === 0) throw new TypeError("Failed to execute 'isTypeSupported' on 'MediaSource': 1 argument required, but only 0 present.");
                    return g.__fenMseIsTypeSupported(type == null ? String(type) : toDomString(type));
                },
                writable: true, configurable: true
            });
            Object.defineProperty(MediaSource, 'canConstructInDedicatedWorker', { value: false, configurable: true });

            // ---- ManagedMediaSource (MSE §11) ------------------------------------------------
            // A MediaSource whose user agent says when it wants data: streaming is true (with
            // startstreaming) while less than STREAMING_AHEAD seconds are buffered past the
            // playback position, and false (with endstreaming) once that much is there, so a
            // player fetches only what playback needs. Evaluated on attach, after every
            // change to the buffers and as the position moves.
            var STREAMING_AHEAD = 10;
            function ManagedMediaSource() {
                if (!(this instanceof ManagedMediaSource)) throw new TypeError("Failed to construct 'ManagedMediaSource': Please use the 'new' operator.");
                MediaSource.call(this);
                this._managed = true;
                this._streaming = false;
            }
            defineInterface('ManagedMediaSource', ManagedMediaSource, MediaSource);
            // WebIDL: an interface object inherits its parent's, so the static
            // isTypeSupported answers on ManagedMediaSource too (hls.js prefers it).
            Object.setPrototypeOf(ManagedMediaSource, MediaSource);
            ['onstartstreaming', 'onendstreaming'].forEach(function (n) { handlerAttribute(ManagedMediaSource.prototype, n); });
            accessor(ManagedMediaSource.prototype, 'streaming', function () { return this._streaming; });
            Object.defineProperty(ManagedMediaSource, 'canConstructInDedicatedWorker', { value: false, configurable: true });
            function updateStreaming(source) {
                if (!source._managed) return;
                var wants;
                if (source._readyState() !== 'open') wants = false;
                else {
                    var element = source._element;
                    var position = element ? Number(element.currentTime) || 0 : 0;
                    var ahead = 0;
                    var buffers = source._activeSourceBuffers._buffers.length ? source._activeSourceBuffers._buffers : source._sourceBuffers._buffers;
                    var first = true;
                    for (var i = 0; i < buffers.length; i++) {
                        var ranges = buffers[i].buffered;
                        var own = 0;
                        for (var r = 0; r < ranges.length; r++) {
                            if (ranges.start(r) <= position + 0.1 && ranges.end(r) > position) { own = ranges.end(r) - position; break; }
                        }
                        ahead = first ? own : Math.min(ahead, own);
                        first = false;
                    }
                    wants = first || ahead < STREAMING_AHEAD;
                }
                if (wants === source._streaming) return;
                source._streaming = wants;
                queueTask(source, function () { fire(source, wants ? 'startstreaming' : 'endstreaming'); });
            }
            g.__fenMseUpdateStreaming = function (id) { var source = sources.get(id); if (source) updateStreaming(source); };

            // The media element's side: sourceopen when the attach steps ran, sourceclose
            // when the element let go (§2.4.2, §2.4.3).
            g.__fenMseDispatch = function (id, type, element) {
                var source = sources.get(id);
                if (!source) return;
                if (type === 'sourceopen') {
                    source._element = element || null;
                    fire(source, 'sourceopen');
                    if (source._managed && element) {
                        // The element's position drives the streaming flag.
                        try { element.addEventListener('timeupdate', function () { updateStreaming(source); }); } catch (e) {}
                        updateStreaming(source);
                    }
                    return;
                }
                if (type === 'sourceclose') {
                    var buffers = source._sourceBuffers._buffers.slice();
                    for (var i = 0; i < buffers.length; i++) {
                        var sb = buffers[i];
                        if (sb._updating) { sb._pendingBytes = null; sb._removing = false; sb._updating = false; queueFire(source, sb, 'abort'); queueFire(source, sb, 'updateend'); }
                        removeTracks(source, sb);
                        listRemove(source._activeSourceBuffers, source, sb);
                        listRemove(source._sourceBuffers, source, sb);
                        sb._removed = true;
                    }
                    queueTask(source, function () { fire(source, 'sourceclose'); });
                    source._element = null;
                    if (source._managed && source._streaming) { source._streaming = false; queueTask(source, function () { fire(source, 'endstreaming'); }); }
                }
            };
            // The blob URL store entry for a media element's src, when it is a MediaSource.
            g.__fenMseFromBlobUrl = function (url) {
                var entry = typeof g.__fenResolveBlobUrlEntry === 'function' ? g.__fenResolveBlobUrlEntry(url) : null;
                return entry instanceof MediaSource ? entry._id : -1;
            };
        })();
        """;
}
