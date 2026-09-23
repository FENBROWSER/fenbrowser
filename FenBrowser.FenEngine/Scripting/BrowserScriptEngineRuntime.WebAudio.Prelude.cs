namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The realm side of Web Audio (docs/WEB_AUDIO_DESIGN.md, WA-D1): every interface's WebIDL
/// shape, argument conversion and validation, the connection bookkeeping that disconnect()
/// checks against, AudioBuffer storage (WA-D4) and the events. Rendering happens in
/// <c>FenBrowser.Media.WebAudio</c>, reached through the <c>__fenWa*</c> natives.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private const string WebAudioPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.__fenWaCreateContext !== 'function' || typeof g.BaseAudioContext === 'function') return;

            var INTERNAL = {};
            var S = Symbol('fenWebAudio');
            var FLT_MAX = 3.4028234663852886e38;
            var contexts = new Map();

            // ---- WebIDL helpers ----------------------------------------------------------

            function webidl(ctor, name) {
                var proto = ctor.prototype;
                Object.getOwnPropertyNames(proto).forEach(function (key) {
                    if (key === 'constructor') return;
                    var d = Object.getOwnPropertyDescriptor(proto, key);
                    d.enumerable = true;
                    Object.defineProperty(proto, key, d);
                });
                Object.defineProperty(proto, Symbol.toStringTag, { value: name, configurable: true });
                Object.defineProperty(g, name, { value: ctor, writable: true, configurable: true, enumerable: false });
            }
            function domError(message, name) { return new g.DOMException(message, name); }
            function typeError(message) { return new TypeError(message); }
            function toFloat(v, what) {
                var n = Number(v);
                if (!isFinite(n)) throw typeError("The provided float value for " + what + " is non-finite.");
                var f = Math.fround(n);
                if (!isFinite(f)) throw typeError("The provided float value for " + what + " is outside the range of a float.");
                return f;
            }
            function toDouble(v, what) {
                var n = Number(v);
                if (!isFinite(n)) throw typeError("The provided double value for " + what + " is non-finite.");
                return n;
            }
            function toUnsignedLong(v) {
                var n = Number(v);
                if (!isFinite(n)) return 0;
                n = Math.trunc(n) % 4294967296;
                return n < 0 ? n + 4294967296 : n;
            }
            function toBoolean(v) { return !!v; }
            function toEnum(v, values, what) {
                var s = String(v);
                if (values.indexOf(s) < 0) throw typeError("The provided value '" + s + "' is not a valid enum value of type " + what + ".");
                return s;
            }
            function dictionary(v, what) {
                if (v === undefined || v === null) return {};
                if (typeof v !== 'object' && typeof v !== 'function') throw typeError("The provided value is not of type '" + what + "'.");
                return v;
            }
            function requireContext(context, what) {
                if (!(context instanceof BaseAudioContext)) throw typeError("Failed to construct '" + what + "': parameter 1 is not of type 'BaseAudioContext'.");
                return context;
            }
            function queueTask(fn) {
                if (typeof g.__fenQueueMediaTask === 'function') g.__fenQueueMediaTask(null, fn);
                else g.setTimeout(fn, 0);
            }
            function fire(target, event) {
                try { target.dispatchEvent(event); } catch (e) { if (typeof g.reportError === 'function') g.reportError(e); }
            }
            function handler(proto, name) {
                var key = '_fenWaOn_' + name;
                var type = name.slice(2);
                Object.defineProperty(proto, name, {
                    get: function () { return this[key] || null; },
                    set: function (v) {
                        var previous = this[key];
                        if (previous) this.removeEventListener(type, previous._fenWrapper);
                        if (typeof v === 'function' || (v !== null && typeof v === 'object')) {
                            var target = this;
                            var wrapper = function (ev) {
                                var h = target[key];
                                if (typeof h === 'function') return h.call(target, ev);
                                if (h && typeof h.handleEvent === 'function') return h.handleEvent(ev);
                            };
                            try { Object.defineProperty(v, '_fenWrapper', { value: wrapper, configurable: true }); } catch (e) {}
                            this[key] = v;
                            this.addEventListener(type, wrapper);
                        } else {
                            this[key] = null;
                        }
                    },
                    enumerable: true,
                    configurable: true,
                });
            }

            // ---- AudioParam (WA 1.6) ----------------------------------------------------

            class AudioParam {
                constructor(token, node, name, defaultValue, min, max, rate, rateFixed) {
                    if (token !== INTERNAL) throw typeError('Illegal constructor');
                    this[S] = { node: node, name: name, defaultValue: defaultValue, min: min, max: max, rate: rate, rateFixed: rateFixed };
                }
                // The current value, read back within the nominal range (WA 1.6: the
                // computed value is clamped to [minValue, maxValue]).
                get value() {
                    var s = this[S];
                    var v = g.__fenWaParam(s.node[S].ctx[S].id, s.node[S].id, s.name, 'get');
                    return v < s.min ? s.min : v > s.max ? s.max : v;
                }
                set value(v) {
                    var s = this[S];
                    var f = toFloat(v, 'value');
                    paramCall(this, 'setValue', f, 0, 0);
                }
                get automationRate() { return this[S].rate; }
                set automationRate(v) {
                    var s = this[S];
                    var rate = String(v);
                    if (rate !== 'a-rate' && rate !== 'k-rate') return;
                    if (s.rateFixed && rate !== s.rate) throw domError("The automationRate of this AudioParam cannot be changed.", 'InvalidStateError');
                    s.rate = rate;
                    g.__fenWaParam(s.node[S].ctx[S].id, s.node[S].id, s.name, 'rate', rate === 'k-rate' ? 1 : 0);
                }
                get defaultValue() { return this[S].defaultValue; }
                get minValue() { return this[S].min; }
                get maxValue() { return this[S].max; }
                setValueAtTime(value, startTime) {
                    requireArgs(arguments, 2, 'setValueAtTime', 'AudioParam');
                    var v = toFloat(value, 'value'), t = toDouble(startTime, 'startTime');
                    if (t < 0) throw new RangeError("Failed to execute 'setValueAtTime' on 'AudioParam': The start time provided (" + t + ") is less than the minimum bound (0).");
                    paramCall(this, 'setValueAtTime', v, t, 0);
                    return this;
                }
                linearRampToValueAtTime(value, endTime) {
                    requireArgs(arguments, 2, 'linearRampToValueAtTime', 'AudioParam');
                    var v = toFloat(value, 'value'), t = toDouble(endTime, 'endTime');
                    if (t < 0) throw new RangeError("Failed to execute 'linearRampToValueAtTime' on 'AudioParam': The end time provided (" + t + ") is less than the minimum bound (0).");
                    paramCall(this, 'linearRamp', v, t, 0);
                    return this;
                }
                exponentialRampToValueAtTime(value, endTime) {
                    requireArgs(arguments, 2, 'exponentialRampToValueAtTime', 'AudioParam');
                    var v = toFloat(value, 'value'), t = toDouble(endTime, 'endTime');
                    if (v === 0) throw new RangeError("Failed to execute 'exponentialRampToValueAtTime' on 'AudioParam': The float target value provided (0) should not be in the range (-1.40130e-45, 1.40130e-45).");
                    if (t < 0) throw new RangeError("Failed to execute 'exponentialRampToValueAtTime' on 'AudioParam': The end time provided (" + t + ") is less than the minimum bound (0).");
                    paramCall(this, 'exponentialRamp', v, t, 0);
                    return this;
                }
                setTargetAtTime(target, startTime, timeConstant) {
                    requireArgs(arguments, 3, 'setTargetAtTime', 'AudioParam');
                    var v = toFloat(target, 'target'), t = toDouble(startTime, 'startTime'), c = toFloat(timeConstant, 'timeConstant');
                    if (t < 0) throw new RangeError("Failed to execute 'setTargetAtTime' on 'AudioParam': The start time provided (" + t + ") is less than the minimum bound (0).");
                    if (c < 0) throw new RangeError("Failed to execute 'setTargetAtTime' on 'AudioParam': The time constant provided (" + c + ") is less than the minimum bound (0).");
                    paramCall(this, 'setTarget', v, t, c);
                    return this;
                }
                setValueCurveAtTime(values, startTime, duration) {
                    requireArgs(arguments, 3, 'setValueCurveAtTime', 'AudioParam');
                    if (values === null || typeof values !== 'object' || typeof values[Symbol.iterator] !== 'function')
                        throw typeError("Failed to execute 'setValueCurveAtTime' on 'AudioParam': The provided value cannot be converted to a sequence.");
                    var curve = [];
                    for (var x of values) curve.push(toFloat(x, 'values'));
                    var t = toDouble(startTime, 'startTime'), d = toDouble(duration, 'duration');
                    if (t < 0) throw new RangeError("Failed to execute 'setValueCurveAtTime' on 'AudioParam': The start time provided (" + t + ") is less than the minimum bound (0).");
                    if (!(d > 0)) throw new RangeError("Failed to execute 'setValueCurveAtTime' on 'AudioParam': The duration provided (" + d + ") is less than or equal to the minimum bound (0).");
                    if (curve.length < 2) throw domError("Failed to execute 'setValueCurveAtTime' on 'AudioParam': The curve length provided (" + curve.length + ") is less than the minimum bound (2).", 'InvalidStateError');
                    var s = this[S];
                    var err = g.__fenWaParamCurve(s.node[S].ctx[S].id, s.node[S].id, s.name, new Float32Array(curve), t, d);
                    if (err) throw domError("Failed to execute 'setValueCurveAtTime' on 'AudioParam': the curve overlaps another automation event.", 'NotSupportedError');
                    return this;
                }
                cancelScheduledValues(cancelTime) {
                    requireArgs(arguments, 1, 'cancelScheduledValues', 'AudioParam');
                    var t = toDouble(cancelTime, 'cancelTime');
                    if (t < 0) throw new RangeError("Failed to execute 'cancelScheduledValues' on 'AudioParam': The cancel time provided (" + t + ") is less than the minimum bound (0).");
                    paramCall(this, 'cancel', 0, t, 0);
                    return this;
                }
                cancelAndHoldAtTime(cancelTime) {
                    requireArgs(arguments, 1, 'cancelAndHoldAtTime', 'AudioParam');
                    var t = toDouble(cancelTime, 'cancelTime');
                    if (t < 0) throw new RangeError("Failed to execute 'cancelAndHoldAtTime' on 'AudioParam': The cancel time provided (" + t + ") is less than the minimum bound (0).");
                    paramCall(this, 'cancelAndHold', 0, t, 0);
                    return this;
                }
            }
            function paramCall(param, op, a, b, c) {
                var s = param[S];
                var err = g.__fenWaParam(s.node[S].ctx[S].id, s.node[S].id, s.name, op, a, b, c);
                if (err === 'NotSupportedError')
                    throw domError("Failed to execute '" + op + "' on 'AudioParam': the event overlaps a setValueCurveAtTime automation.", 'NotSupportedError');
            }
            function requireArgs(args, count, method, iface) {
                if (args.length < count)
                    throw typeError("Failed to execute '" + method + "' on '" + iface + "': " + count + " arguments required, but only " + args.length + " present.");
            }
            webidl(AudioParam, 'AudioParam');

            // ---- AudioNode (WA 1.5) ------------------------------------------------------

            var modes = ['max', 'clamped-max', 'explicit'];
            var interpretations = ['speakers', 'discrete'];

            class AudioNode extends g.EventTarget {
                constructor(token, context, kind, inputs, outputs, channelCount, mode, interpretation, arg) {
                    if (token !== INTERNAL) throw typeError('Illegal constructor');
                    super();
                    var id = g.__fenWaCreateNode(context[S].id, kind, arg === undefined ? 0 : arg);
                    this[S] = {
                        ctx: context, id: id, kind: kind, inputs: inputs, outputs: outputs,
                        channelCount: channelCount, mode: mode, interpretation: interpretation,
                        connections: [], params: {},
                    };
                }
                get context() { return this[S].ctx; }
                get numberOfInputs() { return this[S].inputs; }
                get numberOfOutputs() { return this[S].outputs; }
                get channelCount() { return this[S].channelCount; }
                set channelCount(v) {
                    var n = toUnsignedLong(v);
                    checkChannelCount(this, n);
                    this[S].channelCount = n;
                    pushChannelConfig(this);
                }
                get channelCountMode() { return this[S].mode; }
                set channelCountMode(v) {
                    var mode = String(v);
                    if (modes.indexOf(mode) < 0) return;
                    checkChannelCountMode(this, mode);
                    this[S].mode = mode;
                    pushChannelConfig(this);
                }
                get channelInterpretation() { return this[S].interpretation; }
                set channelInterpretation(v) {
                    var value = String(v);
                    if (interpretations.indexOf(value) < 0) return;
                    checkChannelInterpretation(this, value);
                    this[S].interpretation = value;
                    pushChannelConfig(this);
                }
                connect(destination, output, input) {
                    requireArgs(arguments, 1, 'connect', 'AudioNode');
                    var s = this[S];
                    var out = output === undefined ? 0 : toUnsignedLong(output);
                    if (destination instanceof AudioParam) {
                        var ps = destination[S];
                        if (ps.node[S].ctx !== s.ctx) throw domError("Failed to execute 'connect' on 'AudioNode': cannot connect to an AudioParam belonging to a different audio context.", 'InvalidAccessError');
                        if (out >= s.outputs) throw domError("Failed to execute 'connect' on 'AudioNode': output index (" + out + ") exceeds number of outputs (" + s.outputs + ").", 'IndexSizeError');
                        if (!s.connections.some(function (c) { return c.dest === destination && c.output === out; })) {
                            s.connections.push({ dest: destination, output: out, input: -1 });
                            g.__fenWaConnectParam(s.ctx[S].id, s.id, out, ps.node[S].id, ps.name);
                        }
                        return undefined;
                    }
                    if (!(destination instanceof AudioNode)) throw typeError("Failed to execute 'connect' on 'AudioNode': parameter 1 is not of type 'AudioNode'.");
                    var inp = input === undefined ? 0 : toUnsignedLong(input);
                    var d = destination[S];
                    if (d.ctx !== s.ctx) throw domError("Failed to execute 'connect' on 'AudioNode': cannot connect to an AudioNode belonging to a different audio context.", 'InvalidAccessError');
                    if (out >= s.outputs) throw domError("Failed to execute 'connect' on 'AudioNode': output index (" + out + ") exceeds number of outputs (" + s.outputs + ").", 'IndexSizeError');
                    if (inp >= d.inputs) throw domError("Failed to execute 'connect' on 'AudioNode': input index (" + inp + ") exceeds number of inputs (" + d.inputs + ").", 'IndexSizeError');
                    if (!s.connections.some(function (c) { return c.dest === destination && c.output === out && c.input === inp; })) {
                        s.connections.push({ dest: destination, output: out, input: inp });
                        g.__fenWaConnect(s.ctx[S].id, s.id, out, d.id, inp);
                    }
                    return destination;
                }
                disconnect(a, b, c) {
                    var s = this[S];
                    function drop(match) {
                        var kept = [], removed = [];
                        s.connections.forEach(function (conn) { (match(conn) ? removed : kept).push(conn); });
                        s.connections = kept;
                        removed.forEach(function (conn) {
                            if (conn.input < 0) g.__fenWaDisconnectParam(s.ctx[S].id, s.id, conn.output, conn.dest[S].node[S].id, conn.dest[S].name);
                            else g.__fenWaDisconnect(s.ctx[S].id, s.id, conn.output, conn.dest[S].id, conn.input);
                        });
                        return removed.length;
                    }
                    if (arguments.length === 0) { drop(function () { return true; }); return; }
                    if (typeof a === 'number' || (!(a instanceof AudioNode) && !(a instanceof AudioParam))) {
                        if (arguments.length === 1 && !(a instanceof AudioNode) && !(a instanceof AudioParam) && (a === null || typeof a === 'object'))
                            throw typeError("Failed to execute 'disconnect' on 'AudioNode': parameter 1 is not of type 'AudioNode'.");
                        var output = toUnsignedLong(a);
                        if (output >= s.outputs) throw domError("Failed to execute 'disconnect' on 'AudioNode': The output index provided (" + output + ") is outside the range [0, " + s.outputs + ").", 'IndexSizeError');
                        drop(function (conn) { return conn.output === output; });
                        return;
                    }
                    var dest = a;
                    var hasOutput = arguments.length >= 2 && b !== undefined;
                    var out = hasOutput ? toUnsignedLong(b) : -1;
                    if (hasOutput && out >= s.outputs) throw domError("Failed to execute 'disconnect' on 'AudioNode': The output index provided (" + out + ") is outside the range [0, " + s.outputs + ").", 'IndexSizeError');
                    if (dest instanceof AudioParam) {
                        if (drop(function (conn) { return conn.dest === dest && (!hasOutput || conn.output === out); }) === 0)
                            throw domError("Failed to execute 'disconnect' on 'AudioNode': the given AudioParam is not connected.", 'InvalidAccessError');
                        return;
                    }
                    var hasInput = arguments.length >= 3 && c !== undefined;
                    var inp = hasInput ? toUnsignedLong(c) : -1;
                    if (hasInput && inp >= dest[S].inputs) throw domError("Failed to execute 'disconnect' on 'AudioNode': The input index provided (" + inp + ") is outside the range [0, " + dest[S].inputs + ").", 'IndexSizeError');
                    if (drop(function (conn) { return conn.dest === dest && (!hasOutput || conn.output === out) && (!hasInput || conn.input === inp); }) === 0)
                        throw domError("Failed to execute 'disconnect' on 'AudioNode': the given destination is not connected.", 'InvalidAccessError');
                }
            }
            function pushChannelConfig(node) {
                var s = node[S];
                g.__fenWaChannelConfig(s.ctx[S].id, s.id, s.channelCount, modes.indexOf(s.mode), interpretations.indexOf(s.interpretation));
            }
            function checkChannelCount(node, n) {
                var s = node[S];
                if (n < 1 || n > 32) throw domError("Failed to set the 'channelCount' property on 'AudioNode': The channel count provided (" + n + ") is outside the range [1, 32].", 'NotSupportedError');
                if (s.kind === 'splitter' && n !== s.outputs) throw domError("Failed to set the 'channelCount' property on 'AudioNode': ChannelSplitterNode's channelCount cannot be changed.", 'InvalidStateError');
                if ((s.kind === 'convolver' || s.kind === 'compressor' || s.kind === 'panner') && n > 2) throw domError("Failed to set the 'channelCount' property on 'AudioNode': the channelCount of this node (" + n + ") cannot be greater than 2.", 'NotSupportedError');
                if (s.kind === 'stereopanner' && n > 2) throw domError("Failed to set the 'channelCount' property on 'AudioNode': StereoPanner's channelCount (" + n + ") cannot be greater than 2.", 'NotSupportedError');
                if (s.kind === 'merger' && n !== 1) throw domError("Failed to set the 'channelCount' property on 'AudioNode': ChannelMergerNode's channelCount must be 1.", 'InvalidStateError');
                if (s.kind === 'destination') {
                    if (s.ctx instanceof OfflineAudioContext && n !== s.channelCount) throw domError("Failed to set the 'channelCount' property on 'AudioNode': an OfflineAudioContext destination's channelCount cannot be changed.", 'InvalidStateError');
                    if (n > s.ctx[S].maxChannels) throw domError("Failed to set the 'channelCount' property on 'AudioNode': The channel count provided (" + n + ") is outside the range [1, " + s.ctx[S].maxChannels + "].", 'IndexSizeError');
                }
            }
            function checkChannelCountMode(node, mode) {
                var s = node[S];
                if ((s.kind === 'convolver' || s.kind === 'compressor' || s.kind === 'panner') && mode === 'max') throw domError("Failed to set the 'channelCountMode' property on 'AudioNode': the channelCountMode of this node cannot be 'max'.", 'NotSupportedError');
                if (s.kind === 'stereopanner' && mode === 'max') throw domError("Failed to set the 'channelCountMode' property on 'AudioNode': StereoPanner's channelCountMode cannot be set to 'max'.", 'NotSupportedError');
                if ((s.kind === 'splitter' || s.kind === 'merger') && mode !== 'explicit') throw domError("Failed to set the 'channelCountMode' property on 'AudioNode': the mode of this node cannot be changed from 'explicit'.", 'InvalidStateError');
                if (s.kind === 'destination' && s.ctx instanceof OfflineAudioContext && mode !== s.mode) throw domError("Failed to set the 'channelCountMode' property on 'AudioNode': an OfflineAudioContext destination's mode cannot be changed.", 'InvalidStateError');
            }
            function checkChannelInterpretation(node, value) {
                var s = node[S];
                if (s.kind === 'splitter' && value !== 'discrete') throw domError("Failed to set the 'channelInterpretation' property on 'AudioNode': ChannelSplitterNode's interpretation cannot be changed from 'discrete'.", 'InvalidStateError');
            }
            // AudioNodeOptions (WA 1.5.1) go through the same checks as the attributes.
            function applyNodeOptions(node, options) {
                if (options.channelCount !== undefined) node.channelCount = options.channelCount;
                if (options.channelCountMode !== undefined) {
                    var mode = toEnum(options.channelCountMode, modes, 'ChannelCountMode');
                    node.channelCountMode = mode;
                }
                if (options.channelInterpretation !== undefined) {
                    var value = toEnum(options.channelInterpretation, interpretations, 'ChannelInterpretation');
                    node.channelInterpretation = value;
                }
            }
            function makeParam(node, name, defaultValue, min, max, rate, rateFixed) {
                var p = new AudioParam(INTERNAL, node, name, defaultValue, min, max, rate, !!rateFixed);
                node[S].params[name] = p;
                return p;
            }
            webidl(AudioNode, 'AudioNode');

            // ---- AudioDestinationNode (WA 1.8) -------------------------------------------

            class AudioDestinationNode extends AudioNode {
                constructor(token, context, channels) {
                    if (token !== INTERNAL) throw typeError('Illegal constructor');
                    super(INTERNAL, context, 'destination', 1, 1, channels, 'explicit', 'speakers');
                }
                get maxChannelCount() { return this[S].ctx[S].maxChannels; }
            }
            webidl(AudioDestinationNode, 'AudioDestinationNode');

            // ---- AudioBuffer (WA 1.4) ----------------------------------------------------

            class AudioBuffer {
                constructor(options) {
                    if (arguments.length < 1) throw typeError("Failed to construct 'AudioBuffer': 1 argument required, but only 0 present.");
                    var o = dictionary(options, 'AudioBufferOptions');
                    if (o.length === undefined) throw typeError("Failed to construct 'AudioBuffer': required member length is undefined.");
                    if (o.sampleRate === undefined) throw typeError("Failed to construct 'AudioBuffer': required member sampleRate is undefined.");
                    var channels = o.numberOfChannels === undefined ? 1 : toUnsignedLong(o.numberOfChannels);
                    var length = toUnsignedLong(o.length);
                    var rate = toFloat(o.sampleRate, 'sampleRate');
                    initBuffer(this, channels, length, rate, 'construct', "'AudioBuffer'");
                }
                get sampleRate() { return this[S].sampleRate; }
                get length() { return this[S].length; }
                get duration() { return this[S].length / this[S].sampleRate; }
                get numberOfChannels() { return this[S].data.length; }
                getChannelData(channel) {
                    requireArgs(arguments, 1, 'getChannelData', 'AudioBuffer');
                    var c = toUnsignedLong(channel);
                    var s = this[S];
                    if (c >= s.data.length) throw domError("Failed to execute 'getChannelData' on 'AudioBuffer': channel index (" + c + ") exceeds number of channels (" + s.data.length + ").", 'IndexSizeError');
                    return s.data[c];
                }
                copyFromChannel(destination, channelNumber, bufferOffset) {
                    requireArgs(arguments, 2, 'copyFromChannel', 'AudioBuffer');
                    if (!(destination instanceof Float32Array)) throw typeError("Failed to execute 'copyFromChannel' on 'AudioBuffer': parameter 1 is not of type 'Float32Array'.");
                    var c = toUnsignedLong(channelNumber);
                    var offset = bufferOffset === undefined ? 0 : toUnsignedLong(bufferOffset);
                    var s = this[S];
                    if (c >= s.data.length) throw domError("Failed to execute 'copyFromChannel' on 'AudioBuffer': The channelNumber provided (" + c + ") is outside the range [0, " + s.data.length + ").", 'IndexSizeError');
                    if (offset >= s.length) return;
                    var count = Math.min(destination.length, s.length - offset);
                    destination.set(s.data[c].subarray(offset, offset + count));
                }
                copyToChannel(source, channelNumber, bufferOffset) {
                    requireArgs(arguments, 2, 'copyToChannel', 'AudioBuffer');
                    if (!(source instanceof Float32Array)) throw typeError("Failed to execute 'copyToChannel' on 'AudioBuffer': parameter 1 is not of type 'Float32Array'.");
                    var c = toUnsignedLong(channelNumber);
                    var offset = bufferOffset === undefined ? 0 : toUnsignedLong(bufferOffset);
                    var s = this[S];
                    if (c >= s.data.length) throw domError("Failed to execute 'copyToChannel' on 'AudioBuffer': The channelNumber provided (" + c + ") is outside the range [0, " + s.data.length + ").", 'IndexSizeError');
                    if (offset >= s.length) return;
                    var count = Math.min(source.length, s.length - offset);
                    s.data[c].set(source.subarray(0, count), offset);
                }
            }
            function initBuffer(buffer, channels, length, rate, verb, iface) {
                if (channels < 1 || channels > 32) throw domError("Failed to " + verb + " " + iface + ": The number of channels provided (" + channels + ") is outside the range [1, 32].", 'NotSupportedError');
                if (length < 1) throw domError("Failed to " + verb + " " + iface + ": The number of frames provided (" + length + ") is less than or equal to the minimum bound (0).", 'NotSupportedError');
                if (!(rate >= 3000 && rate <= 768000)) throw domError("Failed to " + verb + " " + iface + ": The sample rate provided (" + rate + ") is outside the range [3000, 768000].", 'NotSupportedError');
                var data = [];
                for (var i = 0; i < channels; i++) data.push(new Float32Array(length));
                buffer[S] = { sampleRate: rate, length: length, data: data };
            }
            function bufferFromChannels(channels, length, rate) {
                var b = Object.create(AudioBuffer.prototype);
                initBuffer(b, channels, length, rate, 'create', "'AudioBuffer'");
                return b;
            }
            webidl(AudioBuffer, 'AudioBuffer');

            // ---- AudioScheduledSourceNode (WA 1.3) -------------------------------------

            class AudioScheduledSourceNode extends AudioNode {
                constructor(token, context, kind, channelCount) {
                    if (token !== INTERNAL) throw typeError('Illegal constructor');
                    super(INTERNAL, context, kind, 0, 1, channelCount, 'max', 'speakers');
                    this[S].started = false;
                    // Only sources end, so only they are found again by id for 'ended'.
                    context[S].nodes.set(this[S].id, this);
                }
                start(when) {
                    var t = when === undefined ? 0 : toDouble(when, 'when');
                    startSource(this, t, 0, undefined, 'AudioScheduledSourceNode');
                }
                stop(when) {
                    var s = this[S];
                    var t = when === undefined ? 0 : toDouble(when, 'when');
                    if (!s.started) throw domError("Failed to execute 'stop' on 'AudioScheduledSourceNode': cannot call stop without calling start first.", 'InvalidStateError');
                    if (t < 0) throw new RangeError("Failed to execute 'stop' on 'AudioScheduledSourceNode': The stop time provided (" + t + ") is less than the minimum bound (0).");
                    g.__fenWaSourceStop(s.ctx[S].id, s.id, t);
                }
            }
            function startSource(node, t, offset, duration, iface) {
                var s = node[S];
                if (s.started) throw domError("Failed to execute 'start' on '" + iface + "': cannot call start more than once.", 'InvalidStateError');
                if (t < 0) throw new RangeError("Failed to execute 'start' on '" + iface + "': The start time provided (" + t + ") is less than the minimum bound (0).");
                s.started = true;
                if (s.kind === 'buffersource') pushBuffer(node);
                g.__fenWaSourceStart(s.ctx[S].id, s.id, t, offset, duration === undefined ? Infinity : duration);
            }
            handler(AudioScheduledSourceNode.prototype, 'onended');
            webidl(AudioScheduledSourceNode, 'AudioScheduledSourceNode');

            // ---- GainNode (WA 1.19) -----------------------------------------------------

            class GainNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'GainNode');
                    var o = dictionary(options, 'GainOptions');
                    super(INTERNAL, context, 'gain', 1, 1, 2, 'max', 'speakers');
                    makeParam(this, 'gain', 1, -FLT_MAX, FLT_MAX, 'a-rate');
                    applyNodeOptions(this, o);
                    if (o.gain !== undefined) this[S].params.gain.value = o.gain;
                }
                get gain() { return this[S].params.gain; }
            }
            webidl(GainNode, 'GainNode');

            // ---- ConstantSourceNode (WA 1.13) -------------------------------------------

            class ConstantSourceNode extends AudioScheduledSourceNode {
                constructor(context, options) {
                    requireContext(context, 'ConstantSourceNode');
                    var o = dictionary(options, 'ConstantSourceOptions');
                    super(INTERNAL, context, 'constant', 2);
                    makeParam(this, 'offset', 1, -FLT_MAX, FLT_MAX, 'a-rate');
                    if (o.offset !== undefined) this[S].params.offset.value = o.offset;
                }
                get offset() { return this[S].params.offset; }
            }
            webidl(ConstantSourceNode, 'ConstantSourceNode');

            // ---- AudioBufferSourceNode (WA 1.9) ----------------------------------------

            class AudioBufferSourceNode extends AudioScheduledSourceNode {
                constructor(context, options) {
                    requireContext(context, 'AudioBufferSourceNode');
                    var o = dictionary(options, 'AudioBufferSourceOptions');
                    super(INTERNAL, context, 'buffersource', 2);
                    var s = this[S];
                    s.buffer = null;
                    s.bufferSet = false;
                    s.loop = false;
                    s.loopStart = 0;
                    s.loopEnd = 0;
                    makeParam(this, 'playbackRate', 1, -FLT_MAX, FLT_MAX, 'k-rate', true);
                    makeParam(this, 'detune', 0, -FLT_MAX, FLT_MAX, 'k-rate', true);
                    if (o.buffer !== undefined && o.buffer !== null) this.buffer = o.buffer;
                    if (o.detune !== undefined) s.params.detune.value = o.detune;
                    if (o.loop !== undefined) this.loop = o.loop;
                    if (o.loopEnd !== undefined) this.loopEnd = o.loopEnd;
                    if (o.loopStart !== undefined) this.loopStart = o.loopStart;
                    if (o.playbackRate !== undefined) s.params.playbackRate.value = o.playbackRate;
                }
                get buffer() { return this[S].buffer; }
                set buffer(v) {
                    var s = this[S];
                    if (v !== null && !(v instanceof AudioBuffer)) throw typeError("Failed to set the 'buffer' property on 'AudioBufferSourceNode': The provided value is not of type 'AudioBuffer'.");
                    if (v !== null) {
                        if (s.bufferSet) throw domError("Failed to set the 'buffer' property on 'AudioBufferSourceNode': Cannot set buffer to non-null after it has been already been set to a non-null buffer.", 'InvalidStateError');
                        s.bufferSet = true;
                    }
                    s.buffer = v;
                    // WA 1.4 "acquire the content": now if already started, else at start().
                    if (s.started) pushBuffer(this);
                }
                get playbackRate() { return this[S].params.playbackRate; }
                get detune() { return this[S].params.detune; }
                get loop() { return this[S].loop; }
                set loop(v) { this[S].loop = toBoolean(v); pushLoop(this); }
                get loopStart() { return this[S].loopStart; }
                set loopStart(v) { this[S].loopStart = toDouble(v, 'loopStart'); pushLoop(this); }
                get loopEnd() { return this[S].loopEnd; }
                set loopEnd(v) { this[S].loopEnd = toDouble(v, 'loopEnd'); pushLoop(this); }
                start(when, offset, duration) {
                    var t = when === undefined ? 0 : toDouble(when, 'when');
                    var off = offset === undefined ? 0 : toDouble(offset, 'offset');
                    var dur = duration === undefined ? undefined : toDouble(duration, 'duration');
                    if (off < 0) throw new RangeError("Failed to execute 'start' on 'AudioBufferSourceNode': The offset provided (" + off + ") is less than the minimum bound (0).");
                    if (dur !== undefined && dur < 0) throw new RangeError("Failed to execute 'start' on 'AudioBufferSourceNode': The duration provided (" + dur + ") is less than the minimum bound (0).");
                    startSource(this, t, off, dur, 'AudioBufferSourceNode');
                }
            }
            function pushBuffer(node) {
                var s = node[S];
                var b = s.buffer;
                if (b === null) { g.__fenWaSetBuffer(s.ctx[S].id, s.id, null, 0); return; }
                g.__fenWaSetBuffer(s.ctx[S].id, s.id, b[S].data, b[S].sampleRate);
                acquireContent(b);
            }
            // WA 1.4 "acquire the content": getChannelData() hands out fresh copies from then
            // on, so nothing script writes through an array it already held can reach what the
            // engine took. Those arrays are left attached rather than detached: pages (and WPT)
            // keep reading them after start(), and they still hold the acquired content.
            function acquireContent(buffer) {
                var data = buffer[S].data;
                for (var c = 0; c < data.length; c++) data[c] = data[c].slice();
            }
            function pushLoop(node) {
                var s = node[S];
                g.__fenWaSetLoop(s.ctx[S].id, s.id, s.loop, s.loopStart, s.loopEnd);
            }
            webidl(AudioBufferSourceNode, 'AudioBufferSourceNode');

            // ---- ChannelSplitterNode / ChannelMergerNode (WA 1.25, 1.26) ---------------

            class ChannelSplitterNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'ChannelSplitterNode');
                    var o = dictionary(options, 'ChannelSplitterOptions');
                    var outputs = o.numberOfOutputs === undefined ? 6 : toUnsignedLong(o.numberOfOutputs);
                    if (outputs < 1 || outputs > 32) throw domError("Failed to construct 'ChannelSplitterNode': The number of outputs provided (" + outputs + ") is outside the range [1, 32].", 'IndexSizeError');
                    super(INTERNAL, context, 'splitter', 1, outputs, outputs, 'explicit', 'discrete', outputs);
                    applyNodeOptions(this, o);
                }
            }
            webidl(ChannelSplitterNode, 'ChannelSplitterNode');

            class ChannelMergerNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'ChannelMergerNode');
                    var o = dictionary(options, 'ChannelMergerOptions');
                    var inputs = o.numberOfInputs === undefined ? 6 : toUnsignedLong(o.numberOfInputs);
                    if (inputs < 1 || inputs > 32) throw domError("Failed to construct 'ChannelMergerNode': The number of inputs provided (" + inputs + ") is outside the range [1, 32].", 'IndexSizeError');
                    super(INTERNAL, context, 'merger', inputs, 1, 1, 'explicit', 'speakers', inputs);
                    applyNodeOptions(this, o);
                }
            }
            webidl(ChannelMergerNode, 'ChannelMergerNode');

            // ---- WA2: OscillatorNode, PeriodicWave (WA 1.27, 1.28) ---------------------

            var oscillatorTypes = ['sine', 'square', 'sawtooth', 'triangle', 'custom'];

            function toFloatSequence(v, what, iface) {
                if (v === null || typeof v !== 'object' || typeof v[Symbol.iterator] !== 'function')
                    throw typeError("Failed to construct '" + iface + "': The provided value for " + what + " cannot be converted to a sequence.");
                var out = [];
                for (var x of v) out.push(toFloat(x, what));
                return out;
            }
            function toDoubleSequence(v, what, iface) {
                if (v === null || typeof v !== 'object' || typeof v[Symbol.iterator] !== 'function')
                    throw typeError("Failed to execute '" + iface + "': The provided value for " + what + " cannot be converted to a sequence.");
                var out = [];
                for (var x of v) out.push(toDouble(x, what));
                return out;
            }

            class PeriodicWave {
                constructor(context, options) {
                    requireContext(context, 'PeriodicWave');
                    var o = dictionary(options, 'PeriodicWaveOptions');
                    var real = o.real === undefined ? null : toFloatSequence(o.real, 'real', 'PeriodicWave');
                    var imag = o.imag === undefined ? null : toFloatSequence(o.imag, 'imag', 'PeriodicWave');
                    initPeriodicWave(this, context, real, imag, toBoolean(o.disableNormalization), "Failed to construct 'PeriodicWave'");
                }
            }
            function initPeriodicWave(wave, context, real, imag, disable, prefix) {
                // WA 1.28.1: neither array given is a sine; one given pairs with zeros.
                if (real === null && imag === null) { real = [0, 0]; imag = [0, 1]; }
                else if (real === null) real = new Array(imag.length).fill(0);
                else if (imag === null) imag = new Array(real.length).fill(0);
                if (real.length !== imag.length) throw domError(prefix + ": real and imag have different lengths (" + real.length + ", " + imag.length + ").", 'IndexSizeError');
                if (real.length < 2) throw domError(prefix + ": the length of real (" + real.length + ") is less than the minimum bound (2).", 'IndexSizeError');
                var id = g.__fenWaCreatePeriodicWave(context[S].id, new Float32Array(real), new Float32Array(imag), disable);
                wave[S] = { ctx: context, id: id };
            }
            webidl(PeriodicWave, 'PeriodicWave');

            class OscillatorNode extends AudioScheduledSourceNode {
                constructor(context, options) {
                    requireContext(context, 'OscillatorNode');
                    var o = dictionary(options, 'OscillatorOptions');
                    var type = o.type === undefined ? 'sine' : toEnum(o.type, oscillatorTypes, 'OscillatorType');
                    if (o.periodicWave !== undefined && !(o.periodicWave instanceof PeriodicWave))
                        throw typeError("Failed to construct 'OscillatorNode': member periodicWave is not of type 'PeriodicWave'.");
                    if (type === 'custom' && o.periodicWave === undefined)
                        throw domError("Failed to construct 'OscillatorNode': A PeriodicWave must be specified if the type is set to 'custom'.", 'InvalidStateError');
                    super(INTERNAL, context, 'oscillator', 2);
                    var nyquist = context[S].sampleRate / 2;
                    makeParam(this, 'frequency', 440, -nyquist, nyquist, 'a-rate');
                    makeParam(this, 'detune', 0, -153600, 153600, 'a-rate');
                    applyNodeOptions(this, o);
                    this[S].type = 'sine';
                    if (o.periodicWave !== undefined) this.setPeriodicWave(o.periodicWave);
                    else if (type !== 'sine') this.type = type;
                    if (o.frequency !== undefined) this[S].params.frequency.value = o.frequency;
                    if (o.detune !== undefined) this[S].params.detune.value = o.detune;
                }
                get type() { return this[S].type; }
                set type(v) {
                    var type = String(v);
                    if (oscillatorTypes.indexOf(type) < 0) return;
                    if (type === 'custom') {
                        if (this[S].type === 'custom') return;
                        throw domError("Failed to set the 'type' property on 'OscillatorNode': 'type' cannot be set directly to 'custom'. Use setPeriodicWave() to create a custom Oscillator type.", 'InvalidStateError');
                    }
                    this[S].type = type;
                    g.__fenWaOscillatorType(this[S].ctx[S].id, this[S].id, oscillatorTypes.indexOf(type));
                }
                get frequency() { return this[S].params.frequency; }
                get detune() { return this[S].params.detune; }
                setPeriodicWave(periodicWave) {
                    requireArgs(arguments, 1, 'setPeriodicWave', 'OscillatorNode');
                    if (!(periodicWave instanceof PeriodicWave)) throw typeError("Failed to execute 'setPeriodicWave' on 'OscillatorNode': parameter 1 is not of type 'PeriodicWave'.");
                    this[S].type = 'custom';
                    g.__fenWaOscillatorWave(this[S].ctx[S].id, this[S].id, periodicWave[S].id);
                }
            }
            webidl(OscillatorNode, 'OscillatorNode');

            // ---- DelayNode (WA 1.17) -----------------------------------------------------

            class DelayNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'DelayNode');
                    var o = dictionary(options, 'DelayOptions');
                    var max = o.maxDelayTime === undefined ? 1 : toDouble(o.maxDelayTime, 'maxDelayTime');
                    if (!(max > 0 && max < 180)) throw domError("Failed to construct 'DelayNode': The max delay time provided (" + max + ") is outside the range (0, 180).", 'NotSupportedError');
                    super(INTERNAL, context, 'delay', 1, 1, 2, 'max', 'speakers', max);
                    makeParam(this, 'delayTime', 0, 0, max, 'a-rate');
                    applyNodeOptions(this, o);
                    if (o.delayTime !== undefined) this[S].params.delayTime.value = o.delayTime;
                }
                get delayTime() { return this[S].params.delayTime; }
            }
            webidl(DelayNode, 'DelayNode');

            // ---- BiquadFilterNode (WA 1.10) ----------------------------------------------

            var biquadTypes = ['lowpass', 'highpass', 'bandpass', 'lowshelf', 'highshelf', 'peaking', 'notch', 'allpass'];

            function checkResponseArrays(method, iface, frequencyHz, magResponse, phaseResponse) {
                [frequencyHz, magResponse, phaseResponse].forEach(function (a, i) {
                    if (!(a instanceof Float32Array)) throw typeError("Failed to execute '" + method + "' on '" + iface + "': parameter " + (i + 1) + " is not of type 'Float32Array'.");
                });
                if (magResponse.length !== frequencyHz.length || phaseResponse.length !== frequencyHz.length)
                    throw domError("Failed to execute '" + method + "' on '" + iface + "': the response arrays must be as long as the frequency array.", 'InvalidAccessError');
            }

            class BiquadFilterNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'BiquadFilterNode');
                    var o = dictionary(options, 'BiquadFilterOptions');
                    var type = o.type === undefined ? 'lowpass' : toEnum(o.type, biquadTypes, 'BiquadFilterType');
                    super(INTERNAL, context, 'biquad', 1, 1, 2, 'max', 'speakers');
                    var nyquist = context[S].sampleRate / 2;
                    makeParam(this, 'frequency', 350, 0, nyquist, 'a-rate');
                    makeParam(this, 'detune', 0, -153600, 153600, 'a-rate');
                    makeParam(this, 'Q', 1, -FLT_MAX, FLT_MAX, 'a-rate');
                    makeParam(this, 'gain', 0, -FLT_MAX, 1541.273681640625, 'a-rate');
                    applyNodeOptions(this, o);
                    this[S].type = 'lowpass';
                    if (type !== 'lowpass') this.type = type;
                    var p = this[S].params;
                    if (o.Q !== undefined) p.Q.value = o.Q;
                    if (o.detune !== undefined) p.detune.value = o.detune;
                    if (o.frequency !== undefined) p.frequency.value = o.frequency;
                    if (o.gain !== undefined) p.gain.value = o.gain;
                }
                get type() { return this[S].type; }
                set type(v) {
                    var type = String(v);
                    if (biquadTypes.indexOf(type) < 0) return;
                    this[S].type = type;
                    g.__fenWaBiquadType(this[S].ctx[S].id, this[S].id, biquadTypes.indexOf(type));
                }
                get frequency() { return this[S].params.frequency; }
                get detune() { return this[S].params.detune; }
                get Q() { return this[S].params.Q; }
                get gain() { return this[S].params.gain; }
                getFrequencyResponse(frequencyHz, magResponse, phaseResponse) {
                    requireArgs(arguments, 3, 'getFrequencyResponse', 'BiquadFilterNode');
                    checkResponseArrays('getFrequencyResponse', 'BiquadFilterNode', frequencyHz, magResponse, phaseResponse);
                    g.__fenWaBiquadResponse(this[S].ctx[S].id, this[S].id, frequencyHz, magResponse, phaseResponse, biquadTypes.indexOf(this[S].type));
                }
            }
            webidl(BiquadFilterNode, 'BiquadFilterNode');

            // ---- IIRFilterNode (WA 1.22) ---------------------------------------------------

            class IIRFilterNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'IIRFilterNode');
                    if (options === undefined || options === null) throw typeError("Failed to construct 'IIRFilterNode': 2 arguments required, but only 1 present.");
                    var o = dictionary(options, 'IIRFilterOptions');
                    if (o.feedforward === undefined) throw typeError("Failed to construct 'IIRFilterNode': required member feedforward is undefined.");
                    if (o.feedback === undefined) throw typeError("Failed to construct 'IIRFilterNode': required member feedback is undefined.");
                    var ff = toDoubleSequence(o.feedforward, 'feedforward', 'IIRFilterNode');
                    var fb = toDoubleSequence(o.feedback, 'feedback', 'IIRFilterNode');
                    checkIirCoefficients(ff, fb, "Failed to construct 'IIRFilterNode'");
                    super(INTERNAL, context, 'iir', 1, 1, 2, 'max', 'speakers');
                    this[S].feedforward = ff;
                    this[S].feedback = fb;
                    g.__fenWaIirCoefficients(context[S].id, this[S].id, new Float64Array(ff), new Float64Array(fb));
                    applyNodeOptions(this, o);
                }
                getFrequencyResponse(frequencyHz, magResponse, phaseResponse) {
                    requireArgs(arguments, 3, 'getFrequencyResponse', 'IIRFilterNode');
                    checkResponseArrays('getFrequencyResponse', 'IIRFilterNode', frequencyHz, magResponse, phaseResponse);
                    g.__fenWaIirResponse(this[S].ctx[S].id, new Float64Array(this[S].feedforward), new Float64Array(this[S].feedback), frequencyHz, magResponse, phaseResponse);
                }
            }
            function checkIirCoefficients(ff, fb, prefix) {
                if (ff.length < 1 || ff.length > 20) throw domError(prefix + ": The number of feedforward coefficients provided (" + ff.length + ") is outside the range [1, 20].", 'NotSupportedError');
                if (fb.length < 1 || fb.length > 20) throw domError(prefix + ": The number of feedback coefficients provided (" + fb.length + ") is outside the range [1, 20].", 'NotSupportedError');
                if (ff.every(function (v) { return v === 0; })) throw domError(prefix + ": At least one feedforward coefficient must be non-zero.", 'InvalidStateError');
                if (fb[0] === 0) throw domError(prefix + ": First feedback coefficient must be non-zero.", 'InvalidStateError');
            }
            webidl(IIRFilterNode, 'IIRFilterNode');

            // ---- WaveShaperNode (WA 1.31) ----------------------------------------------------

            var oversampleTypes = ['none', '2x', '4x'];

            class WaveShaperNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'WaveShaperNode');
                    var o = dictionary(options, 'WaveShaperOptions');
                    var oversample = o.oversample === undefined ? 'none' : toEnum(o.oversample, oversampleTypes, 'OverSampleType');
                    var curve = o.curve === undefined || o.curve === null ? null : toFloatSequence(o.curve, 'curve', 'WaveShaperNode');
                    super(INTERNAL, context, 'waveshaper', 1, 1, 2, 'max', 'speakers');
                    this[S].curve = null;
                    this[S].oversample = 'none';
                    applyNodeOptions(this, o);
                    if (curve !== null) this.curve = new Float32Array(curve);
                    if (oversample !== 'none') this.oversample = oversample;
                }
                get curve() { return this[S].curve; }
                set curve(v) {
                    var s = this[S];
                    if (v !== null && !(v instanceof Float32Array)) throw typeError("Failed to set the 'curve' property on 'WaveShaperNode': The provided value is not of type 'Float32Array'.");
                    if (v !== null && v.length < 2) throw domError("Failed to set the 'curve' property on 'WaveShaperNode': The curve length provided (" + v.length + ") is less than the minimum bound (2).", 'InvalidStateError');
                    if (v !== null && s.curveSet) throw domError("Failed to set the 'curve' property on 'WaveShaperNode': The curve can only be set once.", 'InvalidStateError');
                    if (v !== null) s.curveSet = true;
                    // WA 1.31: the curve is copied when it is set.
                    s.curve = v === null ? null : new Float32Array(v);
                    g.__fenWaWaveShaperCurve(s.ctx[S].id, s.id, s.curve);
                }
                get oversample() { return this[S].oversample; }
                set oversample(v) {
                    var value = String(v);
                    if (oversampleTypes.indexOf(value) < 0) return;
                    this[S].oversample = value;
                    g.__fenWaWaveShaperOversample(this[S].ctx[S].id, this[S].id, oversampleTypes.indexOf(value));
                }
            }
            webidl(WaveShaperNode, 'WaveShaperNode');

            // ---- StereoPannerNode (WA 1.29) ----------------------------------------------------

            class StereoPannerNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'StereoPannerNode');
                    var o = dictionary(options, 'StereoPannerOptions');
                    super(INTERNAL, context, 'stereopanner', 1, 1, 2, 'clamped-max', 'speakers');
                    makeParam(this, 'pan', 0, -1, 1, 'a-rate');
                    applyNodeOptions(this, o);
                    if (o.pan !== undefined) this[S].params.pan.value = o.pan;
                }
                get pan() { return this[S].params.pan; }
            }
            webidl(StereoPannerNode, 'StereoPannerNode');

            // ---- WA3: AnalyserNode (WA 1.8) -----------------------------------------------

            class AnalyserNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'AnalyserNode');
                    var o = dictionary(options, 'AnalyserOptions');
                    var fftSize = o.fftSize === undefined ? 2048 : toUnsignedLong(o.fftSize);
                    var maxDb = o.maxDecibels === undefined ? -30 : toDouble(o.maxDecibels, 'maxDecibels');
                    var minDb = o.minDecibels === undefined ? -100 : toDouble(o.minDecibels, 'minDecibels');
                    var smoothing = o.smoothingTimeConstant === undefined ? 0.8 : toDouble(o.smoothingTimeConstant, 'smoothingTimeConstant');
                    checkFftSize(fftSize, "Failed to construct 'AnalyserNode'");
                    if (minDb >= maxDb) throw domError("Failed to construct 'AnalyserNode': minDecibels (" + minDb + ") must be less than maxDecibels (" + maxDb + ").", 'IndexSizeError');
                    if (!(smoothing >= 0 && smoothing <= 1)) throw domError("Failed to construct 'AnalyserNode': The smoothing value provided (" + smoothing + ") is outside the range [0, 1].", 'IndexSizeError');
                    super(INTERNAL, context, 'analyser', 1, 1, 2, 'max', 'speakers');
                    var s = this[S];
                    s.fftSize = fftSize; s.minDb = minDb; s.maxDb = maxDb; s.smoothing = smoothing;
                    applyNodeOptions(this, o);
                }
                get fftSize() { return this[S].fftSize; }
                set fftSize(v) { var n = toUnsignedLong(v); checkFftSize(n, "Failed to set the 'fftSize' property on 'AnalyserNode'"); this[S].fftSize = n; }
                get frequencyBinCount() { return this[S].fftSize / 2; }
                get minDecibels() { return this[S].minDb; }
                set minDecibels(v) {
                    var d = toDouble(v, 'minDecibels');
                    if (d >= this[S].maxDb) throw domError("Failed to set the 'minDecibels' property on 'AnalyserNode': The minDecibels provided (" + d + ") is greater than or equal to the maxDecibels (" + this[S].maxDb + ").", 'IndexSizeError');
                    this[S].minDb = d;
                }
                get maxDecibels() { return this[S].maxDb; }
                set maxDecibels(v) {
                    var d = toDouble(v, 'maxDecibels');
                    if (d <= this[S].minDb) throw domError("Failed to set the 'maxDecibels' property on 'AnalyserNode': The maxDecibels provided (" + d + ") is less than or equal to the minDecibels (" + this[S].minDb + ").", 'IndexSizeError');
                    this[S].maxDb = d;
                }
                get smoothingTimeConstant() { return this[S].smoothing; }
                set smoothingTimeConstant(v) {
                    var d = toDouble(v, 'smoothingTimeConstant');
                    if (!(d >= 0 && d <= 1)) throw domError("Failed to set the 'smoothingTimeConstant' property on 'AnalyserNode': The smoothing value provided (" + d + ") is outside the range [0, 1].", 'IndexSizeError');
                    this[S].smoothing = d;
                }
                getFloatFrequencyData(array) { analyserRead(this, 0, array, Float32Array, 'getFloatFrequencyData', 'Float32Array'); }
                getByteFrequencyData(array) { analyserRead(this, 1, array, Uint8Array, 'getByteFrequencyData', 'Uint8Array'); }
                getFloatTimeDomainData(array) { analyserRead(this, 2, array, Float32Array, 'getFloatTimeDomainData', 'Float32Array'); }
                getByteTimeDomainData(array) { analyserRead(this, 3, array, Uint8Array, 'getByteTimeDomainData', 'Uint8Array'); }
            }
            function checkFftSize(n, prefix) {
                if (n < 32 || n > 32768 || (n & (n - 1)) !== 0)
                    throw domError(prefix + ": The value provided (" + n + ") is not a power of two in the range [32, 32768].", 'IndexSizeError');
            }
            function analyserRead(node, kind, array, type, method, typeName) {
                if (arguments.length < 3 || !(array instanceof type))
                    throw typeError("Failed to execute '" + method + "' on 'AnalyserNode': parameter 1 is not of type '" + typeName + "'.");
                var s = node[S];
                g.__fenWaAnalyserRead(s.ctx[S].id, s.id, kind, array, s.fftSize, s.minDb, s.maxDb, s.smoothing);
            }
            webidl(AnalyserNode, 'AnalyserNode');

            // ---- ConvolverNode (WA 1.14) -----------------------------------------------------

            class ConvolverNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'ConvolverNode');
                    var o = dictionary(options, 'ConvolverOptions');
                    super(INTERNAL, context, 'convolver', 1, 1, 2, 'clamped-max', 'speakers');
                    this[S].normalize = !toBoolean(o.disableNormalization);
                    this[S].buffer = null;
                    applyNodeOptions(this, o);
                    if (o.buffer !== undefined && o.buffer !== null) this.buffer = o.buffer;
                }
                get buffer() { return this[S].buffer; }
                set buffer(v) {
                    var s = this[S];
                    if (v !== null && !(v instanceof AudioBuffer)) throw typeError("Failed to set the 'buffer' property on 'ConvolverNode': The provided value is not of type 'AudioBuffer'.");
                    if (v !== null) {
                        var ch = v[S].data.length;
                        if (ch !== 1 && ch !== 2 && ch !== 4) throw domError("Failed to set the 'buffer' property on 'ConvolverNode': The buffer must have 1, 2, or 4 channels, not " + ch + ".", 'NotSupportedError');
                        if (v[S].sampleRate !== s.ctx[S].sampleRate) throw domError("Failed to set the 'buffer' property on 'ConvolverNode': The buffer sample rate " + v[S].sampleRate + " does not match the context rate " + s.ctx[S].sampleRate + ".", 'NotSupportedError');
                    }
                    s.buffer = v;
                    g.__fenWaConvolverBuffer(s.ctx[S].id, s.id, v === null ? null : v[S].data, v === null ? 0 : v[S].sampleRate, s.normalize);
                    if (v !== null) acquireContent(v);
                }
                get normalize() { return this[S].normalize; }
                set normalize(v) { this[S].normalize = toBoolean(v); }
            }
            webidl(ConvolverNode, 'ConvolverNode');

            // ---- DynamicsCompressorNode (WA 1.15) ---------------------------------------------

            class DynamicsCompressorNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'DynamicsCompressorNode');
                    var o = dictionary(options, 'DynamicsCompressorOptions');
                    super(INTERNAL, context, 'compressor', 1, 1, 2, 'clamped-max', 'speakers');
                    makeParam(this, 'threshold', -24, -100, 0, 'k-rate', true);
                    makeParam(this, 'knee', 30, 0, 40, 'k-rate', true);
                    makeParam(this, 'ratio', 12, 1, 20, 'k-rate', true);
                    makeParam(this, 'attack', 0.003, 0, 1, 'k-rate', true);
                    makeParam(this, 'release', 0.25, 0, 1, 'k-rate', true);
                    applyNodeOptions(this, o);
                    var p = this[S].params;
                    ['attack', 'knee', 'ratio', 'release', 'threshold'].forEach(function (name) {
                        if (o[name] !== undefined) p[name].value = o[name];
                    });
                }
                get threshold() { return this[S].params.threshold; }
                get knee() { return this[S].params.knee; }
                get ratio() { return this[S].params.ratio; }
                get attack() { return this[S].params.attack; }
                get release() { return this[S].params.release; }
                get reduction() { return g.__fenWaCompressorReduction(this[S].ctx[S].id, this[S].id); }
            }
            webidl(DynamicsCompressorNode, 'DynamicsCompressorNode');

            // ---- PannerNode / AudioListener (WA 1.23, 1.24) -----------------------------------

            var panningModels = ['equalpower', 'HRTF'];
            var distanceModels = ['linear', 'inverse', 'exponential'];

            class PannerNode extends AudioNode {
                constructor(context, options) {
                    requireContext(context, 'PannerNode');
                    var o = dictionary(options, 'PannerOptions');
                    super(INTERNAL, context, 'panner', 1, 1, 2, 'clamped-max', 'speakers');
                    var s = this[S];
                    s.panningModel = 'equalpower'; s.distanceModel = 'inverse';
                    s.refDistance = 1; s.maxDistance = 10000; s.rolloffFactor = 1;
                    s.coneInnerAngle = 360; s.coneOuterAngle = 360; s.coneOuterGain = 0;
                    var self = this;
                    ['positionX', 'positionY', 'positionZ'].forEach(function (n) { makeParam(self, n, 0, -FLT_MAX, FLT_MAX, 'a-rate'); });
                    makeParam(this, 'orientationX', 1, -FLT_MAX, FLT_MAX, 'a-rate');
                    makeParam(this, 'orientationY', 0, -FLT_MAX, FLT_MAX, 'a-rate');
                    makeParam(this, 'orientationZ', 0, -FLT_MAX, FLT_MAX, 'a-rate');
                    applyNodeOptions(this, o);
                    if (o.panningModel !== undefined) this.panningModel = toEnum(o.panningModel, panningModels, 'PanningModelType');
                    if (o.distanceModel !== undefined) this.distanceModel = toEnum(o.distanceModel, distanceModels, 'DistanceModelType');
                    ['positionX', 'positionY', 'positionZ', 'orientationX', 'orientationY', 'orientationZ'].forEach(function (n) {
                        if (o[n] !== undefined) s.params[n].value = o[n];
                    });
                    ['refDistance', 'maxDistance', 'rolloffFactor', 'coneInnerAngle', 'coneOuterAngle', 'coneOuterGain'].forEach(function (n) {
                        if (o[n] !== undefined) self[n] = o[n];
                    });
                    pushPanner(this);
                }
                get panningModel() { return this[S].panningModel; }
                set panningModel(v) { var m = String(v); if (panningModels.indexOf(m) < 0) return; this[S].panningModel = m; pushPanner(this); }
                get distanceModel() { return this[S].distanceModel; }
                set distanceModel(v) { var m = String(v); if (distanceModels.indexOf(m) < 0) return; this[S].distanceModel = m; pushPanner(this); }
                get positionX() { return this[S].params.positionX; }
                get positionY() { return this[S].params.positionY; }
                get positionZ() { return this[S].params.positionZ; }
                get orientationX() { return this[S].params.orientationX; }
                get orientationY() { return this[S].params.orientationY; }
                get orientationZ() { return this[S].params.orientationZ; }
                get refDistance() { return this[S].refDistance; }
                set refDistance(v) {
                    var d = toDouble(v, 'refDistance');
                    if (d < 0) throw new RangeError("Failed to set the 'refDistance' property on 'PannerNode': The provided value (" + d + ") is less than the minimum bound (0).");
                    this[S].refDistance = d; pushPanner(this);
                }
                get maxDistance() { return this[S].maxDistance; }
                set maxDistance(v) {
                    var d = toDouble(v, 'maxDistance');
                    if (!(d > 0)) throw new RangeError("Failed to set the 'maxDistance' property on 'PannerNode': The provided value (" + d + ") is less than or equal to the minimum bound (0).");
                    this[S].maxDistance = d; pushPanner(this);
                }
                get rolloffFactor() { return this[S].rolloffFactor; }
                set rolloffFactor(v) {
                    var d = toDouble(v, 'rolloffFactor');
                    if (d < 0) throw new RangeError("Failed to set the 'rolloffFactor' property on 'PannerNode': The provided value (" + d + ") is less than the minimum bound (0).");
                    this[S].rolloffFactor = d; pushPanner(this);
                }
                get coneInnerAngle() { return this[S].coneInnerAngle; }
                set coneInnerAngle(v) { this[S].coneInnerAngle = toDouble(v, 'coneInnerAngle'); pushPanner(this); }
                get coneOuterAngle() { return this[S].coneOuterAngle; }
                set coneOuterAngle(v) { this[S].coneOuterAngle = toDouble(v, 'coneOuterAngle'); pushPanner(this); }
                get coneOuterGain() { return this[S].coneOuterGain; }
                set coneOuterGain(v) {
                    var d = toDouble(v, 'coneOuterGain');
                    if (!(d >= 0 && d <= 1)) throw domError("Failed to set the 'coneOuterGain' property on 'PannerNode': The provided value (" + d + ") is outside the range [0, 1].", 'InvalidStateError');
                    this[S].coneOuterGain = d; pushPanner(this);
                }
                setPosition(x, y, z) {
                    requireArgs(arguments, 3, 'setPosition', 'PannerNode');
                    var p = this[S].params;
                    p.positionX.value = x; p.positionY.value = y; p.positionZ.value = z;
                }
                setOrientation(x, y, z) {
                    requireArgs(arguments, 3, 'setOrientation', 'PannerNode');
                    var p = this[S].params;
                    p.orientationX.value = x; p.orientationY.value = y; p.orientationZ.value = z;
                }
            }
            function pushPanner(node) {
                var s = node[S];
                g.__fenWaPannerConfig(s.ctx[S].id, s.id, panningModels.indexOf(s.panningModel), distanceModels.indexOf(s.distanceModel),
                    s.refDistance, s.maxDistance, s.rolloffFactor, s.coneInnerAngle, s.coneOuterAngle, s.coneOuterGain);
            }
            webidl(PannerNode, 'PannerNode');

            class AudioListener {
                constructor(token, context) {
                    if (token !== INTERNAL) throw typeError('Illegal constructor');
                    // The listener's params belong to a graph node of its own.
                    this[S] = { ctx: context, id: g.__fenWaListenerId(context[S].id), params: {} };
                    var self = this;
                    [['positionX', 0], ['positionY', 0], ['positionZ', 0], ['forwardX', 0], ['forwardY', 0], ['forwardZ', -1], ['upX', 0], ['upY', 1], ['upZ', 0]].forEach(function (d) {
                        makeParam(self, d[0], d[1], -FLT_MAX, FLT_MAX, 'a-rate');
                    });
                }
                get positionX() { return this[S].params.positionX; }
                get positionY() { return this[S].params.positionY; }
                get positionZ() { return this[S].params.positionZ; }
                get forwardX() { return this[S].params.forwardX; }
                get forwardY() { return this[S].params.forwardY; }
                get forwardZ() { return this[S].params.forwardZ; }
                get upX() { return this[S].params.upX; }
                get upY() { return this[S].params.upY; }
                get upZ() { return this[S].params.upZ; }
                setPosition(x, y, z) {
                    requireArgs(arguments, 3, 'setPosition', 'AudioListener');
                    var p = this[S].params;
                    p.positionX.value = x; p.positionY.value = y; p.positionZ.value = z;
                }
                setOrientation(x, y, z, xUp, yUp, zUp) {
                    requireArgs(arguments, 6, 'setOrientation', 'AudioListener');
                    var p = this[S].params;
                    p.forwardX.value = x; p.forwardY.value = y; p.forwardZ.value = z;
                    p.upX.value = xUp; p.upY.value = yUp; p.upZ.value = zUp;
                }
            }
            webidl(AudioListener, 'AudioListener');

            // ---- BaseAudioContext (WA 1.1) ----------------------------------------------

            class BaseAudioContext extends g.EventTarget {
                constructor(token) {
                    if (token !== INTERNAL) throw typeError('Illegal constructor');
                    super();
                }
                get destination() { return this[S].destination; }
                get sampleRate() { return this[S].sampleRate; }
                get currentTime() { return g.__fenWaCurrentTime(this[S].id); }
                get state() { return this[S].state; }
                get renderQuantumSize() { return this[S].quantum; }
                createBuffer(numberOfChannels, length, sampleRate) {
                    requireArgs(arguments, 3, 'createBuffer', 'BaseAudioContext');
                    var b = Object.create(AudioBuffer.prototype);
                    initBuffer(b, toUnsignedLong(numberOfChannels), toUnsignedLong(length), toFloat(sampleRate, 'sampleRate'), 'execute', "'createBuffer' on 'BaseAudioContext'");
                    return b;
                }
                createBufferSource() { return new AudioBufferSourceNode(this); }
                createConstantSource() { return new ConstantSourceNode(this); }
                createGain() { return new GainNode(this); }
                createChannelSplitter(numberOfOutputs) {
                    return new ChannelSplitterNode(this, { numberOfOutputs: numberOfOutputs === undefined ? 6 : numberOfOutputs });
                }
                createChannelMerger(numberOfInputs) {
                    return new ChannelMergerNode(this, { numberOfInputs: numberOfInputs === undefined ? 6 : numberOfInputs });
                }
                createOscillator() { return new OscillatorNode(this); }
                createPeriodicWave(real, imag, constraints) {
                    requireArgs(arguments, 2, 'createPeriodicWave', 'BaseAudioContext');
                    var c = dictionary(constraints, 'PeriodicWaveConstraints');
                    var w = Object.create(PeriodicWave.prototype);
                    initPeriodicWave(w, this, toFloatSequence(real, 'real', 'createPeriodicWave'), toFloatSequence(imag, 'imag', 'createPeriodicWave'), toBoolean(c.disableNormalization), "Failed to execute 'createPeriodicWave' on 'BaseAudioContext'");
                    return w;
                }
                createDelay(maxDelayTime) { return new DelayNode(this, { maxDelayTime: maxDelayTime === undefined ? 1 : maxDelayTime }); }
                createBiquadFilter() { return new BiquadFilterNode(this); }
                createIIRFilter(feedforward, feedback) {
                    requireArgs(arguments, 2, 'createIIRFilter', 'BaseAudioContext');
                    return new IIRFilterNode(this, { feedforward: feedforward, feedback: feedback });
                }
                createWaveShaper() { return new WaveShaperNode(this); }
                createStereoPanner() { return new StereoPannerNode(this); }
                createAnalyser() { return new AnalyserNode(this); }
                createConvolver() { return new ConvolverNode(this); }
                createDynamicsCompressor() { return new DynamicsCompressorNode(this); }
                createPanner() { return new PannerNode(this); }
                get listener() { return this[S].listener; }
                // WA 1.1.3 decodeAudioData: the ArrayBuffer is detached, the decode runs in
                // parallel, and the result is resampled to this context's rate.
                decodeAudioData(audioData, successCallback, errorCallback) {
                    var s = this[S];
                    var argc = arguments.length;
                    return new Promise(function (resolve, reject) {
                        if (argc < 1 || !(audioData instanceof ArrayBuffer)) {
                            reject(typeError("Failed to execute 'decodeAudioData' on 'BaseAudioContext': parameter 1 is not of type 'ArrayBuffer'."));
                            return;
                        }
                        var bytes;
                        try {
                            if (audioData.detached === true) throw 0;
                            new Uint8Array(audioData);
                            bytes = new Uint8Array(audioData.transfer());
                        } catch (e) {
                            var cloneError = domError("Failed to execute 'decodeAudioData' on 'BaseAudioContext': Cannot decode detached ArrayBuffer", 'DataCloneError');
                            if (typeof errorCallback === 'function') { try { errorCallback.call(undefined, cloneError); } catch (x) { if (typeof g.reportError === 'function') g.reportError(x); } }
                            reject(cloneError);
                            return;
                        }
                        var id = g.__fenWaDecode(s.id, bytes);
                        s.decodes = s.decodes || new Map();
                        s.decodes.set(id, function (ok, channels, length, message) {
                            if (!ok) {
                                var err = domError("Unable to decode audio data" + (message ? ': ' + message : ''), 'EncodingError');
                                if (typeof errorCallback === 'function') { try { errorCallback.call(undefined, err); } catch (e) { if (typeof g.reportError === 'function') g.reportError(e); } }
                                reject(err);
                                return;
                            }
                            var buffer = bufferFromChannels(channels, length, s.sampleRate);
                            for (var c = 0; c < channels; c++) g.__fenWaReadDecoded(s.id, id, c, buffer[S].data[c]);
                            g.__fenWaReleaseDecoded(s.id, id);
                            if (typeof successCallback === 'function') { try { successCallback.call(undefined, buffer); } catch (e) { if (typeof g.reportError === 'function') g.reportError(e); } }
                            resolve(buffer);
                        });
                    });
                }
            }
            handler(BaseAudioContext.prototype, 'onstatechange');
            webidl(BaseAudioContext, 'BaseAudioContext');

            // WA 1.1 AudioContextRenderSizeCategory / renderSizeHint: 'default' and 'hardware'
            // both mean 128 here; a number asks for that quantum size.
            function renderSize(hint, what, rate) {
                if (hint === undefined) return 128;
                if (typeof hint === 'number') {
                    // WA 1.1: at most six seconds of frames at the context's rate.
                    var n = toUnsignedLong(hint);
                    var max = Math.floor(6 * rate);
                    if (n < 1 || n > max) throw domError("Failed to construct '" + what + "': The render size hint provided (" + n + ") is outside the range [1, " + max + "].", 'NotSupportedError');
                    return n;
                }
                toEnum(hint, ['default', 'hardware'], 'AudioContextRenderSizeCategory');
                return 128;
            }
            function initContext(context, offline, channels, length, rate, quantum) {
                var id = g.__fenWaCreateContext(offline, channels, length, rate, quantum);
                if (typeof id !== 'number' || id <= 0) throw domError('The audio context could not be created.', 'NotSupportedError');
                context[S] = {
                    id: id, sampleRate: g.__fenWaSampleRate(id), state: 'suspended', offline: offline, quantum: quantum,
                    maxChannels: offline ? channels : 2, nodes: new Map(),
                };
                contexts.set(id, context);
                context[S].destination = new AudioDestinationNode(INTERNAL, context, offline ? channels : 2);
                context[S].listener = new AudioListener(INTERNAL, context);
            }
            // WA 1.1: [[control thread state]] changes at once; statechange is queued, so a
            // promise settled in the same task is seen before the event.
            function setState(context, state) {
                var s = context[S];
                if (s.state === state) return;
                s.state = state;
                queueTask(function () { fire(context, new g.Event('statechange')); });
            }

            // ---- AudioContext (WA 1.2) ---------------------------------------------------

            var latencyHints = ['balanced', 'interactive', 'playback'];

            class AudioContext extends BaseAudioContext {
                constructor(options) {
                    var o = dictionary(options, 'AudioContextOptions');
                    var hint = 'interactive';
                    if (o.latencyHint !== undefined) {
                        if (typeof o.latencyHint === 'number') hint = toDouble(o.latencyHint, 'latencyHint');
                        else hint = toEnum(o.latencyHint, latencyHints, 'AudioContextLatencyCategory');
                    }
                    var rate = 0;
                    if (o.sampleRate !== undefined) {
                        rate = toFloat(o.sampleRate, 'sampleRate');
                        if (!(rate >= 3000 && rate <= 768000)) throw domError("Failed to construct 'AudioContext': The sample rate provided (" + rate + ") is outside the range [3000, 768000].", 'NotSupportedError');
                    }
                    var quantum = renderSize(o.renderSizeHint, 'AudioContext', rate || 48000);
                    var sink = o.sinkId === undefined ? '' : sinkTarget(o.sinkId, "Failed to construct 'AudioContext'");
                    super(INTERNAL);
                    initContext(this, false, 2, 0, rate, quantum);
                    this[S].sinkId = typeof sink === 'string' ? sink : new AudioSinkInfo(INTERNAL, 'none');
                    if (sink !== '') g.__fenWaSetSink(this[S].id, typeof sink === 'string' ? sink : null);
                    this[S].latencyHint = hint;
                    var self = this;
                    // WA 1.2.1 step 10: a context allowed to start begins rendering, and says
                    // so in a queued task.
                    if (g.__fenWaAllowedToStart()) {
                        this[S].pendingStart = true;
                        g.__fenWaResume(this[S].id);
                        queueTask(function () {
                            if (self[S].state === 'suspended' && self[S].pendingStart) setState(self, 'running');
                            self[S].pendingStart = false;
                        });
                    }
                }
                get baseLatency() { return this[S].quantum / this[S].sampleRate; }
                get sinkId() { return this[S].sinkId; }
                get playbackStats() {
                    var s = this[S];
                    if (!s.playbackStats) s.playbackStats = new AudioPlaybackStats(INTERNAL, this);
                    return s.playbackStats;
                }
                // WA 1.2.3 setSinkId: an output device's id, '' for the default, or
                // { type: 'none' } to render without one.
                setSinkId(sinkId) {
                    var self = this;
                    var s = this[S];
                    var argc = arguments.length;
                    return new Promise(function (resolve, reject) {
                        if (argc < 1) { reject(typeError("Failed to execute 'setSinkId' on 'AudioContext': 1 argument required, but only 0 present.")); return; }
                        var target;
                        try { target = sinkTarget(sinkId, "Failed to execute 'setSinkId' on 'AudioContext'"); } catch (e) { reject(e); return; }
                        if (s.state === 'closed') { reject(domError("Failed to execute 'setSinkId' on 'AudioContext': the context is closed.", 'InvalidStateError')); return; }
                        if (sameSink(s.sinkId, target)) { resolve(); return; }
                        var result = g.__fenWaSetSink(s.id, typeof target === 'string' ? target : null);
                        if (result === 'NotFoundError') { reject(domError("Failed to execute 'setSinkId' on 'AudioContext': the device '" + target + "' was not found.", 'NotFoundError')); return; }
                        queueTask(function () {
                            s.sinkId = typeof target === 'string' ? target : new AudioSinkInfo(INTERNAL, 'none');
                            resolve();
                            queueTask(function () { fire(self, new g.Event('sinkchange')); });
                        });
                    });
                }
                get outputLatency() { return g.__fenWaOutputLatency(this[S].id); }
                getOutputTimestamp() {
                    var s = this[S];
                    var t = g.__fenWaCurrentTime(s.id);
                    var now = g.performance && typeof g.performance.now === 'function' ? g.performance.now() : 0;
                    return { contextTime: t, performanceTime: now };
                }
                resume() {
                    var self = this;
                    var s = this[S];
                    return new Promise(function (resolve, reject) {
                        if (s.state === 'closed') { reject(domError("Failed to execute 'resume' on 'AudioContext': Cannot resume a closed AudioContext.", 'InvalidStateError')); return; }
                        g.__fenWaResume(s.id);
                        queueTask(function () {
                            if (s.state !== 'closed') setState(self, 'running');
                            resolve();
                        });
                        s.pendingStart = false;
                    });
                }
                suspend() {
                    var self = this;
                    var s = this[S];
                    return new Promise(function (resolve, reject) {
                        if (s.state === 'closed') { reject(domError("Failed to execute 'suspend' on 'AudioContext': Cannot suspend a closed AudioContext.", 'InvalidStateError')); return; }
                        s.pendingStart = false;
                        g.__fenWaSuspend(s.id);
                        queueTask(function () {
                            if (s.state !== 'closed') setState(self, 'suspended');
                            resolve();
                        });
                    });
                }
                close() {
                    var self = this;
                    var s = this[S];
                    return new Promise(function (resolve, reject) {
                        if (s.state === 'closed' || s.closing) { reject(domError("Failed to execute 'close' on 'AudioContext': Cannot close a closed AudioContext.", 'InvalidStateError')); return; }
                        s.closing = true;
                        s.pendingStart = false;
                        g.__fenWaClose(s.id);
                        queueTask(function () {
                            setState(self, 'closed');
                            resolve();
                        });
                    });
                }
            }
            handler(AudioContext.prototype, 'onsinkchange');
            webidl(AudioContext, 'AudioContext');

            function sinkTarget(v, prefix) {
                if (v !== null && typeof v === 'object') {
                    if (v.type === undefined) throw typeError(prefix + ": required member type is undefined.");
                    toEnum(v.type, ['none'], 'AudioSinkType');
                    return { type: 'none' };
                }
                return String(v);
            }
            function sameSink(current, target) {
                if (typeof current === 'string' && typeof target === 'string') return current === target;
                return typeof current === 'object' && typeof target === 'object';
            }

            class AudioSinkInfo {
                constructor(token, type) {
                    if (token !== INTERNAL) throw typeError('Illegal constructor');
                    this[S] = { type: type };
                }
                get type() { return this[S].type; }
            }
            webidl(AudioSinkInfo, 'AudioSinkInfo');

            // AudioPlaybackStats: a snapshot the engine refreshes at most once a second, so every
            // read in one task - and toJSON() - sees the same numbers.
            class AudioPlaybackStats {
                constructor(token, context) {
                    if (token !== INTERNAL) throw typeError('Illegal constructor');
                    this[S] = { ctx: context };
                }
                get underrunDuration() { return statsOf(this)[0]; }
                get underrunEvents() { return statsOf(this)[1]; }
                get totalDuration() { return statsOf(this)[2]; }
                get averageLatency() { return statsOf(this)[3]; }
                get minimumLatency() { return statsOf(this)[4]; }
                get maximumLatency() { return statsOf(this)[5]; }
                resetLatency() { g.__fenWaPlaybackStats(this[S].ctx[S].id, true); this[S].frozen = null; }
                toJSON() {
                    var v = statsOf(this);
                    return { underrunDuration: v[0], underrunEvents: v[1], totalDuration: v[2], averageLatency: v[3], minimumLatency: v[4], maximumLatency: v[5] };
                }
            }
            // Run to completion: once read, the numbers hold until the script that read them
            // has finished (the next microtask checkpoint), even if it busy-waits past the
            // engine's once-a-second refresh.
            function statsOf(stats) {
                var s = stats[S];
                if (s.frozen) return s.frozen;
                var v = g.__fenWaPlaybackStats(s.ctx[S].id, false);
                s.frozen = v && v.length === 6 ? v : [0, 0, 0, 0, 0, 0];
                // A promise job, so it runs ahead of any await that follows the read.
                Promise.resolve().then(function () { s.frozen = null; });
                return s.frozen;
            }
            webidl(AudioPlaybackStats, 'AudioPlaybackStats');

            // ---- OfflineAudioContext (WA 1.3) ------------------------------------------

            class OfflineAudioContext extends BaseAudioContext {
                constructor(a, b, c) {
                    var channels, length, rate, quantum = 128;
                    if (arguments.length === 1 && a !== null && typeof a === 'object') {
                        if (a.length === undefined) throw typeError("Failed to construct 'OfflineAudioContext': required member length is undefined.");
                        if (a.sampleRate === undefined) throw typeError("Failed to construct 'OfflineAudioContext': required member sampleRate is undefined.");
                        channels = a.numberOfChannels === undefined ? 1 : toUnsignedLong(a.numberOfChannels);
                        length = toUnsignedLong(a.length);
                        rate = toFloat(a.sampleRate, 'sampleRate');
                        if (rate >= 3000 && rate <= 768000) quantum = renderSize(a.renderSizeHint, 'OfflineAudioContext', rate);
                    } else {
                        if (arguments.length < 3) throw typeError("Failed to construct 'OfflineAudioContext': 3 arguments required, but only " + arguments.length + " present.");
                        channels = toUnsignedLong(a);
                        length = toUnsignedLong(b);
                        rate = toFloat(c, 'sampleRate');
                    }
                    if (channels < 1 || channels > 32) throw domError("Failed to construct 'OfflineAudioContext': The number of channels provided (" + channels + ") is outside the range [1, 32].", 'NotSupportedError');
                    if (length < 1) throw domError("Failed to construct 'OfflineAudioContext': The number of frames provided (" + length + ") is less than the minimum bound (1).", 'NotSupportedError');
                    if (!(rate >= 3000 && rate <= 768000)) throw domError("Failed to construct 'OfflineAudioContext': The sample rate provided (" + rate + ") is outside the range [3000, 768000].", 'NotSupportedError');
                    super(INTERNAL);
                    initContext(this, true, channels, length, rate, quantum);
                    this[S].length = length;
                    this[S].channels = channels;
                    this[S].suspends = new Map();
                }
                get length() { return this[S].length; }
                startRendering() {
                    var self = this;
                    var s = this[S];
                    if (s.renderingStarted) return Promise.reject(domError("Failed to execute 'startRendering' on 'OfflineAudioContext': cannot call startRendering more than once.", 'InvalidStateError'));
                    s.renderingStarted = true;
                    return new Promise(function (resolve) {
                        s.completeRendering = resolve;
                        setState(self, 'running');
                        g.__fenWaStartRendering(s.id);
                    });
                }
                suspend(suspendTime) {
                    var self = this;
                    var s = this[S];
                    var argc = arguments.length;
                    return new Promise(function (resolve, reject) {
                        if (argc < 1) { reject(typeError("Failed to execute 'suspend' on 'OfflineAudioContext': 1 argument required, but only 0 present.")); return; }
                        var t;
                        try { t = toDouble(suspendTime, 'suspendTime'); } catch (e) { reject(e); return; }
                        if (t < 0) { reject(domError("Failed to execute 'suspend' on 'OfflineAudioContext': negative suspend time (" + t + ") is not allowed.", 'InvalidStateError')); return; }
                        // WA 1.3.4 suspend(): the time is quantized, rounded up to the render quantum.
                        var q = s.quantum;
                        var frame = Math.ceil(t * s.sampleRate / q - 1e-9) * q;
                        if (frame >= s.length) { reject(domError("Failed to execute 'suspend' on 'OfflineAudioContext': cannot schedule a suspend at frame " + frame + " because it is greater than or equal to the total render duration of " + s.length + " frames.", 'InvalidStateError')); return; }
                        if (s.renderingStarted && frame <= g.__fenWaCurrentFrame(s.id)) {
                            reject(domError("Failed to execute 'suspend' on 'OfflineAudioContext': cannot schedule a suspend at frame " + frame + " because it is earlier than the current frame of " + g.__fenWaCurrentFrame(s.id) + ".", 'InvalidStateError'));
                            return;
                        }
                        if (s.suspends.has(frame)) { reject(domError("Failed to execute 'suspend' on 'OfflineAudioContext': cannot schedule more than one suspend at frame " + frame + ".", 'InvalidStateError')); return; }
                        s.suspends.set(frame, function () {
                            setState(self, 'suspended');
                            resolve();
                        });
                        g.__fenWaScheduleSuspend(s.id, frame);
                    });
                }
                resume() {
                    var self = this;
                    var s = this[S];
                    return new Promise(function (resolve, reject) {
                        if (s.state === 'closed') { reject(domError("Failed to execute 'resume' on 'OfflineAudioContext': cannot resume a closed context.", 'InvalidStateError')); return; }
                        if (!s.renderingStarted) { reject(domError("Failed to execute 'resume' on 'OfflineAudioContext': cannot resume an offline context that has not started rendering.", 'InvalidStateError')); return; }
                        if (s.state === 'running') { resolve(); return; }
                        setState(self, 'running');
                        g.__fenWaResume(s.id);
                        resolve();
                    });
                }
            }
            handler(OfflineAudioContext.prototype, 'oncomplete');
            webidl(OfflineAudioContext, 'OfflineAudioContext');

            // ---- OfflineAudioCompletionEvent (WA 1.7) ----------------------------------

            class OfflineAudioCompletionEvent extends g.Event {
                constructor(type, init) {
                    if (arguments.length < 2) throw typeError("Failed to construct 'OfflineAudioCompletionEvent': 2 arguments required, but only " + arguments.length + " present.");
                    var o = dictionary(init, 'OfflineAudioCompletionEventInit');
                    if (!(o.renderedBuffer instanceof AudioBuffer)) throw typeError("Failed to construct 'OfflineAudioCompletionEvent': required member renderedBuffer is not of type 'AudioBuffer'.");
                    super(type, o);
                    this[S] = { renderedBuffer: o.renderedBuffer };
                }
                get renderedBuffer() { return this[S].renderedBuffer; }
            }
            webidl(OfflineAudioCompletionEvent, 'OfflineAudioCompletionEvent');

            // ---- hooks the engine calls --------------------------------------------------

            g.__fenWaOnEnded = function (contextId, nodeId) {
                var context = contexts.get(contextId);
                if (!context) return;
                var node = context[S].nodes.get(nodeId);
                context[S].nodes.delete(nodeId);
                if (node) fire(node, new g.Event('ended'));
            };
            g.__fenWaOnDecoded = function (contextId, requestId, ok, channels, length, message) {
                var context = contexts.get(contextId);
                if (!context || !context[S].decodes) return;
                var done = context[S].decodes.get(requestId);
                context[S].decodes.delete(requestId);
                if (done) done(ok, channels, length, message);
            };
            g.__fenWaOnSuspended = function (contextId, frame) {
                var context = contexts.get(contextId);
                if (!context) return;
                var s = context[S];
                var cb = s.suspends.get(frame);
                s.suspends.delete(frame);
                if (cb) cb();
            };
            g.__fenWaOnComplete = function (contextId) {
                var context = contexts.get(contextId);
                if (!context) return;
                var s = context[S];
                var buffer = bufferFromChannels(s.channels, s.length, s.sampleRate);
                for (var c = 0; c < s.channels; c++) g.__fenWaReadResult(s.id, c, buffer[S].data[c]);
                var resolve = s.completeRendering;
                s.completeRendering = null;
                setState(context, 'closed');
                if (resolve) resolve(buffer);
                queueTask(function () {
                    fire(context, new OfflineAudioCompletionEvent('complete', { renderedBuffer: buffer }));
                });
            };
        })();
        """;
}
