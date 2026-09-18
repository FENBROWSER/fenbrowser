namespace FenBrowser.FenEngine.Scripting;

public sealed partial class FenJsBrowserScriptEngine
{
    /// <summary>
    /// The text track object model, as script in the realm. Spec references: HTML §4.8.12
    /// (text tracks, TextTrackList, TextTrack, TextTrackCueList, TextTrackCue, the "time
    /// marches on" steps, TrackEvent, audio and video track lists), §4.8.12.11 (the track
    /// element and its processing model), WebVTT §5 (VTTCue, VTTRegion) and §7.2 (cue text
    /// DOM construction). The engine reaches in through the <c>__fen*</c> globals defined at
    /// the end; the model reaches out through <c>__fenVttParse</c>, <c>__fenVttCueTree</c>,
    /// <c>__fenTrackFetch</c>, <c>__fenMediaState</c> and <c>__fenQueueMediaTask</c>.
    /// </summary>
    private const string TextTracksPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.EventTarget !== 'function' || typeof g.TextTrack === 'function') return;

            // ---- interface plumbing --------------------------------------------------------

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
                accessor(proto, name,
                    function () { return this[key] || null; },
                    function (v) { this[key] = (typeof v === 'function' || (v !== null && typeof v === 'object')) ? v : null; });
            }
            function constants(ctor, table) {
                Object.keys(table).forEach(function (key) {
                    var d = { value: table[key], writable: false, enumerable: true, configurable: false };
                    Object.defineProperty(ctor, key, d);
                    Object.defineProperty(ctor.prototype, key, d);
                });
            }
            function domException(message, name) { return new g.DOMException(message, name); }
            function isNode(v) { return typeof g.Node === 'function' && v instanceof g.Node; }

            // DOM "fire an event": host nodes dispatch through the engine, which also runs
            // their on* handlers; the script-side targets here run theirs by hand.
            function fire(target, type, init) {
                if (!target) return;
                var ev = init && ('track' in init) ? new g.TrackEvent(type, init) : new g.Event(type, init || {});
                try { target.dispatchEvent(ev); } catch (e) {}
                if (!isNode(target)) {
                    var handler = target['on' + type];
                    if (typeof handler === 'function') { try { handler.call(target, ev); } catch (e) {} }
                    else if (handler && typeof handler.handleEvent === 'function') { try { handler.handleEvent(ev); } catch (e) {} }
                }
            }
            function queueTask(element, fn) {
                if (typeof g.__fenQueueMediaTask === 'function' && element) g.__fenQueueMediaTask(element, fn);
                else g.setTimeout(fn, 0);
            }

            // WebIDL conversions
            function toDouble(v, what) {
                var n = Number(v);
                if (!isFinite(n)) throw new TypeError(what + ' is not a finite floating-point value.');
                return n;
            }
            function toUnrestrictedDouble(v) { return Number(v); }
            function toDomString(v) { return v === undefined ? '' : String(v); }
            function toUnsignedLong(v) {
                var n = Number(v);
                if (!isFinite(n)) return 0;
                n = Math.trunc(n) % 4294967296;
                return n < 0 ? n + 4294967296 : n;
            }
            function inEnum(v, values) { return values.indexOf(v) >= 0; }

            // ---- TrackEvent (HTML §4.8.12.13) ----------------------------------------------

            function TrackEvent(type, init) {
                if (arguments.length === 0) throw new TypeError("Failed to construct 'TrackEvent': 1 argument required, but only 0 present.");
                try { g.Event.call(this, type, init); }
                catch (e) {
                    this.type = String(type);
                    this.bubbles = !!(init && init.bubbles);
                    this.cancelable = !!(init && init.cancelable);
                    this.composed = !!(init && init.composed);
                    this.defaultPrevented = false;
                    this.target = null;
                    this.currentTarget = null;
                    this.eventPhase = 0;
                    this.timeStamp = Date.now();
                }
                var track = init && init.track !== undefined ? init.track : null;
                Object.defineProperty(this, 'track', { value: track, enumerable: true, configurable: true });
            }
            defineInterface('TrackEvent', TrackEvent, g.Event);

            // ---- TextTrackCue / VTTCue (HTML §4.8.12.6, WebVTT §5.2) -----------------------

            var DIRECTIONS = ['', 'rl', 'lr'];
            var LINE_ALIGNS = ['start', 'center', 'end'];
            var POSITION_ALIGNS = ['line-left', 'center', 'line-right', 'auto'];
            var ALIGNS = ['start', 'center', 'end', 'left', 'right'];

            function TextTrackCue() { throw new TypeError('Illegal constructor'); }
            defineInterface('TextTrackCue', TextTrackCue);
            accessor(TextTrackCue.prototype, 'track', function () { return this._track || null; });
            accessor(TextTrackCue.prototype, 'id',
                function () { return this._id; },
                function (v) { this._id = toDomString(v); });
            accessor(TextTrackCue.prototype, 'startTime',
                function () { return this._startTime; },
                function (v) {
                    this._startTime = toDouble(v, 'startTime');
                    if (this._track) this._track._cueTimingChanged(this);
                });
            accessor(TextTrackCue.prototype, 'endTime',
                function () { return this._endTime; },
                function (v) {
                    var n = toUnrestrictedDouble(v);
                    if (n !== n) throw new TypeError('The provided double value is non-finite.');
                    this._endTime = n;
                    if (this._track) this._track._cueTimingChanged(this);
                });
            accessor(TextTrackCue.prototype, 'pauseOnExit',
                function () { return this._pauseOnExit; },
                function (v) { this._pauseOnExit = !!v; });
            handlerAttribute(TextTrackCue.prototype, 'onenter');
            handlerAttribute(TextTrackCue.prototype, 'onexit');

            function VTTCue(startTime, endTime, text) {
                if (!(this instanceof VTTCue)) throw new TypeError("Failed to construct 'VTTCue': Please use the 'new' operator.");
                if (arguments.length < 3) throw new TypeError("Failed to construct 'VTTCue': 3 arguments required, but only " + arguments.length + " present.");
                g.EventTarget.call(this);
                this._startTime = toDouble(startTime, 'startTime');
                var end = toUnrestrictedDouble(endTime);
                if (end !== end) throw new TypeError("Failed to construct 'VTTCue': The provided double value is non-finite.");
                this._endTime = end;
                this._text = toDomString(text);
                this._id = '';
                this._pauseOnExit = false;
                this._track = null;
                this._active = false;
                this._order = 0;
                this._region = null;
                this._vertical = '';
                this._snapToLines = true;
                this._line = 'auto';
                this._lineAlign = 'start';
                this._position = 'auto';
                this._positionAlign = 'auto';
                this._size = 100;
                this._align = 'center';
            }
            defineInterface('VTTCue', VTTCue, TextTrackCue);
            accessor(VTTCue.prototype, 'region',
                function () { return this._region; },
                function (v) { this._region = (v instanceof g.VTTRegion) ? v : null; });
            accessor(VTTCue.prototype, 'vertical',
                function () { return this._vertical; },
                function (v) { v = toDomString(v); if (inEnum(v, DIRECTIONS)) this._vertical = v; });
            accessor(VTTCue.prototype, 'snapToLines',
                function () { return this._snapToLines; },
                function (v) { this._snapToLines = !!v; });
            accessor(VTTCue.prototype, 'line',
                function () { return this._line; },
                function (v) {
                    if (typeof v === 'number') { this._line = toDouble(v, 'line'); return; }
                    if (v === 'auto') { this._line = 'auto'; return; }
                    if (typeof v === 'string' || v === null || v === undefined) throw new TypeError("Failed to set the 'line' property on 'VTTCue': The provided value is not a valid enum value of type AutoKeyword.");
                    this._line = toDouble(v, 'line');
                });
            accessor(VTTCue.prototype, 'lineAlign',
                function () { return this._lineAlign; },
                function (v) { v = toDomString(v); if (inEnum(v, LINE_ALIGNS)) this._lineAlign = v; });
            accessor(VTTCue.prototype, 'position',
                function () { return this._position; },
                function (v) {
                    var n;
                    if (typeof v === 'number') n = toDouble(v, 'position');
                    else if (v === 'auto') { this._position = 'auto'; return; }
                    else if (typeof v === 'string' || v === null || v === undefined) throw new TypeError("Failed to set the 'position' property on 'VTTCue': The provided value is not a valid enum value of type AutoKeyword.");
                    else n = toDouble(v, 'position');
                    if (n < 0 || n > 100) throw domException("Failed to set the 'position' property on 'VTTCue': The value provided (" + n + ") is outside the range [0, 100].", 'IndexSizeError');
                    this._position = n;
                });
            accessor(VTTCue.prototype, 'positionAlign',
                function () { return this._positionAlign; },
                function (v) { v = toDomString(v); if (inEnum(v, POSITION_ALIGNS)) this._positionAlign = v; });
            accessor(VTTCue.prototype, 'size',
                function () { return this._size; },
                function (v) {
                    var n = toDouble(v, 'size');
                    if (n < 0 || n > 100) throw domException("Failed to set the 'size' property on 'VTTCue': The value provided (" + n + ") is outside the range [0, 100].", 'IndexSizeError');
                    this._size = n;
                });
            accessor(VTTCue.prototype, 'align',
                function () { return this._align; },
                function (v) { v = toDomString(v); if (inEnum(v, ALIGNS)) this._align = v; });
            accessor(VTTCue.prototype, 'text',
                function () { return this._text; },
                function (v) { this._text = toDomString(v); });
            method(VTTCue.prototype, 'getCueAsHTML', function () {
                var doc = g.document;
                var fragment = doc.createDocumentFragment();
                var lang = this._track ? this._track.language : '';
                var tree;
                try { tree = JSON.parse(g.__fenVttCueTree(this._text, lang)); } catch (e) { tree = null; }
                if (tree) appendCueNodes(doc, fragment, tree.children || []);
                return fragment;
            }, 0);

            // WebVTT §7.2 "WebVTT cue text DOM construction rules".
            function appendCueNodes(doc, parent, children) {
                for (var i = 0; i < children.length; i++) {
                    var node = children[i];
                    var el = null;
                    switch (node.kind) {
                        case 'text':
                            parent.appendChild(doc.createTextNode(node.text));
                            continue;
                        case 'timestamp':
                            try { parent.appendChild(doc.createProcessingInstruction('timestamp', node.text)); } catch (e) {}
                            continue;
                        case 'c': el = doc.createElement('span'); break;
                        case 'i': el = doc.createElement('i'); break;
                        case 'b': el = doc.createElement('b'); break;
                        case 'u': el = doc.createElement('u'); break;
                        case 'ruby': el = doc.createElement('ruby'); break;
                        case 'rt': el = doc.createElement('rt'); break;
                        case 'v': el = doc.createElement('span'); el.setAttribute('title', node.annotation || ''); break;
                        case 'lang': el = doc.createElement('span'); break;
                        default: continue;
                    }
                    if (node.classes) el.setAttribute('class', node.classes);
                    if (node.kind === 'lang') el.setAttribute('lang', node.annotation || '');
                    appendCueNodes(doc, el, node.children || []);
                    parent.appendChild(el);
                }
            }

            // ---- VTTRegion (WebVTT §5.1) ---------------------------------------------------

            function VTTRegion() {
                if (!(this instanceof VTTRegion)) throw new TypeError("Failed to construct 'VTTRegion': Please use the 'new' operator.");
                this._id = '';
                this._width = 100;
                this._lines = 3;
                this._regionAnchorX = 0;
                this._regionAnchorY = 100;
                this._viewportAnchorX = 0;
                this._viewportAnchorY = 100;
                this._scroll = '';
            }
            defineInterface('VTTRegion', VTTRegion, Object);
            function percentAccessor(name) {
                var key = '_' + name;
                accessor(VTTRegion.prototype, name,
                    function () { return this[key]; },
                    function (v) {
                        var n = toDouble(v, name);
                        if (n < 0 || n > 100) throw domException("Failed to set the '" + name + "' property on 'VTTRegion': The value provided (" + n + ") is outside the range [0, 100].", 'IndexSizeError');
                        this[key] = n;
                    });
            }
            accessor(VTTRegion.prototype, 'id', function () { return this._id; }, function (v) { this._id = toDomString(v); });
            percentAccessor('width');
            accessor(VTTRegion.prototype, 'lines', function () { return this._lines; }, function (v) { this._lines = toUnsignedLong(v); });
            percentAccessor('regionAnchorX');
            percentAccessor('regionAnchorY');
            percentAccessor('viewportAnchorX');
            percentAccessor('viewportAnchorY');
            accessor(VTTRegion.prototype, 'scroll',
                function () { return this._scroll; },
                function (v) { v = toDomString(v); if (v === '' || v === 'up') this._scroll = v; });

            // ---- TextTrackCueList (HTML §4.8.12.7) -----------------------------------------

            function TextTrackCueList() { throw new TypeError('Illegal constructor'); }
            defineInterface('TextTrackCueList', TextTrackCueList, Object);
            function makeCueList() {
                var list = Object.create(TextTrackCueList.prototype);
                list._cues = [];
                list._indexed = 0;
                return list;
            }
            function syncIndexed(list, items) {
                var n = items.length;
                for (var i = 0; i < n; i++) {
                    Object.defineProperty(list, i, { value: items[i], writable: false, enumerable: true, configurable: true });
                }
                for (var j = n; j < list._indexed; j++) delete list[j];
                list._indexed = n;
            }
            accessor(TextTrackCueList.prototype, 'length', function () { return this._cues.length; });
            method(TextTrackCueList.prototype, 'item', function (index) {
                var i = toUnsignedLong(index);
                return i < this._cues.length ? this._cues[i] : null;
            }, 1);
            method(TextTrackCueList.prototype, 'getCueById', function (id) {
                id = toDomString(id);
                if (id === '') return null;
                for (var i = 0; i < this._cues.length; i++) if (this._cues[i]._id === id) return this._cues[i];
                return null;
            }, 1);
            if (typeof Symbol === 'function' && Symbol.iterator) {
                method(TextTrackCueList.prototype, Symbol.iterator, function () { return this._cues.slice()[Symbol.iterator](); });
            }

            // Text track cue order (§4.8.12.6): by start time, then by end time latest first,
            // then in the order the cues were added.
            function cueOrder(a, b) {
                if (a._startTime !== b._startTime) return a._startTime - b._startTime;
                if (a._endTime !== b._endTime) return b._endTime - a._endTime;
                return a._order - b._order;
            }

            // ---- TextTrack (HTML §4.8.12.5) ------------------------------------------------

            var KINDS = ['subtitles', 'captions', 'descriptions', 'chapters', 'metadata'];
            var MODES = ['disabled', 'hidden', 'showing'];
            var cueOrderCounter = 0;

            function TextTrack() { throw new TypeError('Illegal constructor'); }
            defineInterface('TextTrack', TextTrack);
            function makeTextTrack(kind, label, language, trackElement) {
                var track = Object.create(TextTrack.prototype);
                g.EventTarget.call(track);
                track._kind = inEnum(kind, KINDS) ? kind : 'metadata';
                track._label = label || '';
                track._language = language || '';
                track._id = '';
                track._mode = 'disabled';
                track._cueList = makeCueList();
                track._activeList = makeCueList();
                track._trackElement = trackElement || null;
                track._media = null;      // the media element whose list holds this track
                track._readiness = 'not loaded';
                return track;
            }
            accessor(TextTrack.prototype, 'kind', function () { return this._kind; });
            accessor(TextTrack.prototype, 'label', function () { return this._label; });
            accessor(TextTrack.prototype, 'language', function () { return this._language; });
            accessor(TextTrack.prototype, 'id', function () { return this._id; });
            accessor(TextTrack.prototype, 'inBandMetadataTrackDispatchType', function () { return ''; });
            accessor(TextTrack.prototype, 'mode',
                function () { return this._mode; },
                function (v) {
                    v = toDomString(v);
                    if (!inEnum(v, MODES)) return;
                    setTrackMode(this, v, true);
                });
            accessor(TextTrack.prototype, 'cues', function () { return this._mode === 'disabled' ? null : this._cueList; });
            accessor(TextTrack.prototype, 'activeCues', function () { return this._mode === 'disabled' ? null : this._activeList; });
            handlerAttribute(TextTrack.prototype, 'oncuechange');
            method(TextTrack.prototype, 'addCue', function (cue) {
                if (!(cue instanceof TextTrackCue)) throw new TypeError("Failed to execute 'addCue' on 'TextTrack': parameter 1 is not of type 'TextTrackCue'.");
                // §4.8.12.5: a cue already in another track's list is removed from it first.
                if (cue._track && cue._track !== this) cue._track._removeCue(cue, false);
                if (cue._track === this) return;
                this._insertCue(cue);
                cueTimelineChanged(this._media);
            }, 1);
            method(TextTrack.prototype, 'removeCue', function (cue) {
                if (!(cue instanceof TextTrackCue)) throw new TypeError("Failed to execute 'removeCue' on 'TextTrack': parameter 1 is not of type 'TextTrackCue'.");
                if (cue._track !== this) throw domException("Failed to execute 'removeCue' on 'TextTrack': The specified cue is not listed in the TextTrack's list of cues.", 'NotFoundError');
                this._removeCue(cue, true);
            }, 1);
            TextTrack.prototype._insertCue = function (cue) {
                cue._track = this;
                cue._order = ++cueOrderCounter;
                var cues = this._cueList._cues;
                // binary search for the cue order position
                var lo = 0, hi = cues.length;
                while (lo < hi) {
                    var mid = (lo + hi) >> 1;
                    if (cueOrder(cues[mid], cue) <= 0) lo = mid + 1; else hi = mid;
                }
                cues.splice(lo, 0, cue);
                syncIndexed(this._cueList, cues);
            };
            TextTrack.prototype._removeCue = function (cue, refresh) {
                var cues = this._cueList._cues;
                var at = cues.indexOf(cue);
                if (at >= 0) cues.splice(at, 1);
                syncIndexed(this._cueList, cues);
                cue._track = null;
                if (cue._active) {
                    cue._active = false;
                    var active = this._activeList._cues;
                    var ai = active.indexOf(cue);
                    if (ai >= 0) active.splice(ai, 1);
                    syncIndexed(this._activeList, active);
                }
                if (refresh) cueTimelineChanged(this._media);
            };
            TextTrack.prototype._cueTimingChanged = function (cue) {
                var cues = this._cueList._cues;
                var at = cues.indexOf(cue);
                if (at < 0) return;
                cues.splice(at, 1);
                var lo = 0, hi = cues.length;
                while (lo < hi) {
                    var mid = (lo + hi) >> 1;
                    if (cueOrder(cues[mid], cue) <= 0) lo = mid + 1; else hi = mid;
                }
                cues.splice(lo, 0, cue);
                syncIndexed(this._cueList, cues);
                cueTimelineChanged(this._media);
            };
            TextTrack.prototype._clearCues = function () {
                var cues = this._cueList._cues;
                for (var i = 0; i < cues.length; i++) { cues[i]._track = null; cues[i]._active = false; }
                this._cueList._cues = [];
                syncIndexed(this._cueList, this._cueList._cues);
                this._activeList._cues = [];
                syncIndexed(this._activeList, this._activeList._cues);
            };
            TextTrack.prototype._syncActive = function () {
                var active = [];
                var cues = this._cueList._cues;
                for (var i = 0; i < cues.length; i++) if (cues[i]._active) active.push(cues[i]);
                this._activeList._cues = active;
                syncIndexed(this._activeList, active);
            };

            function setTrackMode(track, mode, fromScript) {
                if (track._mode === mode) return;
                var was = track._mode;
                track._mode = mode;
                var media = track._media;
                if (mode === 'disabled') {
                    // §4.8.12.5: cues stop being active without firing exit events.
                    var cues = track._cueList._cues;
                    for (var i = 0; i < cues.length; i++) cues[i]._active = false;
                    track._syncActive();
                }
                if (fromScript) track._modeSetByScript = true;
                if (media) {
                    var model = modelFor(media);
                    if (!model._changeQueued) {
                        model._changeQueued = true;
                        queueTask(media, function () {
                            model._changeQueued = false;
                            fire(model.list, 'change');
                        });
                    }
                }
                if (was === 'disabled' && track._trackElement) startTrackElementLoad(track._trackElement);
                if (mode !== 'disabled') cueTimelineChanged(media);
            }

            // ---- TextTrackList (HTML §4.8.12.4) --------------------------------------------

            function TextTrackList() { throw new TypeError('Illegal constructor'); }
            defineInterface('TextTrackList', TextTrackList);
            accessor(TextTrackList.prototype, 'length', function () { return this._tracks.length; });
            method(TextTrackList.prototype, 'item', function (index) {
                var i = toUnsignedLong(index);
                return i < this._tracks.length ? this._tracks[i] : null;
            }, 1);
            method(TextTrackList.prototype, 'getTrackById', function (id) {
                id = toDomString(id);
                for (var i = 0; i < this._tracks.length; i++) if (this._tracks[i]._id === id) return this._tracks[i];
                return null;
            }, 1);
            handlerAttribute(TextTrackList.prototype, 'onchange');
            handlerAttribute(TextTrackList.prototype, 'onaddtrack');
            handlerAttribute(TextTrackList.prototype, 'onremovetrack');
            if (typeof Symbol === 'function' && Symbol.iterator) {
                method(TextTrackList.prototype, Symbol.iterator, function () { return this._tracks.slice()[Symbol.iterator](); });
            }

            // ---- AudioTrackList / VideoTrackList (HTML §4.8.12.2) ---------------------------

            function AudioTrack() { throw new TypeError('Illegal constructor'); }
            defineInterface('AudioTrack', AudioTrack, Object);
            function VideoTrack() { throw new TypeError('Illegal constructor'); }
            defineInterface('VideoTrack', VideoTrack, Object);
            ['id', 'kind', 'label', 'language'].forEach(function (name) {
                accessor(AudioTrack.prototype, name, function () { return this['_' + name]; });
                accessor(VideoTrack.prototype, name, function () { return this['_' + name]; });
            });
            accessor(AudioTrack.prototype, 'enabled',
                function () { return this._enabled; },
                function (v) { this._enabled = !!v; });
            accessor(VideoTrack.prototype, 'selected',
                function () { return this._selected; },
                function (v) {
                    v = !!v;
                    if (v && this._list) {
                        var tracks = this._list._tracks;
                        for (var i = 0; i < tracks.length; i++) tracks[i]._selected = tracks[i] === this;
                    } else this._selected = v;
                });

            function defineMediaTrackList(name) {
                function List() { throw new TypeError('Illegal constructor'); }
                defineInterface(name, List);
                accessor(List.prototype, 'length', function () { return this._tracks.length; });
                method(List.prototype, 'getTrackById', function (id) {
                    id = toDomString(id);
                    for (var i = 0; i < this._tracks.length; i++) if (this._tracks[i]._id === id) return this._tracks[i];
                    return null;
                }, 1);
                handlerAttribute(List.prototype, 'onchange');
                handlerAttribute(List.prototype, 'onaddtrack');
                handlerAttribute(List.prototype, 'onremovetrack');
                if (typeof Symbol === 'function' && Symbol.iterator) {
                    method(List.prototype, Symbol.iterator, function () { return this._tracks.slice()[Symbol.iterator](); });
                }
                return List;
            }
            var AudioTrackList = defineMediaTrackList('AudioTrackList');
            var VideoTrackList = defineMediaTrackList('VideoTrackList');
            accessor(VideoTrackList.prototype, 'selectedIndex', function () {
                for (var i = 0; i < this._tracks.length; i++) if (this._tracks[i]._selected) return i;
                return -1;
            });

            // ---- the per-media-element model ---------------------------------------------

            var MODEL_KEY = '__fenTextTrackModel';
            function modelFor(media) {
                if (!media) return null;
                var model = media[MODEL_KEY];
                if (model) return model;
                var list = Object.create(TextTrackList.prototype);
                g.EventTarget.call(list);
                list._tracks = [];
                list._indexed = 0;
                var audio = Object.create(AudioTrackList.prototype);
                g.EventTarget.call(audio);
                audio._tracks = [];
                audio._indexed = 0;
                var video = Object.create(VideoTrackList.prototype);
                g.EventTarget.call(video);
                video._tracks = [];
                video._indexed = 0;
                model = {
                    element: media,
                    list: list,
                    audio: audio,
                    video: video,
                    lastTime: undefined,
                    _changeQueued: false,
                    _marchQueued: false,
                    _selectionQueued: false
                };
                try { Object.defineProperty(media, MODEL_KEY, { value: model, writable: true, configurable: true, enumerable: false }); }
                catch (e) { media[MODEL_KEY] = model; }
                return model;
            }
            function addTrackToList(model, track) {
                track._media = model.element;
                var tracks = model.list._tracks;
                // §4.8.12.1: track element tracks in tree order first, then addTextTrack
                // ones in creation order; both orders are the insertion order here.
                if (track._trackElement) {
                    var at = 0;
                    while (at < tracks.length && tracks[at]._trackElement) at++;
                    tracks.splice(at, 0, track);
                } else tracks.push(track);
                syncIndexed(model.list, tracks);
                queueTask(model.element, function () { fire(model.list, 'addtrack', { track: track }); });
            }
            function removeTrackFromList(model, track) {
                var tracks = model.list._tracks;
                var at = tracks.indexOf(track);
                if (at < 0) return;
                tracks.splice(at, 1);
                syncIndexed(model.list, tracks);
                track._media = null;
                queueTask(model.element, function () { fire(model.list, 'removetrack', { track: track }); });
            }

            // ---- the track element (HTML §4.8.12.11) ------------------------------------------

            var TRACK_KEY = '__fenTrackElementState';
            function trackStateFor(trackElement, create) {
                var state = trackElement[TRACK_KEY];
                if (state || !create) return state || null;
                var track = makeTextTrack(reflectKind(trackElement), trackElement.getAttribute('label') || '', trackElement.getAttribute('srclang') || '', trackElement);
                state = { element: trackElement, track: track, readyState: 0, generation: 0, loadedUrl: null };
                try { Object.defineProperty(trackElement, TRACK_KEY, { value: state, writable: true, configurable: true, enumerable: false }); }
                catch (e) { trackElement[TRACK_KEY] = state; }
                return state;
            }
            function reflectKind(trackElement) {
                var raw = trackElement.getAttribute('kind');
                if (raw === null) return 'subtitles';
                raw = raw.toLowerCase();
                return inEnum(raw, KINDS) ? raw : 'metadata';
            }
            function parentMedia(trackElement) {
                var parent = trackElement.parentNode;
                if (!parent || typeof parent.tagName !== 'string') return null;
                var tag = parent.tagName.toLowerCase();
                return (tag === 'video' || tag === 'audio') ? parent : null;
            }

            function trackElementInserted(media, trackElement) {
                var state = trackStateFor(trackElement, true);
                var model = modelFor(media);
                if (state.track._media === media) return;
                if (state.track._media) removeTrackFromList(modelFor(state.track._media), state.track);
                addTrackToList(model, state.track);
                scheduleTrackSelection(model);
                startTrackElementLoad(trackElement);
            }
            function trackElementRemoved(media, trackElement) {
                var state = trackStateFor(trackElement, false);
                if (!state || state.track._media !== media) return;
                removeTrackFromList(modelFor(media), state.track);
                // Cues of a removed track are no longer active.
                var cues = state.track._cueList._cues;
                for (var i = 0; i < cues.length; i++) cues[i]._active = false;
                state.track._syncActive();
            }
            function trackElementAttributeChanged(trackElement, name) {
                var state = trackStateFor(trackElement, true);
                if (name === 'kind') state.track._kind = reflectKind(trackElement);
                else if (name === 'label') state.track._label = trackElement.getAttribute('label') || '';
                else if (name === 'srclang') state.track._language = trackElement.getAttribute('srclang') || '';
                else if (name === 'default') { var media = parentMedia(trackElement); if (media) scheduleTrackSelection(modelFor(media)); }
                else if (name === 'src') {
                    // §4.8.12.11.3: the cues are emptied and the processing model reruns.
                    state.generation++;
                    state.track._clearCues();
                    state.readyState = 0;
                    state.loadedUrl = null;
                    var m = parentMedia(trackElement);
                    if (m) startTrackElementLoad(trackElement);
                }
            }

            // "honor user preferences for automatic text track selection", run once per
            // batch of list changes as a task: the first default subtitles/captions track
            // and the first default descriptions track show; default chapters/metadata
            // tracks become hidden. A track whose mode script has already set is left alone.
            function scheduleTrackSelection(model) {
                if (model._selectionQueued) return;
                model._selectionQueued = true;
                queueTask(model.element, function () {
                    model._selectionQueued = false;
                    var tracks = model.list._tracks;
                    var groups = { sc: [], d: [], other: [] };
                    for (var i = 0; i < tracks.length; i++) {
                        var t = tracks[i];
                        if (!t._trackElement || t._modeSetByScript) continue;
                        var k = t._kind;
                        if (k === 'subtitles' || k === 'captions') groups.sc.push(t);
                        else if (k === 'descriptions') groups.d.push(t);
                        else groups.other.push(t);
                    }
                    pickDefault(groups.sc);
                    pickDefault(groups.d);
                    for (var j = 0; j < groups.other.length; j++) {
                        var o = groups.other[j];
                        if (o._trackElement.hasAttribute('default') && o._mode === 'disabled') setTrackMode(o, 'hidden', false);
                    }
                });
            }
            function pickDefault(candidates) {
                for (var i = 0; i < candidates.length; i++) if (candidates[i]._mode === 'showing') return;
                for (var j = 0; j < candidates.length; j++) {
                    if (candidates[j]._trackElement.hasAttribute('default') && candidates[j]._mode === 'disabled') {
                        setTrackMode(candidates[j], 'showing', false);
                        return;
                    }
                }
            }

            // The track processing model: fetch when the parent is a media element and the
            // mode is not disabled; the result lands as a media element task.
            function startTrackElementLoad(trackElement) {
                var state = trackStateFor(trackElement, true);
                var media = parentMedia(trackElement);
                if (!media || state.track._media !== media) return;
                if (state.track._mode === 'disabled') return;
                if (state.readyState !== 0) return;
                var src = trackElement.getAttribute('src');
                var url = null;
                if (src !== null && src !== '') {
                    try { url = new g.URL(src, g.document.baseURI).href; } catch (e) { url = null; }
                }
                state.readyState = 1; // LOADING
                var generation = ++state.generation;
                var crossOrigin = media.getAttribute('crossorigin');
                if (crossOrigin !== null) crossOrigin = crossOrigin.toLowerCase() === 'use-credentials' ? 'use-credentials' : 'anonymous';
                if (url === null) {
                    queueTask(media, function () { finishTrackLoad(state, generation, false, ''); });
                    return;
                }
                g.__fenTrackFetch(url, crossOrigin, function (ok, text) {
                    finishTrackLoad(state, generation, ok, text);
                });
            }
            function finishTrackLoad(state, generation, ok, text) {
                if (state.generation !== generation) return;
                var parsed = null;
                if (ok) { try { parsed = JSON.parse(g.__fenVttParse(text)); } catch (e) { parsed = null; } }
                if (!parsed) {
                    state.readyState = 3; // ERROR
                    state.track._readiness = 'failed to load';
                    fire(state.element, 'error');
                    return;
                }
                var track = state.track;
                var regions = [];
                for (var r = 0; r < parsed.regions.length; r++) {
                    var rd = parsed.regions[r];
                    var region = new VTTRegion();
                    region._id = rd.id; region._width = rd.width; region._lines = rd.lines;
                    region._regionAnchorX = rd.regionAnchorX; region._regionAnchorY = rd.regionAnchorY;
                    region._viewportAnchorX = rd.viewportAnchorX; region._viewportAnchorY = rd.viewportAnchorY;
                    region._scroll = rd.scroll;
                    regions.push(region);
                }
                for (var c = 0; c < parsed.cues.length; c++) {
                    var cd = parsed.cues[c];
                    var cue = new VTTCue(cd.startTime, cd.endTime, cd.text);
                    cue._id = cd.id;
                    if (cd.region !== undefined && regions[cd.region]) cue._region = regions[cd.region];
                    cue._vertical = cd.vertical;
                    cue._snapToLines = cd.snapToLines;
                    cue._line = cd.line !== undefined ? cd.line : 'auto';
                    cue._lineAlign = cd.lineAlign;
                    cue._position = cd.position !== undefined ? cd.position : 'auto';
                    cue._positionAlign = cd.positionAlign;
                    cue._size = cd.size;
                    cue._align = cd.align;
                    track._insertCue(cue);
                }
                track._stylesheets = parsed.stylesheets || [];
                state.readyState = 2; // LOADED
                track._readiness = 'loaded';
                fire(state.element, 'load');
                cueTimelineChanged(track._media);
            }

            // ---- time marches on (HTML §4.8.12.8) ----------------------------------------------

            function mediaState(media) {
                try { return g.__fenMediaState(media); } catch (e) { return null; }
            }

            // Cues or modes changed: the active set is recomputed once the element has left
            // its poster (before the first play there is no playback position to march).
            function cueTimelineChanged(media) {
                if (!media) return;
                var model = modelFor(media);
                if (model._marchQueued) return;
                model._marchQueued = true;
                queueTask(media, function () {
                    model._marchQueued = false;
                    var s = mediaState(media);
                    if (!s || s.showPoster) return;
                    timeMarchesOn(media, false, true);
                });
            }

            function timeMarchesOn(media, monotonic, fromTimelineChange) {
                var model = modelFor(media);
                var s = mediaState(media);
                if (!s) return;
                var now = s.currentTime;
                var lastTime = monotonic ? model.lastTime : undefined;
                if (fromTimelineChange) lastTime = model.lastTime;
                model.lastTime = now;

                var tracks = model.list._tracks;
                var current = [], other = [], missed = [];
                var affected = [];
                var t, i, cue, cues;
                for (t = 0; t < tracks.length; t++) {
                    var track = tracks[t];
                    if (track._mode === 'disabled') continue;
                    cues = track._cueList._cues;
                    for (i = 0; i < cues.length; i++) {
                        cue = cues[i];
                        if (cue._startTime <= now && cue._endTime > now) current.push(cue);
                        else {
                            other.push(cue);
                            // 4. Missed: wholly between the last position and this one during normal playback.
                            if (lastTime !== undefined && monotonic && cue._startTime >= lastTime && cue._endTime <= now) missed.push(cue);
                        }
                    }
                }

                // 6. Pause-on-exit during normal playback.
                if (monotonic) {
                    for (i = 0; i < other.length; i++) {
                        cue = other[i];
                        if (cue._pauseOnExit && (cue._active || missed.indexOf(cue) >= 0)) { try { media.pause(); } catch (e) {} break; }
                    }
                }

                // 7. Nothing changed?
                var changed = missed.length > 0;
                if (!changed) {
                    for (i = 0; i < current.length; i++) if (!current[i]._active) { changed = true; break; }
                }
                if (!changed) {
                    for (i = 0; i < other.length; i++) if (other[i]._active) { changed = true; break; }
                }
                if (!changed) return;

                // 8–13. Affected tracks and the event list, sorted by time then cue order.
                var events = [];
                function noteTrack(track) { if (affected.indexOf(track) < 0) affected.push(track); }
                for (i = 0; i < missed.length; i++) {
                    cue = missed[i];
                    noteTrack(cue._track);
                    events.push({ time: cue._startTime, cue: cue, type: 'enter' });
                    events.push({ time: cue._endTime, cue: cue, type: 'exit' });
                }
                for (i = 0; i < other.length; i++) {
                    cue = other[i];
                    if (cue._active && missed.indexOf(cue) < 0) {
                        noteTrack(cue._track);
                        events.push({ time: cue._endTime, cue: cue, type: 'exit' });
                    }
                }
                for (i = 0; i < current.length; i++) {
                    cue = current[i];
                    if (!cue._active) {
                        noteTrack(cue._track);
                        events.push({ time: cue._startTime, cue: cue, type: 'enter' });
                    }
                }
                events.sort(function (a, b) {
                    if (a.time !== b.time) return a.time - b.time;
                    if (a.cue !== b.cue) return cueOrder(a.cue, b.cue);
                    return a.type === 'enter' ? -1 : 1;
                });

                // 14–16. Update the active flags, then fire the events as tasks.
                for (i = 0; i < other.length; i++) other[i]._active = false;
                for (i = 0; i < current.length; i++) current[i]._active = true;
                for (t = 0; t < affected.length; t++) affected[t]._syncActive();

                queueTask(media, function () {
                    for (var e = 0; e < events.length; e++) fire(events[e].cue, events[e].type);
                    for (var a = 0; a < affected.length; a++) {
                        var track = affected[a];
                        fire(track, 'cuechange');
                        if (track._trackElement) fire(track._trackElement, 'cuechange');
                    }
                    renderingChanged(media);
                });
            }

            function renderingChanged(media) {
                if (typeof g.__fenTextTracksRenderingChanged === 'function') g.__fenTextTracksRenderingChanged(media);
            }

            // ---- the engine's entry points ------------------------------------------------------

            g.__fenMediaTextTracks = function (media) { return modelFor(media).list; };
            g.__fenMediaAudioTracks = function (media) { return modelFor(media).audio; };
            g.__fenMediaVideoTracks = function (media) { return modelFor(media).video; };
            g.__fenMediaAddTextTrack = function (media, kind, label, language) {
                kind = toDomString(kind);
                if (!inEnum(kind, KINDS)) throw new TypeError("Failed to execute 'addTextTrack' on 'HTMLMediaElement': The provided value '" + kind + "' is not a valid enum value of type TextTrackKind.");
                var track = makeTextTrack(kind, label === undefined ? '' : toDomString(label), language === undefined ? '' : toDomString(language), null);
                track._mode = 'hidden';
                track._readiness = 'loaded';
                addTrackToList(modelFor(media), track);
                return track;
            };
            g.__fenTrackElementTrack = function (trackElement) { return trackStateFor(trackElement, true).track; };
            g.__fenTrackElementReadyState = function (trackElement) {
                var state = trackStateFor(trackElement, false);
                return state ? state.readyState : 0;
            };
            g.__fenTrackElementInserted = function (media, trackElement) { trackElementInserted(media, trackElement); };
            g.__fenTrackElementRemoved = function (media, trackElement) { trackElementRemoved(media, trackElement); };
            g.__fenTrackElementAttributeChanged = function (trackElement, name) { trackElementAttributeChanged(trackElement, name); };
            g.__fenTextTracksTimeMarchesOn = function (media, monotonic) { timeMarchesOn(media, !!monotonic, false); };
            g.__fenTextTracksReset = function (media) {
                var model = media[MODEL_KEY];
                if (!model) return;
                model.lastTime = undefined;
                var tracks = model.list._tracks;
                for (var t = 0; t < tracks.length; t++) {
                    var cues = tracks[t]._cueList._cues;
                    for (var i = 0; i < cues.length; i++) cues[i]._active = false;
                    tracks[t]._syncActive();
                }
                // In-band tracks belong to the resource that is gone.
                model.audio._tracks = []; syncIndexed(model.audio, model.audio._tracks);
                model.video._tracks = []; syncIndexed(model.video, model.video._tracks);
                renderingChanged(media);
            };
            g.__fenMediaInbandTracks = function (media, json) {
                var model = modelFor(media);
                var tracks;
                try { tracks = JSON.parse(json); } catch (e) { tracks = []; }
                model.audio._tracks = [];
                model.video._tracks = [];
                for (var i = 0; i < tracks.length; i++) {
                    var d = tracks[i];
                    var isAudio = d.kind === 'audio';
                    var track = Object.create((isAudio ? AudioTrack : VideoTrack).prototype);
                    track._id = d.id; track._kind = d.trackKind || (isAudio ? 'main' : 'main');
                    track._label = d.label || ''; track._language = d.language || '';
                    track._list = isAudio ? model.audio : model.video;
                    if (isAudio) { track._enabled = d.enabled !== false; model.audio._tracks.push(track); }
                    else { track._selected = d.selected !== false; model.video._tracks.push(track); }
                }
                syncIndexed(model.audio, model.audio._tracks);
                syncIndexed(model.video, model.video._tracks);
            };
            // The rendering side asks for the cues to show: every active cue of every showing
            // track, in cue order, as plain records.
            g.__fenTextTracksShowing = function (media) {
                var model = media[MODEL_KEY];
                if (!model) return null;
                var out = [];
                var tracks = model.list._tracks;
                for (var t = 0; t < tracks.length; t++) {
                    var track = tracks[t];
                    if (track._mode !== 'showing') continue;
                    if (track._kind !== 'subtitles' && track._kind !== 'captions') continue;
                    var cues = track._activeList._cues;
                    for (var i = 0; i < cues.length; i++) {
                        var cue = cues[i];
                        var tree;
                        try { tree = g.__fenVttCueTree(cue._text, track._language); } catch (e) { tree = null; }
                        out.push({
                            text: cue._text, tree: tree, vertical: cue._vertical, snapToLines: cue._snapToLines,
                            line: cue._line, lineAlign: cue._lineAlign, position: cue._position,
                            positionAlign: cue._positionAlign, size: cue._size, align: cue._align,
                            region: cue._region ? {
                                width: cue._region._width, lines: cue._region._lines,
                                regionAnchorX: cue._region._regionAnchorX, regionAnchorY: cue._region._regionAnchorY,
                                viewportAnchorX: cue._region._viewportAnchorX, viewportAnchorY: cue._region._viewportAnchorY,
                                scroll: cue._region._scroll
                            } : null
                        });
                    }
                }
                return JSON.stringify(out);
            };

            constants(g.HTMLTrackElement || function () {}, { NONE: 0, LOADING: 1, LOADED: 2, ERROR: 3 });
        })();
        """;
}
