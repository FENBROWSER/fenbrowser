using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The WebCodecs decoder control surfaces (https://w3c.github.io/webcodecs/ §3.2, §3.4):
/// <c>AudioDecoder</c> and <c>VideoDecoder</c>, their state machine, control message
/// queue and callbacks. The decoding itself is one <c>__fenCodec*</c> native call per
/// message, over the engine's own decoder registry.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private void InstallFenJsWebCodecsControl()
    {
        try
        {
            EvaluateWithFenJsRaw(WebCodecsControlPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] webcodecs control prelude failed: {ex.Message}", LogCategory.JavaScript);
        }
    }

    private const string WebCodecsControlPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.VideoDecoder === 'function') return;

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
            function dictionary(value, what) {
                if (value === undefined || value === null) return {};
                if (typeof value !== 'object' && typeof value !== 'function') {
                    throw new TypeError(what + ': The provided value is not of type dictionary.');
                }
                return value;
            }
            function required(value, member, what) {
                if (value === undefined) {
                    throw new TypeError("Failed to read the '" + member + "' property from '" + what + "': Required member is undefined.");
                }
                return value;
            }
            function invalidState(message) { return new DOMException(message, 'InvalidStateError'); }
            function notSupported(message) { return new DOMException(message, 'NotSupportedError'); }

            // §3.3 / §3.5 "valid config": the codec string must be there and must not be a
            // registered codec string with leading or trailing whitespace.
            function readConfig(kind, config, what) {
                config = dictionary(config, what);
                var codec = String(required(config.codec, 'codec', what));
                // An empty codec is an invalid configuration; one padded with whitespace is
                // a well formed configuration naming a codec nothing registers, so it is
                // unsupported rather than invalid.
                if (codec.length === 0) {
                    throw new TypeError(what + ": the 'codec' member is not a valid codec string.");
                }
                var read = {
                    codec: codec,
                    // A detached description is a TypeError, which __fenWebCodecsBytes raises.
                    description: config.description === undefined ? new Uint8Array(0)
                        : __fenWebCodecsBytes(config.description, what),
                    sampleRate: 0, channels: 0, width: 0, height: 0
                };
                if (kind === 'audio') {
                    read.sampleRate = Number(required(config.sampleRate, 'sampleRate', what));
                    read.channels = Number(required(config.numberOfChannels, 'numberOfChannels', what));
                    if (!(read.sampleRate > 0) || !(read.channels > 0)) {
                        throw new TypeError(what + ': sampleRate and numberOfChannels must be greater than zero.');
                    }
                } else {
                    read.width = config.codedWidth === undefined ? 0 : Number(config.codedWidth);
                    read.height = config.codedHeight === undefined ? 0 : Number(config.codedHeight);
                    if ((config.codedWidth !== undefined && !(read.width > 0)) ||
                        (config.codedHeight !== undefined && !(read.height > 0))) {
                        throw new TypeError(what + ': codedWidth and codedHeight must be greater than zero when present.');
                    }
                }
                return read;
            }
            function cloneConfig(kind, config) {
                var copy = { codec: String(config.codec) };
                if (kind === 'audio') {
                    copy.sampleRate = Number(config.sampleRate);
                    copy.numberOfChannels = Number(config.numberOfChannels);
                } else {
                    if (config.codedWidth !== undefined) copy.codedWidth = Number(config.codedWidth);
                    if (config.codedHeight !== undefined) copy.codedHeight = Number(config.codedHeight);
                    if (config.displayAspectWidth !== undefined) copy.displayAspectWidth = Number(config.displayAspectWidth);
                    if (config.displayAspectHeight !== undefined) copy.displayAspectHeight = Number(config.displayAspectHeight);
                }
                // §3.3: the returned configuration is a copy, so the page cannot see its
                // own description object come back and change it under the decoder.
                if (config.description !== undefined) copy.description = __fenWebCodecsBytes(config.description, 'AudioDecoderConfig');
                return copy;
            }

            function makeDecoder(name, kind, chunkType, makeOutput) {
                function Decoder(init) {
                    if (!(this instanceof Decoder)) throw new TypeError("Failed to construct '" + name + "': Please use the 'new' operator.");
                    init = dictionary(init, name + 'Init');
                    var output = required(init.output, 'output', name + 'Init');
                    var error = required(init.error, 'error', name + 'Init');
                    if (typeof output !== 'function' || typeof error !== 'function') {
                        throw new TypeError("Failed to construct '" + name + "': the callbacks must be functions.");
                    }
                    this._output = output;
                    this._error = error;
                    this._state = 'unconfigured';
                    this._handle = 0;
                    this._queue = 0;
                    this._ondequeue = null;
                    this._key = false;
                    this._flushes = [];
                    this._lastError = null;
                }
                defineInterface(name, Decoder);
                accessor(Decoder.prototype, 'state', function () { return this._state; });
                accessor(Decoder.prototype, 'decodeQueueSize', function () { return this._queue; });
                accessor(Decoder.prototype, 'ondequeue',
                    function () { return this._ondequeue; },
                    function (v) { this._ondequeue = typeof v === 'function' ? v : null; });

                function fail(self, e) {
                    if (self._state === 'closed') return;
                    self._lastError = e;
                    close(self, e);
                    try { self._error.call(undefined, e); } catch (ignored) {}
                }
                // §3.2 "close": the pending flushes are rejected, with the reason when
                // there is one and AbortError when the page simply reset or closed.
                function close(self, reason) {
                    if (self._handle) { __fenCodecClose(self._handle); self._handle = 0; }
                    self._state = 'closed';
                    self._queue = 0;
                    settleFlushes(self, reason || new DOMException('The decoder was closed.', 'AbortError'));
                }
                function settleFlushes(self, reason) {
                    var pending = self._flushes;
                    self._flushes = [];
                    for (var i = 0; i < pending.length; i++) pending[i](reason);
                }
                function dequeued(self) {
                    if (self._queue > 0) self._queue--;
                    var handler = self._ondequeue;
                    if (typeof handler === 'function') {
                        try { handler.call(self, { type: 'dequeue' }); } catch (ignored) {}
                    }
                }
                function emit(self, outputs) {
                    if (!outputs) return;
                    for (var i = 0; i < outputs.length; i++) {
                        var item = makeOutput(outputs[i]);
                        try { self._output.call(undefined, item); } catch (ignored) {}
                    }
                }

                method(Decoder.prototype, 'configure', function (config) {
                    if (this._state === 'closed') throw invalidState(name + ' is closed.');
                    var read = readConfig(kind, config, "Failed to execute 'configure' on '" + name + "'");
                    if (this._handle) { __fenCodecClose(this._handle); this._handle = 0; }
                    this._state = 'configured';
                    this._key = false;
                    this._lastError = null;
                    var handle = __fenCodecConfigure(kind, read.codec, read.sampleRate, read.channels, read.width, read.height, read.description);
                    if (handle < 0) {
                        var self = this;
                        Promise.resolve().then(function () { fail(self, notSupported("The '" + read.codec + "' configuration is not supported.")); });
                        return;
                    }
                    this._handle = handle;
                });

                method(Decoder.prototype, 'decode', function (chunk) {
                    if (this._state !== 'configured') throw invalidState(name + ' is not configured.');
                    if (!(chunk instanceof g[chunkType])) {
                        throw new TypeError("Failed to execute 'decode' on '" + name + "': parameter 1 is not of type '" + chunkType + "'.");
                    }
                    // §3.4: the first chunk of a decoder that has not seen a key frame must
                    // be one, or the decoder is in an error state.
                    if (!this._key) {
                        if (chunk.type !== 'key') {
                            throw new DOMException("A key chunk is required after configure() or flush().", 'DataError');
                        }
                        this._key = true;
                    }
                    var bytes = new Uint8Array(chunk.byteLength);
                    chunk.copyTo(bytes);
                    this._queue++;
                    var self = this;
                    Promise.resolve().then(function () {
                        if (self._state !== 'configured' || !self._handle) return;
                        var outputs = __fenCodecDecode(self._handle, bytes, chunk.timestamp, chunk.duration === null ? 0 : chunk.duration, chunk.type === 'key');
                        dequeued(self);
                        if (outputs === null) {
                            fail(self, new DOMException('Decoding failed.', 'EncodingError'));
                            return;
                        }
                        emit(self, outputs);
                    });
                });

                method(Decoder.prototype, 'flush', function () {
                    if (this._state === 'closed') {
                        return Promise.reject(this._lastError || invalidState(name + ' is closed.'));
                    }
                    if (this._state !== 'configured') return Promise.reject(invalidState(name + ' is not configured.'));
                    var self = this;
                    // A flush that a reset() or close() overtakes rejects instead of
                    // resolving, so the page never sees output from before the reset.
                    return new Promise(function (resolve, reject) {
                        self._flushes.push(reject);
                        Promise.resolve().then(function () {
                            var index = self._flushes.indexOf(reject);
                            if (index < 0) return; // already settled by a reset or a close
                            self._flushes.splice(index, 1);
                            if (self._state !== 'configured' || !self._handle) {
                                reject(self._lastError || invalidState(name + ' is not configured.'));
                                return;
                            }
                            var outputs = __fenCodecFlush(self._handle);
                            if (outputs === null) {
                                var e = new DOMException('Decoding failed.', 'EncodingError');
                                fail(self, e);
                                reject(e);
                                return;
                            }
                            emit(self, outputs);
                            self._queue = 0;
                            self._key = false;
                            resolve();
                        });
                    });
                });

                method(Decoder.prototype, 'reset', function () {
                    if (this._state === 'closed') throw invalidState(name + ' is closed.');
                    if (this._handle) __fenCodecReset(this._handle);
                    this._state = 'unconfigured';
                    this._queue = 0;
                    this._key = false;
                    settleFlushes(this, new DOMException('The decoder was reset.', 'AbortError'));
                });

                method(Decoder.prototype, 'close', function () {
                    if (this._state === 'closed') throw invalidState(name + ' is closed.');
                    close(this);
                });

                Object.defineProperty(Decoder, 'isConfigSupported', {
                    value: function (config) {
                        return new Promise(function (resolve, reject) {
                            var read;
                            try { read = readConfig(kind, config, name + '.isConfigSupported'); }
                            catch (e) { reject(e); return; }
                            resolve({
                                supported: !!__fenCodecSupported(kind, read.codec, read.sampleRate, read.channels, read.width, read.height, read.description),
                                config: cloneConfig(kind, config)
                            });
                        });
                    },
                    writable: true, enumerable: true, configurable: true
                });
                return Decoder;
            }

            makeDecoder('AudioDecoder', 'audio', 'EncodedAudioChunk', function (o) { return __fenWebCodecsAudioData(o); });
            makeDecoder('VideoDecoder', 'video', 'EncodedVideoChunk', function (o) { return __fenWebCodecsVideoFrame(o); });

            // There are no encoders in this build (ADR-0001 builds libavcodec with decoders
            // only), so the encoder interfaces are deliberately absent rather than present
            // and always failing.
        })();
        """;
}
