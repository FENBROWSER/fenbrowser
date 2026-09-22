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

            function MediaStreamTrack(stream, state) {
                g.EventTarget.call(this);
                this._stream = stream;
                this._id = state.id;
                this._kind = state.kind;
                this._label = state.label;
                this._live = true;
                this._muted = false;
                this._enabled = true;
            }
            defineInterface('MediaStreamTrack', MediaStreamTrack);
            accessor(MediaStreamTrack.prototype, 'id', function () { return this._id; });
            accessor(MediaStreamTrack.prototype, 'kind', function () { return this._kind; });
            accessor(MediaStreamTrack.prototype, 'label', function () { return this._label; });
            accessor(MediaStreamTrack.prototype, 'muted', function () { return this._muted; });
            accessor(MediaStreamTrack.prototype, 'readyState', function () { return this._live ? 'live' : 'ended'; });
            accessor(MediaStreamTrack.prototype, 'enabled',
                function () { return this._enabled; },
                function (v) { this._enabled = !!v; });
            handlerAttribute(MediaStreamTrack.prototype, 'onended');
            handlerAttribute(MediaStreamTrack.prototype, 'onmute');
            handlerAttribute(MediaStreamTrack.prototype, 'onunmute');
            method(MediaStreamTrack.prototype, 'stop', function () {
                // Stopping a track does not fire ended - the page did it on purpose.
                if (!this._live) return;
                if (this._stream) g.__fenCaptureStreamStopTrack(this._stream._element, this._id);
                this._live = false;
                if (this._stream) this._stream._sync();
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

            function MediaStream(element, id) {
                g.EventTarget.call(this);
                this._element = element;
                this._id = id;
                this._tracks = [];
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

            // ---- HTMLMediaElement.captureStream() ----

            var streams = new WeakMap();
            if (typeof g.HTMLMediaElement === 'function' && g.HTMLMediaElement.prototype) {
                method(g.HTMLMediaElement.prototype, 'captureStream', function () {
                    var element = this;
                    var stream = streams.get(element);
                    if (!stream) {
                        var id = g.__fenCaptureStream(element);
                        if (id === null) throw new g.DOMException('The element cannot be captured.', 'InvalidStateError');
                        stream = new MediaStream(element, id);
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
        })();
        """;
}
