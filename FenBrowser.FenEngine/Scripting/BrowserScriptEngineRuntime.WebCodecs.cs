using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// WebCodecs (https://w3c.github.io/webcodecs/). This part carries the value types that
/// move data between the page and the codecs - the encoded chunks and the colour space -
/// which are plain data and live entirely in the realm. The decoders themselves are in
/// <c>BrowserScriptEngineRuntime.WebCodecsDecoders.cs</c>, over the same demuxer and
/// decoder registries the media element uses.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private void InstallFenJsWebCodecs()
    {
        try
        {
            EvaluateWithFenJsRaw(WebCodecsPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] webcodecs prelude failed: {ex.Message}", LogCategory.JavaScript);
        }

        InstallFenJsWebCodecsDecoders();
        InstallFenJsWebCodecsFrames();
        InstallFenJsWebCodecsControl();
    }

    private const string WebCodecsPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.EncodedVideoChunk === 'function') return;

            function defineInterface(name, ctor) {
                Object.defineProperty(ctor.prototype, 'constructor', { value: ctor, writable: true, configurable: true });
                Object.defineProperty(ctor.prototype, Symbol.toStringTag, { value: name, configurable: true });
                Object.defineProperty(ctor, 'name', { value: name, configurable: true });
                g[name] = ctor;
            }
            function accessor(proto, name, get) {
                Object.defineProperty(proto, name, { get: get, enumerable: true, configurable: true });
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
            function toEnum(value, values, member, what) {
                if (value === undefined) return null;
                var s = String(value);
                if (values.indexOf(s) < 0) {
                    throw new TypeError("Failed to read the '" + member + "' property from '" + what +
                        "': The provided value '" + s + "' is not a valid enum value.");
                }
                return s;
            }
            function toLongLong(value, member, what) {
                var n = Number(required(value, member, what));
                if (!isFinite(n)) throw new TypeError(what + '.' + member + ' is not a finite value.');
                return Math.trunc(n);
            }

            // A BufferSource as bytes. A detached buffer reads as empty, which is what the
            // callers' size checks then reject.
            // A transferred buffer is detached: it still answers as an ArrayBuffer but holds
            // nothing, and a view over it covers nothing. Either way it cannot be read from
            // or written to, so it is a TypeError wherever a BufferSource is wanted.
            function isDetached(source) {
                if (source instanceof ArrayBuffer) return source.byteLength === 0 && !source.__fenEmptyOnPurpose;
                return ArrayBuffer.isView(source) && source.buffer instanceof ArrayBuffer &&
                    source.buffer.byteLength === 0 && source.byteLength > 0;
            }
            function bytesOf(source, what) {
                if (source === null || typeof source !== 'object') {
                    throw new TypeError(what + ': The provided value is not of type BufferSource.');
                }
                if (source instanceof ArrayBuffer) {
                    if (isDetached(source)) throw new TypeError(what + ': the buffer is detached.');
                    return new Uint8Array(source.slice(0));
                }
                if (ArrayBuffer.isView(source)) {
                    if (isDetached(source)) throw new TypeError(what + ': the buffer is detached.');
                    return new Uint8Array(source.buffer.slice(source.byteOffset, source.byteOffset + source.byteLength));
                }
                throw new TypeError(what + ': The provided value is not of type BufferSource.');
            }
            function viewOf(destination, what) {
                if (destination === null || typeof destination !== 'object') {
                    throw new TypeError(what + ': The provided value is not of type BufferSource.');
                }
                if (destination instanceof ArrayBuffer) return new Uint8Array(destination);
                if (ArrayBuffer.isView(destination)) {
                    if (destination.buffer instanceof ArrayBuffer && destination.buffer.byteLength === 0 && destination.byteLength > 0) {
                        throw new TypeError(what + ': the destination buffer is detached.');
                    }
                    return new Uint8Array(destination.buffer, destination.byteOffset, destination.byteLength);
                }
                throw new TypeError(what + ': The provided value is not of type BufferSource.');
            }
            g.__fenWebCodecsBytes = bytesOf;
            g.__fenWebCodecsView = viewOf;
            g.__fenWebCodecsDetached = isDetached;

            // §9.1 EncodedAudioChunk / §9.2 EncodedVideoChunk: immutable encoded data with
            // its type and timing.
            function makeChunk(name, types) {
                function Chunk(init) {
                    if (!(this instanceof Chunk)) throw new TypeError("Failed to construct '" + name + "': Please use the 'new' operator.");
                    init = dictionary(init, name + 'Init');
                    var type = toEnum(required(init.type, 'type', name + 'Init'), types, 'type', name + 'Init');
                    var timestamp = toLongLong(init.timestamp, 'timestamp', name + 'Init');
                    var duration = init.duration === undefined || init.duration === null
                        ? null
                        : toLongLong(init.duration, 'duration', name + 'Init');
                    var data = bytesOf(required(init.data, 'data', name + 'Init'), "Failed to construct '" + name + "'");
                    this._type = type;
                    this._timestamp = timestamp;
                    this._duration = duration;
                    this._data = data;
                }
                defineInterface(name, Chunk);
                accessor(Chunk.prototype, 'type', function () { return this._type; });
                accessor(Chunk.prototype, 'timestamp', function () { return this._timestamp; });
                accessor(Chunk.prototype, 'duration', function () { return this._duration; });
                accessor(Chunk.prototype, 'byteLength', function () { return this._data.length; });
                method(Chunk.prototype, 'copyTo', function (destination) {
                    var view = viewOf(destination, "Failed to execute 'copyTo' on '" + name + "'");
                    if (view.byteLength < this._data.length) {
                        throw new TypeError("Failed to execute 'copyTo' on '" + name + "': destination is not large enough.");
                    }
                    view.set(this._data);
                });
                return Chunk;
            }

            makeChunk('EncodedAudioChunk', ['key', 'delta']);
            makeChunk('EncodedVideoChunk', ['key', 'delta']);

            // §9.5 VideoColorSpace.
            var PRIMARIES = ['bt709', 'bt470bg', 'smpte170m', 'bt2020', 'smpte432'];
            var TRANSFERS = ['bt709', 'smpte170m', 'iec61966-2-1', 'linear', 'pq', 'hlg'];
            var MATRICES = ['rgb', 'bt709', 'bt470bg', 'smpte170m', 'bt2020-ncl'];

            function VideoColorSpace(init) {
                if (!(this instanceof VideoColorSpace)) throw new TypeError("Failed to construct 'VideoColorSpace': Please use the 'new' operator.");
                init = dictionary(init, 'VideoColorSpaceInit');
                this._primaries = toEnum(init.primaries, PRIMARIES, 'primaries', 'VideoColorSpaceInit');
                this._transfer = toEnum(init.transfer, TRANSFERS, 'transfer', 'VideoColorSpaceInit');
                this._matrix = toEnum(init.matrix, MATRICES, 'matrix', 'VideoColorSpaceInit');
                this._fullRange = init.fullRange === undefined || init.fullRange === null ? null : !!init.fullRange;
            }
            defineInterface('VideoColorSpace', VideoColorSpace);
            accessor(VideoColorSpace.prototype, 'primaries', function () { return this._primaries; });
            accessor(VideoColorSpace.prototype, 'transfer', function () { return this._transfer; });
            accessor(VideoColorSpace.prototype, 'matrix', function () { return this._matrix; });
            accessor(VideoColorSpace.prototype, 'fullRange', function () { return this._fullRange; });
            method(VideoColorSpace.prototype, 'toJSON', function () {
                return {
                    primaries: this._primaries,
                    transfer: this._transfer,
                    matrix: this._matrix,
                    fullRange: this._fullRange
                };
            });
            g.__fenVideoColorSpace = function (primaries, transfer, matrix, fullRange) {
                var space = Object.create(VideoColorSpace.prototype);
                space._primaries = primaries === undefined ? null : primaries;
                space._transfer = transfer === undefined ? null : transfer;
                space._matrix = matrix === undefined ? null : matrix;
                space._fullRange = fullRange === undefined ? null : fullRange;
                return space;
            };
        })();
        """;
}
