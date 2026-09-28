namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// <c>MediaStream</c>, <c>MediaStreamTrack</c> and <c>MediaStreamTrackEvent</c> as script,
/// over the <c>__fenCaptureStream*</c> natives. The objects live here because a page holds
/// on to the track it was handed and compares it with the one it gets back.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private const string MediaStreamPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.EventTarget !== 'function' || typeof g.MediaStream === 'function') return;
            if (typeof g.__fenCaptureStream !== 'function') return;

            function defineInterface(name, ctor, base) {
                ctor.prototype = Object.create((base || g.EventTarget).prototype);
                Object.defineProperty(ctor.prototype, 'constructor', { value: ctor, writable: true, configurable: true });
                Object.defineProperty(ctor.prototype, Symbol.toStringTag, { value: name, configurable: true });
                Object.defineProperty(ctor, 'name', { value: name, configurable: true });
                g[name] = ctor;
                return ctor;
            }
            function accessor(target, name, get, set) {
                Object.defineProperty(target, name, { get: get, set: set, enumerable: true, configurable: true });
            }
            function method(target, name, fn, length) {
                if (length !== undefined) Object.defineProperty(fn, 'length', { value: length, configurable: true });
                Object.defineProperty(target, name, { value: fn, writable: true, enumerable: true, configurable: true });
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
            function queueTask(fn) {
                if (typeof g.__fenQueueMediaTask === 'function') g.__fenQueueMediaTask(null, fn);
                else g.setTimeout(fn, 0);
            }

            // ---- MediaStreamTrack ----

            var INTERNAL = {};
            function newId() {
                var hex = '';
                for (var i = 0; i < 32; i++) hex += Math.floor(Math.random() * 16).toString(16);
                return hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-4' + hex.slice(13, 16) + '-8' + hex.slice(17, 20) + '-' + hex.slice(20, 32);
            }

            // A track belongs to its source: an element's capture (stream._element), or an
            // audio pipe a Web Audio graph fills (_pipe). Clones share the source.
            function MediaStreamTrack(stream, state) {
                if (stream !== INTERNAL && !(stream === null || stream instanceof MediaStream))
                    throw new TypeError('Illegal constructor');
                g.EventTarget.call(this);
                this._stream = stream === INTERNAL ? null : stream;
                this._id = state.id;
                this._kind = state.kind;
                this._label = state.label || '';
                this._pipe = state.pipe || null;
                this._live = true;
                this._muted = false;
                this._enabled = true;
                // Whatever renders this track (a Web Audio source node) and must hear when it
                // is stopped or disabled.
                this._consumers = [];
            }
            defineInterface('MediaStreamTrack', MediaStreamTrack);
            function notifyConsumers(track) {
                for (var i = 0; i < track._consumers.length; i++) track._consumers[i](track);
            }
            accessor(MediaStreamTrack.prototype, 'id', function () { return this._id; });
            accessor(MediaStreamTrack.prototype, 'kind', function () { return this._kind; });
            accessor(MediaStreamTrack.prototype, 'label', function () { return this._label; });
            accessor(MediaStreamTrack.prototype, 'muted', function () { return this._muted; });
            accessor(MediaStreamTrack.prototype, 'readyState', function () { return this._live ? 'live' : 'ended'; });
            accessor(MediaStreamTrack.prototype, 'enabled',
                function () { return this._enabled; },
                function (v) {
                    var next = !!v;
                    if (next === this._enabled) return;
                    this._enabled = next;
                    notifyConsumers(this);
                });
            handlerAttribute(MediaStreamTrack.prototype, 'onended');
            handlerAttribute(MediaStreamTrack.prototype, 'onmute');
            handlerAttribute(MediaStreamTrack.prototype, 'onunmute');
            method(MediaStreamTrack.prototype, 'stop', function () {
                // Stopping a track does not fire ended - the page did it on purpose.
                if (!this._live) return;
                if (this._stream && this._stream._element) g.__fenCaptureStreamStopTrack(this._stream._element, this._id);
                this._live = false;
                if (this._stream && this._stream._element) this._stream._sync();
                notifyConsumers(this);
            }, 0);
            // mediacapture-main clone(): a new track with its own id on the same source.
            method(MediaStreamTrack.prototype, 'clone', function () {
                var t = new MediaStreamTrack(INTERNAL, { id: newId(), kind: this._kind, label: this._label, pipe: this._pipe });
                t._live = this._live;
                t._muted = this._muted;
                t._enabled = this._enabled;
                t._element = this._stream ? this._stream._element : this._element;
                return t;
            }, 0);
            method(MediaStreamTrack.prototype, 'getSettings', function () {
                return { deviceId: this._id };
            }, 0);
            method(MediaStreamTrack.prototype, 'getCapabilities', function () { return {}; }, 0);
            method(MediaStreamTrack.prototype, 'getConstraints', function () { return {}; }, 0);
            method(MediaStreamTrack.prototype, 'applyConstraints', function () { return Promise.resolve(); }, 0);

            // ---- MediaStreamTrackEvent ----

            function MediaStreamTrackEvent(type, init) {
                if (arguments.length < 1)
                    throw new TypeError("Failed to construct 'MediaStreamTrackEvent': 1 argument required.");
                init = init || {};
                if (!init.track)
                    throw new TypeError("Failed to construct 'MediaStreamTrackEvent': track is required.");
                var ev = new g.Event(String(type), init);
                Object.setPrototypeOf(ev, MediaStreamTrackEvent.prototype);
                ev._track = init.track;
                return ev;
            }
            defineInterface('MediaStreamTrackEvent', MediaStreamTrackEvent, g.Event);
            accessor(MediaStreamTrackEvent.prototype, 'track', function () { return this._track; });

            // ---- MediaStream ----

            // mediacapture-main 4.2: MediaStream(), MediaStream(stream) and
            // MediaStream(tracks). An element's capture builds one through the INTERNAL token.
            function MediaStream(a, b, c) {
                if (!(this instanceof MediaStream)) throw new TypeError("Failed to construct 'MediaStream': Please use the 'new' operator.");
                g.EventTarget.call(this);
                this._tracks = [];
                if (a === INTERNAL) {
                    this._element = b;
                    this._id = c;
                    return;
                }
                this._element = null;
                this._id = newId();
                var source = a === undefined ? [] : a instanceof MediaStream ? a.getTracks() : a;
                if (source === null || typeof source !== 'object' || typeof source[Symbol.iterator] !== 'function')
                    throw new TypeError("Failed to construct 'MediaStream': The provided value cannot be converted to a sequence.");
                for (var t of source) {
                    if (!(t instanceof MediaStreamTrack)) throw new TypeError("Failed to construct 'MediaStream': Failed to convert value to 'MediaStreamTrack'.");
                    if (this._tracks.indexOf(t) < 0) this._tracks.push(t);
                }
            }
            defineInterface('MediaStream', MediaStream);
            accessor(MediaStream.prototype, 'id', function () { return this._id; });
            accessor(MediaStream.prototype, 'active', function () {
                for (var i = 0; i < this._tracks.length; i++) if (this._tracks[i]._live) return true;
                return false;
            });
            handlerAttribute(MediaStream.prototype, 'onaddtrack');
            handlerAttribute(MediaStream.prototype, 'onremovetrack');
            method(MediaStream.prototype, 'getTracks', function () { return this._tracks.slice(); }, 0);
            method(MediaStream.prototype, 'getAudioTracks', function () {
                return this._tracks.filter(function (t) { return t.kind === 'audio'; });
            }, 0);
            method(MediaStream.prototype, 'getVideoTracks', function () {
                return this._tracks.filter(function (t) { return t.kind === 'video'; });
            }, 0);
            method(MediaStream.prototype, 'addTrack', function (track) {
                if (!(track instanceof MediaStreamTrack)) throw new TypeError("Failed to execute 'addTrack' on 'MediaStream': parameter 1 is not of type 'MediaStreamTrack'.");
                if (this._tracks.indexOf(track) < 0) this._tracks.push(track);
            }, 1);
            method(MediaStream.prototype, 'removeTrack', function (track) {
                if (!(track instanceof MediaStreamTrack)) throw new TypeError("Failed to execute 'removeTrack' on 'MediaStream': parameter 1 is not of type 'MediaStreamTrack'.");
                var at = this._tracks.indexOf(track);
                if (at >= 0) this._tracks.splice(at, 1);
            }, 1);
            method(MediaStream.prototype, 'clone', function () {
                return new MediaStream(this._tracks.map(function (t) { return t.clone(); }));
            }, 0);
            method(MediaStream.prototype, 'getTrackById', function (id) {
                for (var i = 0; i < this._tracks.length; i++)
                    if (this._tracks[i].id === String(id)) return this._tracks[i];
                return null;
            }, 1);

            function fireTrackEvent(target, type, track) {
                var ev = new MediaStreamTrackEvent(type, { track: track, bubbles: false, cancelable: false });
                try { target.dispatchEvent(ev); } catch (e) {}
            }

            // Brings the page's objects in line with what the element has now, and fires
            // what that change owes the page. The tracks a page already holds are kept.
            method(MediaStream.prototype, '_sync', function () {
                if (!this._element) return;
                var state = g.__fenCaptureStreamPoll(this._element);
                if (!state) return;

                var self = this;
                var kept = [];
                var added = [];
                var removed = [];
                var byId = Object.create(null);
                for (var i = 0; i < this._tracks.length; i++) byId[this._tracks[i].id] = this._tracks[i];

                for (var j = 0; j < state.tracks.length; j++) {
                    var s = state.tracks[j];
                    var track = byId[s.id];
                    if (!track) {
                        track = new MediaStreamTrack(this, s);
                        added.push(track);
                    } else {
                        delete byId[s.id];
                    }
                    var wasMuted = track._muted;
                    track._muted = !!s.muted;
                    track._live = !!s.live;
                    if (wasMuted !== track._muted) {
                        (function (t) {
                            queueTask(function () {
                                try { t.dispatchEvent(new g.Event(t._muted ? 'mute' : 'unmute')); } catch (e) {}
                            });
                        })(track);
                    }
                    kept.push(track);
                }

                for (var id in byId) {
                    var gone = byId[id];
                    gone._live = false;
                    removed.push(gone);
                }

                this._tracks = kept;

                if (added.length || removed.length) {
                    // A track ends before it is removed, but in a task of its own: a page
                    // that waits for "ended" and only then listens for "removetrack" - which
                    // is how the specification reads - has to still be in time for it.
                    queueTask(function () {
                        for (var a = 0; a < removed.length; a++) {
                            try { removed[a].dispatchEvent(new g.Event('ended')); } catch (e) {}
                        }

                        queueTask(function () {
                            for (var c = 0; c < removed.length; c++) fireTrackEvent(self, 'removetrack', removed[c]);
                            for (var b = 0; b < added.length; b++) fireTrackEvent(self, 'addtrack', added[b]);
                        });
                    });
                }
            }, 0);

            // ---- HTMLMediaElement.srcObject (HTML 4.8.11.2) ----
            // The assigned media provider object: a MediaStream, MediaSource or Blob. The
            // pipeline loads a MediaSource or Blob through a blob: URL of its own, which is
            // revoked when the object is replaced; a MediaStream goes by its id.
            var providers = new WeakMap();
            var providerUrls = new WeakMap();

            // Streams some element plays, with how many elements play each. Their track
            // lists are pushed to the engine whenever they change.
            var providerStreams = new Map();
            function streamUrl(stream) { return 'fen-mediastream:' + stream.id; }
            function syncProviders() { providerStreams.forEach(function (_, stream) { syncStream(stream); }); }
            function syncStream(stream) {
                g.__fenSyncMediaStream(streamUrl(stream), stream._tracks.map(function (t) {
                    if (!t.__fenProviderWatched) {
                        Object.defineProperty(t, '__fenProviderWatched', { value: true });
                        t._consumers.push(syncProviders);
                    }
                    return { id: String(t._id), kind: String(t._kind), pipe: t._pipe || '', video: t._video || '', live: !!t._live, enabled: !!t._enabled };
                }));
            }
            function retainStream(stream) {
                providerStreams.set(stream, (providerStreams.get(stream) || 0) + 1);
                syncStream(stream);
            }
            function releaseStream(stream) {
                var count = (providerStreams.get(stream) || 0) - 1;
                if (count > 0) { providerStreams.set(stream, count); return; }
                providerStreams.delete(stream);
                g.__fenForgetMediaStream(streamUrl(stream));
            }
            ['addTrack', 'removeTrack', '_sync'].forEach(function (name) {
                var original = MediaStream.prototype[name];
                if (typeof original !== 'function') return;
                Object.defineProperty(MediaStream.prototype, name, {
                    value: function () {
                        var result = original.apply(this, arguments);
                        if (providerStreams.has(this)) syncStream(this);
                        return result;
                    },
                    writable: true, configurable: true
                });
            });
            if (typeof g.HTMLMediaElement === 'function' && g.HTMLMediaElement.prototype) {
                Object.defineProperty(g.HTMLMediaElement.prototype, 'srcObject', {
                    get: function () {
                        return providers.has(this) ? providers.get(this) : null;
                    },
                    set: function (value) {
                        if (value === undefined) value = null;
                        if (value !== null &&
                            !(value instanceof MediaStream) &&
                            !(typeof g.MediaSource === 'function' && value instanceof g.MediaSource) &&
                            !(typeof g.ManagedMediaSource === 'function' && value instanceof g.ManagedMediaSource) &&
                            !(typeof g.Blob === 'function' && value instanceof g.Blob)) {
                            throw new TypeError("Failed to set the 'srcObject' property on 'HTMLMediaElement': " +
                                "The provided value is not of type '(MediaSourceHandle or MediaStream or MediaSource or Blob)'.");
                        }
                        var previousUrl = providerUrls.get(this);
                        if (previousUrl) g.URL.revokeObjectURL(previousUrl);
                        providerUrls.delete(this);
                        var previous = providers.get(this);
                        if (previous instanceof MediaStream) releaseStream(previous);
                        var url = null;
                        if (value === null) {
                            providers.delete(this);
                        } else {
                            providers.set(this, value);
                            if (value instanceof MediaStream) {
                                retainStream(value);
                                url = streamUrl(value);
                            } else {
                                url = g.URL.createObjectURL(value);
                                providerUrls.set(this, url);
                            }
                        }
                        g.__fenSetMediaProvider(this, url);
                    },
                    configurable: true, enumerable: true
                });
            }

            // ---- HTMLCanvasElement.captureStream() (mediacapture-fromelement 3) ----
            if (typeof g.HTMLCanvasElement === 'function' && g.HTMLCanvasElement.prototype &&
                typeof g.__fenCanvasCaptureStream === 'function') {
                var CanvasCaptureMediaStreamTrack = function CanvasCaptureMediaStreamTrack() {
                    throw new TypeError('Illegal constructor');
                };
                CanvasCaptureMediaStreamTrack.prototype = Object.create(MediaStreamTrack.prototype);
                Object.defineProperty(CanvasCaptureMediaStreamTrack.prototype, 'constructor',
                    { value: CanvasCaptureMediaStreamTrack, writable: true, configurable: true });
                Object.defineProperty(CanvasCaptureMediaStreamTrack.prototype, Symbol.toStringTag,
                    { value: 'CanvasCaptureMediaStreamTrack', configurable: true });
                accessor(CanvasCaptureMediaStreamTrack.prototype, 'canvas', function () { return this._canvas; });
                // mediacapture-fromelement issue 48 (tentative): a canvas track's settings
                // are the canvas's current size.
                method(CanvasCaptureMediaStreamTrack.prototype, 'getSettings', function () {
                    var canvas = this._canvas;
                    return { width: canvas.width, height: canvas.height, resizeMode: 'none' };
                }, 0);
                method(CanvasCaptureMediaStreamTrack.prototype, 'requestFrame', function () {
                    if (this._live) g.__fenCanvasRequestFrame(this._video);
                }, 0);
                Object.defineProperty(g, 'CanvasCaptureMediaStreamTrack',
                    { value: CanvasCaptureMediaStreamTrack, writable: true, configurable: true });

                // A stopped canvas track takes no more frames.
                var stopTrack = MediaStreamTrack.prototype.stop;
                method(MediaStreamTrack.prototype, 'stop', function () {
                    var wasLive = this._live;
                    stopTrack.call(this);
                    if (wasLive && this._video) g.__fenCanvasCaptureStop(this._video);
                }, 0);

                method(g.HTMLCanvasElement.prototype, 'captureStream', function (frameRate) {
                    var rate = -1;
                    if (frameRate !== undefined) {
                        rate = Number(frameRate);
                        if (!(rate >= 0)) {
                            throw new g.DOMException('The frame rate must be a non-negative number.', 'NotSupportedError');
                        }
                    }
                    var key = g.__fenCanvasCaptureStream(this, rate);
                    if (key === null) throw new g.DOMException('The canvas cannot be captured.', 'InvalidStateError');
                    var track = new MediaStreamTrack(INTERNAL, { id: newId(), kind: 'video', label: '', pipe: null });
                    Object.setPrototypeOf(track, CanvasCaptureMediaStreamTrack.prototype);
                    Object.defineProperty(track, '_video', { value: key });
                    Object.defineProperty(track, '_canvas', { value: this });
                    return new MediaStream([track]);
                }, 0);
            }

            // ---- Tracks for getUserMedia (the UserMedia prelude builds on this) ----
            // A captured device's track: its audio pipe or picture source, the key that
            // releases the device, and the settings the device was opened with.
            Object.defineProperty(g, '__fenMakeCaptureTrack', {
                value: function (kind, label, pipe, video, key, settings) {
                    var track = new MediaStreamTrack(INTERNAL, { id: newId(), kind: kind, label: label, pipe: pipe || null });
                    if (video) Object.defineProperty(track, '_video', { value: video });
                    Object.defineProperty(track, '_capture', { value: key });
                    Object.defineProperty(track, '_settings', { value: settings });
                    return track;
                },
                configurable: true
            });
            var trackSettings = MediaStreamTrack.prototype.getSettings;
            method(MediaStreamTrack.prototype, 'getSettings', function () {
                if (this._settings) return Object.assign({}, this._settings);
                return trackSettings.call(this);
            }, 0);
            var stopCaptured = MediaStreamTrack.prototype.stop;
            method(MediaStreamTrack.prototype, 'stop', function () {
                var wasLive = this._live;
                stopCaptured.call(this);
                if (wasLive && this._capture) g.__fenCloseCapture(this._capture);
            }, 0);

            // ---- HTMLMediaElement.captureStream() ----

            var streams = new WeakMap();
            if (typeof g.HTMLMediaElement === 'function' && g.HTMLMediaElement.prototype) {
                method(g.HTMLMediaElement.prototype, 'captureStream', function () {
                    var element = this;
                    var stream = streams.get(element);
                    if (!stream) {
                        var id = g.__fenCaptureStream(element);
                        if (id === null) throw new g.DOMException('The element cannot be captured.', 'InvalidStateError');
                        stream = new MediaStream(INTERNAL, element, id);
                        streams.set(element, stream);

                        // The element's own events are what move the capture along: a
                        // resource arriving, being replaced, or running out.
                        ['loadedmetadata', 'emptied', 'ended', 'play', 'pause', 'playing',
                         'waiting', 'canplay', 'loadstart'].forEach(function (type) {
                            element.addEventListener(type, function () { stream._sync(); });
                        });
                    }

                    stream._sync();
                    return stream;
                }, 0);
            }

            // For Web Audio (WA 1.21): a stream with one audio track fed by an engine pipe.
            Object.defineProperty(g, '__fenStreamWithAudioPipe', {
                value: function (pipe) {
                    var track = new MediaStreamTrack(INTERNAL, { id: newId(), kind: 'audio', label: '', pipe: pipe });
                    return new MediaStream([track]);
                },
                configurable: true, writable: true, enumerable: false,
            });
        })();
        """;
}
