using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The Media Session Standard (https://w3c.github.io/mediasession/): <c>navigator.mediaSession</c>
/// with <c>metadata</c>, <c>playbackState</c>, <c>setActionHandler()</c>, <c>setPositionState()</c>,
/// <c>setMicrophoneActive()</c> and <c>setCameraActive()</c>, and the <c>MediaMetadata</c>
/// interface with its frozen artwork and chapter lists. The session is the page's side of
/// the contract; no platform media-control surface (SMTC, MPRIS) is attached yet, so
/// action handlers are stored but only ever invoked by script.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private void InstallFenJsMediaSession()
    {
        try
        {
            EvaluateWithFenJsRaw(MediaSessionPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] media session prelude failed: {ex.Message}", LogCategory.JavaScript);
        }
    }

    private const string MediaSessionPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.MediaSession === 'function') return;

            function defineInterface(name, ctor) {
                Object.defineProperty(ctor.prototype, 'constructor', { value: ctor, writable: true, configurable: true });
                Object.defineProperty(ctor.prototype, Symbol.toStringTag, { value: name, configurable: true });
                Object.defineProperty(ctor, 'name', { value: name, configurable: true });
                g[name] = ctor;
            }
            function accessor(proto, name, get, set) {
                Object.defineProperty(proto, name, { get: get, set: set, enumerable: true, configurable: true });
            }
            function method(proto, name, fn) {
                Object.defineProperty(proto, name, { value: fn, writable: true, enumerable: true, configurable: true });
            }
            function toDomString(v) { return v === undefined ? '' : String(v); }
            function toDouble(v, what) {
                var n = Number(v);
                if (!isFinite(n)) throw new TypeError(what + ' is not a finite floating-point value.');
                return n;
            }
            function requireDictionary(v, what) {
                if (v === undefined || v === null) return {};
                if (typeof v !== 'object' && typeof v !== 'function') throw new TypeError(what + ': The provided value is not of type dictionary.');
                return v;
            }
            function toSequence(v, what) {
                if (v === undefined) return [];
                if (v === null || typeof v[Symbol.iterator] !== 'function') throw new TypeError(what + ': The provided value cannot be converted to a sequence.');
                return Array.from(v);
            }
            // MediaImage: src is parsed against the document base URL, the rest are strings;
            // the record is frozen and carries only the three members.
            function toImage(init) {
                init = requireDictionary(init, 'MediaImage');
                if (init.src === undefined) throw new TypeError("Failed to read the 'src' property from 'MediaImage': Required member is undefined.");
                var src;
                try { src = new g.URL(toDomString(init.src), g.document.baseURI).href; }
                catch (e) { throw new TypeError("Failed to construct 'MediaMetadata': '" + toDomString(init.src) + "' can't be resolved to a valid URL."); }
                return Object.freeze({ src: src, sizes: toDomString(init.sizes), type: toDomString(init.type) });
            }
            function toImages(v, what) { return Object.freeze(toSequence(v, what).map(toImage)); }
            function toChapter(init) {
                init = requireDictionary(init, 'ChapterInformation');
                return Object.freeze({
                    title: toDomString(init.title),
                    startTime: init.startTime === undefined ? 0 : toDouble(init.startTime, 'startTime'),
                    artwork: toImages(init.artwork, 'ChapterInformation artwork')
                });
            }

            function MediaMetadata(init) {
                if (!(this instanceof MediaMetadata)) throw new TypeError("Failed to construct 'MediaMetadata': Please use the 'new' operator.");
                init = requireDictionary(init, "Failed to construct 'MediaMetadata'");
                this._title = toDomString(init.title);
                this._artist = toDomString(init.artist);
                this._album = toDomString(init.album);
                this._artwork = toImages(init.artwork, "Failed to construct 'MediaMetadata': artwork");
                this._chapterInfo = Object.freeze(toSequence(init.chapterInfo, "Failed to construct 'MediaMetadata': chapterInfo").map(toChapter));
            }
            defineInterface('MediaMetadata', MediaMetadata);
            accessor(MediaMetadata.prototype, 'title', function () { return this._title; }, function (v) { this._title = toDomString(v); });
            accessor(MediaMetadata.prototype, 'artist', function () { return this._artist; }, function (v) { this._artist = toDomString(v); });
            accessor(MediaMetadata.prototype, 'album', function () { return this._album; }, function (v) { this._album = toDomString(v); });
            accessor(MediaMetadata.prototype, 'artwork', function () { return this._artwork; }, function (v) { this._artwork = toImages(v, "Failed to set the 'artwork' property on 'MediaMetadata'"); });
            // chapterInfo is read-only (FrozenArray, no setter).
            accessor(MediaMetadata.prototype, 'chapterInfo', function () { return this._chapterInfo; });

            var ACTIONS = ['play', 'pause', 'seekbackward', 'seekforward', 'previoustrack', 'nexttrack', 'skipad', 'stop', 'seekto',
                'togglemicrophone', 'togglecamera', 'togglescreenshare', 'hangup', 'previousslide', 'nextslide', 'enterpictureinpicture', 'voiceactivity'];
            var STATES = ['none', 'paused', 'playing'];

            function MediaSession() { throw new TypeError('Illegal constructor'); }
            defineInterface('MediaSession', MediaSession);
            accessor(MediaSession.prototype, 'metadata',
                function () { return this._metadata; },
                function (v) {
                    if (v !== null && !(v instanceof MediaMetadata)) throw new TypeError("Failed to set the 'metadata' property on 'MediaSession': The provided value is not of type 'MediaMetadata'.");
                    this._metadata = v;
                });
            accessor(MediaSession.prototype, 'playbackState',
                function () { return this._playbackState; },
                function (v) { v = toDomString(v); if (STATES.indexOf(v) >= 0) this._playbackState = v; });
            method(MediaSession.prototype, 'setActionHandler', function (action, handler) {
                action = toDomString(action);
                if (ACTIONS.indexOf(action) < 0) throw new TypeError("Failed to execute 'setActionHandler' on 'MediaSession': The provided value '" + action + "' is not a valid enum value of type MediaSessionAction.");
                if (handler !== null && handler !== undefined && typeof handler !== 'function') throw new TypeError("Failed to execute 'setActionHandler' on 'MediaSession': parameter 2 is not of type 'Function'.");
                if (handler) this._handlers[action] = handler; else delete this._handlers[action];
            });
            method(MediaSession.prototype, 'setPositionState', function (state) {
                if (state === undefined || state === null) { this._positionState = null; return; }
                state = requireDictionary(state, "Failed to execute 'setPositionState' on 'MediaSession'");
                if (Object.keys(state).length === 0) { this._positionState = null; return; }
                var duration = state.duration === undefined ? undefined : Number(state.duration);
                if (duration === undefined || duration !== duration || duration < 0) throw new TypeError("Failed to execute 'setPositionState' on 'MediaSession': The provided duration cannot be less than zero.");
                var position = state.position === undefined ? 0 : toDouble(state.position, 'position');
                if (position < 0 || position > duration) throw new TypeError("Failed to execute 'setPositionState' on 'MediaSession': The provided position cannot be less than zero or greater than duration.");
                var rate = state.playbackRate === undefined ? 1 : toDouble(state.playbackRate, 'playbackRate');
                if (rate === 0) throw new TypeError("Failed to execute 'setPositionState' on 'MediaSession': The provided playbackRate cannot be equal to zero.");
                this._positionState = { duration: duration, position: position, playbackRate: rate, at: (g.performance && g.performance.now ? g.performance.now() : Date.now()) };
            });
            method(MediaSession.prototype, 'setMicrophoneActive', function (active) { this._microphoneActive = !!active; });
            method(MediaSession.prototype, 'setCameraActive', function (active) { this._cameraActive = !!active; });

            var session = Object.create(MediaSession.prototype);
            session._metadata = null;
            session._playbackState = 'none';
            session._handlers = Object.create(null);
            session._positionState = null;
            session._microphoneActive = false;
            session._cameraActive = false;

            // The engine's hook for a platform media control surface: runs the page's handler
            // for an action with the MediaSessionActionDetails it is given.
            g.__fenMediaSessionAction = function (action, details) {
                var handler = session._handlers[action];
                if (typeof handler !== 'function') return false;
                var d = Object.assign({ action: action }, details || {});
                try { handler.call(session, d); } catch (e) {}
                return true;
            };

            var installed = false;
            if (typeof g.Navigator === 'function' && g.Navigator.prototype) {
                try {
                    Object.defineProperty(g.Navigator.prototype, 'mediaSession', { get: function () { return session; }, enumerable: true, configurable: true });
                    installed = g.navigator && g.navigator.mediaSession === session;
                } catch (e) { installed = false; }
            }
            if (!installed && g.navigator) {
                try { Object.defineProperty(g.navigator, 'mediaSession', { value: session, enumerable: true, configurable: true }); }
                catch (e) { try { g.navigator.mediaSession = session; } catch (e2) {} }
            }
        })();
        """;
}
