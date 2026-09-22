using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The WebCodecs media types (https://w3c.github.io/webcodecs/ §9.3-9.4): <c>AudioData</c>
/// and <c>VideoFrame</c> carrying decoded samples and pictures, and the
/// <c>AudioDecoder</c> and <c>VideoDecoder</c> control surfaces over the
/// <c>__fenCodec*</c> natives.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private void InstallFenJsWebCodecsFrames()
    {
        try
        {
            EvaluateWithFenJsRaw(WebCodecsFramesPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] webcodecs frames prelude failed: {ex.Message}", LogCategory.JavaScript);
        }
    }

    private const string WebCodecsFramesPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.AudioData === 'function') return;

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
            function toLong(value, member, what) {
                var n = Number(required(value, member, what));
                if (!isFinite(n)) throw new TypeError(what + '.' + member + ' is not a finite value.');
                return Math.trunc(n);
            }
            function invalidState(message) { return new DOMException(message, 'InvalidStateError'); }

            // ---- AudioData (§9.3) ---------------------------------------------------------
            var AUDIO_FORMATS = ['u8', 's16', 's32', 'f32', 'u8-planar', 's16-planar', 's32-planar', 'f32-planar'];
            var SAMPLE_BYTES = { 'u8': 1, 's16': 2, 's32': 4, 'f32': 4 };
            function audioSampleBytes(format) { return SAMPLE_BYTES[format.replace('-planar', '')]; }
            function isPlanar(format) { return format.indexOf('-planar') > 0; }

            function AudioData(init) {
                if (!(this instanceof AudioData)) throw new TypeError("Failed to construct 'AudioData': Please use the 'new' operator.");
                init = dictionary(init, 'AudioDataInit');
                var format = toEnum(required(init.format, 'format', 'AudioDataInit'), AUDIO_FORMATS, 'format', 'AudioDataInit');
                var sampleRate = Number(required(init.sampleRate, 'sampleRate', 'AudioDataInit'));
                var frames = toLong(init.numberOfFrames, 'numberOfFrames', 'AudioDataInit');
                var channels = toLong(init.numberOfChannels, 'numberOfChannels', 'AudioDataInit');
                var timestamp = toLong(init.timestamp, 'timestamp', 'AudioDataInit');
                var data = __fenWebCodecsBytes(required(init.data, 'data', 'AudioDataInit'), "Failed to construct 'AudioData'");
                if (!(sampleRate > 0)) throw new TypeError("Failed to construct 'AudioData': sampleRate must be greater than zero.");
                if (frames <= 0) throw new TypeError("Failed to construct 'AudioData': numberOfFrames must be greater than zero.");
                if (channels <= 0) throw new TypeError("Failed to construct 'AudioData': numberOfChannels must be greater than zero.");
                var needed = frames * channels * audioSampleBytes(format);
                if (data.length < needed) {
                    throw new TypeError("Failed to construct 'AudioData': data is too small (" + data.length + " of " + needed + " bytes).");
                }
                this._format = format;
                this._sampleRate = sampleRate;
                this._frames = frames;
                this._channels = channels;
                this._timestamp = timestamp;
                this._data = data;
            }
            defineInterface('AudioData', AudioData);
            accessor(AudioData.prototype, 'format', function () { return this._format; });
            accessor(AudioData.prototype, 'sampleRate', function () { return this._format === null ? 0 : this._sampleRate; });
            accessor(AudioData.prototype, 'numberOfFrames', function () { return this._format === null ? 0 : this._frames; });
            accessor(AudioData.prototype, 'numberOfChannels', function () { return this._format === null ? 0 : this._channels; });
            accessor(AudioData.prototype, 'timestamp', function () { return this._timestamp; });
            accessor(AudioData.prototype, 'duration', function () {
                return this._format === null ? 0 : Math.floor(this._frames / this._sampleRate * 1000000);
            });
            method(AudioData.prototype, 'close', function () {
                this._format = null;
                this._data = new Uint8Array(0);
            });
            method(AudioData.prototype, 'clone', function () {
                if (this._format === null) throw invalidState('AudioData is closed.');
                var copy = Object.create(AudioData.prototype);
                copy._format = this._format;
                copy._sampleRate = this._sampleRate;
                copy._frames = this._frames;
                copy._channels = this._channels;
                copy._timestamp = this._timestamp;
                copy._data = new Uint8Array(this._data);
                return copy;
            });
            // §9.3.5 "copy to": the source samples for one plane and frame range, in the
            // destination's sample format. A planar layout has one plane per channel; an
            // interleaved one has a single plane holding every channel.
            function audioCopyPlan(self, options, what) {
                options = dictionary(options, what);
                var planeIndex = toLong(options.planeIndex, 'planeIndex', what);
                var destFormat = options.format === undefined ? self._format : String(options.format);
                if (AUDIO_FORMATS.indexOf(destFormat) < 0) {
                    throw new TypeError(what + ": '" + destFormat + "' is not a valid AudioSampleFormat.");
                }
                var destPlanes = isPlanar(destFormat) ? self._channels : 1;
                if (planeIndex < 0 || planeIndex >= destPlanes) {
                    throw new RangeError(what + ': planeIndex is out of range.');
                }
                var frameOffset = options.frameOffset === undefined ? 0 : toLong(options.frameOffset, 'frameOffset', what);
                if (frameOffset < 0 || frameOffset >= self._frames) throw new RangeError(what + ': frameOffset is out of range.');
                var frameCount = options.frameCount === undefined ? self._frames - frameOffset : toLong(options.frameCount, 'frameCount', what);
                if (frameCount < 0 || frameOffset + frameCount > self._frames) throw new RangeError(what + ': frameCount is out of range.');
                var channels = isPlanar(destFormat) ? 1 : self._channels;
                return {
                    format: destFormat,
                    planeIndex: planeIndex,
                    frameOffset: frameOffset,
                    frameCount: frameCount,
                    bytes: frameCount * channels * audioSampleBytes(destFormat)
                };
            }
            // Every conversion goes through a signed 32-bit sample, so an integer format
            // converts to another by shifting - which is what the spec's conversions are -
            // rather than by a float round trip, which would be off by one at the edges.
            function audioReader(bytes, format) {
                var view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
                switch (format.replace('-planar', '')) {
                    case 'u8': return function (i) { return (view.getUint8(i) - 128) << 24; };
                    case 's16': return function (i) { return view.getInt16(i * 2, true) << 16; };
                    case 's32': return function (i) { return view.getInt32(i * 4, true); };
                    default: return function (i) {
                        var f = view.getFloat32(i * 4, true);
                        f = f < -1 ? -1 : (f > 1 ? 1 : f);
                        var s = Math.round(f * 2147483648);
                        return s > 2147483647 ? 2147483647 : s;
                    };
                }
            }
            function audioWriter(view, format) {
                switch (format.replace('-planar', '')) {
                    case 'u8': return function (i, v) { view.setUint8(i, (v >> 24) + 128); };
                    case 's16': return function (i, v) { view.setInt16(i * 2, v >> 16, true); };
                    case 's32': return function (i, v) { view.setInt32(i * 4, v, true); };
                    default: return function (i, v) { view.setFloat32(i * 4, v / 2147483648, true); };
                }
            }
            method(AudioData.prototype, 'allocationSize', function (options) {
                if (this._format === null) throw invalidState('AudioData is closed.');
                return audioCopyPlan(this, options, "Failed to execute 'allocationSize' on 'AudioData'").bytes;
            });
            method(AudioData.prototype, 'copyTo', function (destination, options) {
                if (this._format === null) throw invalidState('AudioData is closed.');
                var what = "Failed to execute 'copyTo' on 'AudioData'";
                var plan = audioCopyPlan(this, options, what);
                var view = __fenWebCodecsView(destination, what);
                if (view.byteLength < plan.bytes) throw new TypeError(what + ': destination is not large enough.');

                // The same sample type on both sides moves the samples as they are, whatever
                // the plane layout: a float sample keeps its exact value, which matters
                // because a float sample is not required to be inside [-1, 1].
                var sameFormat = plan.format.replace('-planar', '') === this._format.replace('-planar', '');
                var read = sameFormat
                    ? (function (bytes, format) {
                        var bytesPer = audioSampleBytes(format);
                        var src = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
                        return function (i) { return bytesPer === 1 ? src.getUint8(i) : (bytesPer === 2 ? src.getUint16(i * 2, true) : src.getUint32(i * 4, true)); };
                    })(this._data, this._format)
                    : audioReader(this._data, this._format);
                var out;
                out = new DataView(view.buffer, view.byteOffset, view.byteLength);
                var write = sameFormat
                    ? (function (view, format) {
                        var bytesPer = audioSampleBytes(format);
                        return function (i, v) { if (bytesPer === 1) view.setUint8(i, v); else if (bytesPer === 2) view.setUint16(i * 2, v, true); else view.setUint32(i * 4, v, true); };
                    })(out, plan.format)
                    : audioWriter(out, plan.format);
                var sourcePlanar = isPlanar(this._format);
                var destPlanar = isPlanar(plan.format);
                var channels = this._channels;
                var frames = this._frames;
                var at = 0;
                for (var f = 0; f < plan.frameCount; f++) {
                    var frame = plan.frameOffset + f;
                    if (destPlanar) {
                        var c = plan.planeIndex;
                        write(at++, read(sourcePlanar ? c * frames + frame : frame * channels + c));
                    } else {
                        for (var ch = 0; ch < channels; ch++) {
                            write(at++, read(sourcePlanar ? ch * frames + frame : frame * channels + ch));
                        }
                    }
                }
            });

            // ---- VideoFrame (§9.4), from a buffer ----------------------------------------
            var VIDEO_FORMATS = ['I420', 'I420A', 'I422', 'I444', 'NV12', 'RGBA', 'RGBX', 'BGRA', 'BGRX'];
            // Per format: the plane sizes as [widthDivisor, heightDivisor, bytesPerSample].
            var VIDEO_PLANES = {
                'I420': [[1, 1, 1], [2, 2, 1], [2, 2, 1]],
                'I422': [[1, 1, 1], [2, 1, 1], [2, 1, 1]],
                'I444': [[1, 1, 1], [1, 1, 1], [1, 1, 1]],
                'NV12': [[1, 1, 1], [2, 2, 2]],
                'RGBA': [[1, 1, 4]], 'RGBX': [[1, 1, 4]], 'BGRA': [[1, 1, 4]], 'BGRX': [[1, 1, 4]]
            };
            function planeGeometry(format, width, height) {
                var spec = VIDEO_PLANES[format];
                var planes = [];
                var offset = 0;
                for (var i = 0; i < spec.length; i++) {
                    var w = Math.ceil(width / spec[i][0]) * spec[i][2];
                    var h = Math.ceil(height / spec[i][1]);
                    planes.push({ offset: offset, stride: w, rows: h, size: w * h });
                    offset += w * h;
                }
                return { planes: planes, size: offset };
            }

            function VideoFrame(data, init) {
                if (!(this instanceof VideoFrame)) throw new TypeError("Failed to construct 'VideoFrame': Please use the 'new' operator.");
                if (arguments.length < 2) {
                    // Construction from a canvas, an image or another frame needs the canvas
                    // integration, which this engine does not have yet.
                    throw new (DOMException)("Failed to construct 'VideoFrame': only a BufferSource with a VideoFrameBufferInit is supported.", 'NotSupportedError');
                }
                init = dictionary(init, 'VideoFrameBufferInit');
                var format = toEnum(required(init.format, 'format', 'VideoFrameBufferInit'), VIDEO_FORMATS, 'format', 'VideoFrameBufferInit');
                if (!VIDEO_PLANES[format]) {
                    throw new DOMException("Failed to construct 'VideoFrame': the '" + format + "' format is not supported.", 'NotSupportedError');
                }
                var codedWidth = toLong(init.codedWidth, 'codedWidth', 'VideoFrameBufferInit');
                var codedHeight = toLong(init.codedHeight, 'codedHeight', 'VideoFrameBufferInit');
                var timestamp = toLong(init.timestamp, 'timestamp', 'VideoFrameBufferInit');
                if (codedWidth <= 0 || codedHeight <= 0) {
                    throw new TypeError("Failed to construct 'VideoFrame': codedWidth and codedHeight must be greater than zero.");
                }
                var bytes = __fenWebCodecsBytes(data, "Failed to construct 'VideoFrame'");
                var geometry = planeGeometry(format, codedWidth, codedHeight);
                if (bytes.length < geometry.size) {
                    throw new TypeError("Failed to construct 'VideoFrame': data is too small (" + bytes.length + " of " + geometry.size + " bytes).");
                }
                this._init(format, codedWidth, codedHeight, timestamp,
                    init.duration === undefined || init.duration === null ? null : toLong(init.duration, 'duration', 'VideoFrameBufferInit'),
                    bytes, init.colorSpace === undefined ? null : init.colorSpace,
                    init.visibleRect === undefined ? null : init.visibleRect,
                    init.displayWidth === undefined ? null : toLong(init.displayWidth, 'displayWidth', 'VideoFrameBufferInit'),
                    init.displayHeight === undefined ? null : toLong(init.displayHeight, 'displayHeight', 'VideoFrameBufferInit'));
            }
            defineInterface('VideoFrame', VideoFrame);
            method(VideoFrame.prototype, '_init', function (format, width, height, timestamp, duration, bytes, colorSpace, visibleRect, displayWidth, displayHeight) {
                this._format = format;
                this._codedWidth = width;
                this._codedHeight = height;
                this._timestamp = timestamp;
                this._duration = duration;
                this._data = bytes;
                var rect = visibleRect ? {
                    x: toLong(visibleRect.x === undefined ? 0 : visibleRect.x, 'x', 'DOMRectInit'),
                    y: toLong(visibleRect.y === undefined ? 0 : visibleRect.y, 'y', 'DOMRectInit'),
                    width: toLong(required(visibleRect.width, 'width', 'DOMRectInit'), 'width', 'DOMRectInit'),
                    height: toLong(required(visibleRect.height, 'height', 'DOMRectInit'), 'height', 'DOMRectInit')
                } : { x: 0, y: 0, width: width, height: height };
                if (rect.x < 0 || rect.y < 0 || rect.width <= 0 || rect.height <= 0 ||
                    rect.x + rect.width > width || rect.y + rect.height > height) {
                    throw new TypeError("VideoFrame: visibleRect is outside the coded picture.");
                }
                this._visibleRect = rect;
                this._displayWidth = displayWidth === null ? rect.width : displayWidth;
                this._displayHeight = displayHeight === null ? rect.height : displayHeight;
                this._colorSpace = colorSpace && typeof colorSpace === 'object'
                    ? __fenVideoColorSpace(colorSpace.primaries, colorSpace.transfer, colorSpace.matrix, colorSpace.fullRange)
                    : __fenVideoColorSpace();
            });
            function rectOf(r) { return { x: r.x, y: r.y, width: r.width, height: r.height, top: r.y, left: r.x, right: r.x + r.width, bottom: r.y + r.height }; }
            accessor(VideoFrame.prototype, 'format', function () { return this._format; });
            accessor(VideoFrame.prototype, 'codedWidth', function () { return this._format === null ? 0 : this._codedWidth; });
            accessor(VideoFrame.prototype, 'codedHeight', function () { return this._format === null ? 0 : this._codedHeight; });
            accessor(VideoFrame.prototype, 'codedRect', function () {
                return this._format === null ? null : rectOf({ x: 0, y: 0, width: this._codedWidth, height: this._codedHeight });
            });
            accessor(VideoFrame.prototype, 'visibleRect', function () { return this._format === null ? null : rectOf(this._visibleRect); });
            accessor(VideoFrame.prototype, 'displayWidth', function () { return this._format === null ? 0 : this._displayWidth; });
            accessor(VideoFrame.prototype, 'displayHeight', function () { return this._format === null ? 0 : this._displayHeight; });
            accessor(VideoFrame.prototype, 'timestamp', function () { return this._timestamp; });
            accessor(VideoFrame.prototype, 'duration', function () { return this._duration; });
            accessor(VideoFrame.prototype, 'colorSpace', function () { return this._colorSpace; });
            method(VideoFrame.prototype, 'close', function () {
                this._format = null;
                this._data = new Uint8Array(0);
            });
            method(VideoFrame.prototype, 'clone', function () {
                if (this._format === null) throw invalidState('VideoFrame is closed.');
                var copy = Object.create(VideoFrame.prototype);
                copy._format = this._format;
                copy._codedWidth = this._codedWidth;
                copy._codedHeight = this._codedHeight;
                copy._timestamp = this._timestamp;
                copy._duration = this._duration;
                copy._data = new Uint8Array(this._data);
                copy._visibleRect = this._visibleRect;
                copy._displayWidth = this._displayWidth;
                copy._displayHeight = this._displayHeight;
                copy._colorSpace = this._colorSpace;
                return copy;
            });
            method(VideoFrame.prototype, 'allocationSize', function () {
                if (this._format === null) throw invalidState('VideoFrame is closed.');
                return planeGeometry(this._format, this._codedWidth, this._codedHeight).size;
            });
            method(VideoFrame.prototype, 'copyTo', function (destination) {
                if (this._format === null) throw invalidState('VideoFrame is closed.');
                var what = "Failed to execute 'copyTo' on 'VideoFrame'";
                var geometry = planeGeometry(this._format, this._codedWidth, this._codedHeight);
                var view = __fenWebCodecsView(destination, what);
                if (view.byteLength < geometry.size) {
                    return Promise.reject(new TypeError(what + ': destination is not large enough.'));
                }
                view.set(this._data.subarray(0, geometry.size));
                var layout = geometry.planes.map(function (p) { return { offset: p.offset, stride: p.stride }; });
                return Promise.resolve(layout);
            });

            // The decoders hand back a picture or a block of samples as a plain object.
            g.__fenWebCodecsAudioData = function (o) {
                var data = Object.create(AudioData.prototype);
                data._format = o.format;
                data._sampleRate = o.sampleRate;
                data._frames = o.numberOfFrames;
                data._channels = o.numberOfChannels;
                data._timestamp = o.timestamp;
                data._data = o.data;
                return data;
            };
            g.__fenWebCodecsVideoFrame = function (o) {
                var frame = Object.create(VideoFrame.prototype);
                frame._init(o.format, o.codedWidth, o.codedHeight, o.timestamp, o.duration || null, o.data, null, null, null, null);
                return frame;
            };
        })();
        """;
}
