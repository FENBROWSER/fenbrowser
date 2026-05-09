using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    public static class WebAudioApi
    {
        public static FenObject CreateAudioContextConstructor(IExecutionContext context)
        {
            FenObject prototype = null;
            var ctor = new FenFunction("AudioContext", (args, thisVal) =>
            {
                var audioCtx = new FnAudioContext(context);
                if (prototype != null)
                    audioCtx.SetPrototype(prototype);
                audioCtx.Set("__className", FenValue.FromString("AudioContext"));
                return FenValue.FromObject(audioCtx);
            })
            {
                IsConstructor = true,
                NativeLength = 0
            };

            prototype = CreateAudioNodePrototype(context, ctor, "AudioContext");
            prototype.Set("__className", FenValue.FromString("AudioContext.prototype"));

            ctor.Set("prototype", FenValue.FromObject(prototype));

            return ctor;
        }

        public static FenObject CreateOscillatorNodeConstructor()
        {
            var ctor = new FenFunction("OscillatorNode", (args, thisVal) =>
            {
                var context = args.Length > 0 && args[0].IsObject ? args[0].AsObject() : null;
                var node = new FnOscillatorNode(context);
                node.Set("__className", FenValue.FromString("OscillatorNode"));
                return FenValue.FromObject(node);
            })
            {
                IsConstructor = true,
                NativeLength = 1
            };

            var prototype = CreateAudioNodePrototype(null, ctor, "OscillatorNode");
            prototype.Set("__className", FenValue.FromString("OscillatorNode.prototype"));

            ctor.Set("prototype", FenValue.FromObject(prototype));

            return ctor;
        }

        public static FenObject CreateGainNodeConstructor()
        {
            var ctor = new FenFunction("GainNode", (args, thisVal) =>
            {
                var context = args.Length > 0 && args[0].IsObject ? args[0].AsObject() : null;
                var node = new FnGainNode(context);
                node.Set("__className", FenValue.FromString("GainNode"));
                return FenValue.FromObject(node);
            })
            {
                IsConstructor = true,
                NativeLength = 1
            };

            var prototype = CreateAudioNodePrototype(null, ctor, "GainNode");
            prototype.Set("__className", FenValue.FromString("GainNode.prototype"));

            ctor.Set("prototype", FenValue.FromObject(prototype));

            return ctor;
        }

        public static FenObject CreateBiquadFilterNodeConstructor()
        {
            var ctor = new FenFunction("BiquadFilterNode", (args, thisVal) =>
            {
                var context = args.Length > 0 && args[0].IsObject ? args[0].AsObject() : null;
                var node = new FnBiquadFilterNode(context);
                node.Set("__className", FenValue.FromString("BiquadFilterNode"));
                return FenValue.FromObject(node);
            })
            {
                IsConstructor = true,
                NativeLength = 1
            };

            var prototype = CreateAudioNodePrototype(null, ctor, "BiquadFilterNode");
            prototype.Set("__className", FenValue.FromString("BiquadFilterNode.prototype"));

            ctor.Set("prototype", FenValue.FromObject(prototype));

            return ctor;
        }

        public static FenObject CreateDelayNodeConstructor()
        {
            var ctor = new FenFunction("DelayNode", (args, thisVal) =>
            {
                var context = args.Length > 0 && args[0].IsObject ? args[0].AsObject() : null;
                var maxDelay = args.Length > 1 && args[1].IsNumber ? args[1].ToNumber() : 1.0;
                if (maxDelay < 0) maxDelay = 1.0;
                var node = new FnDelayNode(context, maxDelay);
                node.Set("__className", FenValue.FromString("DelayNode"));
                return FenValue.FromObject(node);
            })
            {
                IsConstructor = true,
                NativeLength = 2
            };

            var prototype = CreateAudioNodePrototype(null, ctor, "DelayNode");
            prototype.Set("__className", FenValue.FromString("DelayNode.prototype"));

            ctor.Set("prototype", FenValue.FromObject(prototype));

            return ctor;
        }

        internal class FnAudioParam : FenObject
        {
            private readonly object _lock = new object();
            private double _value;
            private readonly List<AutomationEvent> _events = new List<AutomationEvent>();

            public double Value => _value;

            internal FnAudioParam(double defaultValue)
            {
                _value = defaultValue;
            }

            private void AddEvent(AutomationEvent evt)
            {
                lock (_lock)
                {
                    _events.Add(evt);
                    _events.Sort((a, b) => a.Time.CompareTo(b.Time));
                }
            }

            private void CancelAfterTime(double cancelTime)
            {
                lock (_lock)
                {
                    _events.RemoveAll(e => e.Time >= cancelTime);
                    AutomationEvent holdEvent = null;
                    for (int i = _events.Count - 1; i >= 0; i--)
                    {
                        if (_events[i].Time <= cancelTime)
                        {
                            holdEvent = _events[i];
                            break;
                        }
                    }
                    if (holdEvent != null && !(holdEvent is CancelAndHoldEvent))
                    {
                        var hold = new CancelAndHoldEvent(cancelTime, _value);
                        _events.Add(hold);
                        _events.Sort((a, b) => a.Time.CompareTo(b.Time));
                    }
                }
            }

            internal double Evaluate(double currentTime)
            {
                lock (_lock)
                {
                    if (_events.Count == 0)
                        return _value;

                    const double epsilon = 1e-9;
                    for (int i = _events.Count - 1; i >= 0; i--)
                    {
                        var evt = _events[i];
                        if (evt.Time <= currentTime + epsilon)
                        {
                            if (evt.Time <= currentTime - epsilon && i < _events.Count - 1)
                                continue;
                            return evt.Evaluate(currentTime, _value, this, i);
                        }
                    }
                    return _value;
                }
            }

            private abstract class AutomationEvent
            {
                public double Time { get; }
                protected AutomationEvent(double time) { Time = time; }
                public abstract double Evaluate(double currentTime, double currentValue, FnAudioParam param, int eventIndex);
            }

            private class SetValueEvent : AutomationEvent
            {
                private readonly double _val;
                public SetValueEvent(double time, double val) : base(time) { _val = val; }
                public override double Evaluate(double currentTime, double currentValue, FnAudioParam param, int eventIndex)
                {
                    return _val;
                }
            }

            private class LinearRampEvent : AutomationEvent
            {
                private readonly double _target;
                private double _startTime;
                private double _startValue;
                public LinearRampEvent(double time, double target) : base(time) { _target = target; }
                public void SetStart(double startTime, double startValue) { _startTime = startTime; _startValue = startValue; }
                public override double Evaluate(double currentTime, double currentValue, FnAudioParam param, int eventIndex)
                {
                    if (currentTime >= Time) return _target;
                    double progress = (currentTime - _startTime) / (Time - _startTime);
                    return _startValue + (_target - _startValue) * progress;
                }
            }

            private class ExponentialRampEvent : AutomationEvent
            {
                private readonly double _target;
                private double _startTime;
                private double _startValue;
                public ExponentialRampEvent(double time, double target) : base(time) { _target = target; }
                public void SetStart(double startTime, double startValue) { _startTime = startTime; _startValue = startValue; }
                public override double Evaluate(double currentTime, double currentValue, FnAudioParam param, int eventIndex)
                {
                    if (currentTime >= Time) return _target;
                    double progress = (currentTime - _startTime) / (Time - _startTime);
                    return _startValue * Math.Pow(_target / _startValue, progress);
                }
            }

            private class SetTargetEvent : AutomationEvent
            {
                private readonly double _target;
                private readonly double _timeConstant;
                public SetTargetEvent(double time, double target, double timeConstant) : base(time) { _target = target; _timeConstant = timeConstant; }
                public override double Evaluate(double currentTime, double currentValue, FnAudioParam param, int eventIndex)
                {
                    return _target + (currentValue - _target) * Math.Exp(-(currentTime - Time) / _timeConstant);
                }
            }

            private class CancelAndHoldEvent : AutomationEvent
            {
                private readonly double _heldValue;
                public CancelAndHoldEvent(double time, double heldValue) : base(time) { _heldValue = heldValue; }
                public override double Evaluate(double currentTime, double currentValue, FnAudioParam param, int eventIndex)
                {
                    return _heldValue;
                }
            }

            public void SetValueAtTime(double value, double startTime)
            {
                AddEvent(new SetValueEvent(startTime, value));
            }

            public void LinearRampToValueAtTime(double value, double endTime)
            {
                var ramp = new LinearRampEvent(endTime, value);
                lock (_lock)
                {
                    double startTime = 0;
                    double startValue = _value;
                    for (int i = _events.Count - 1; i >= 0; i--)
                    {
                        var e = _events[i];
                        if (e.Time < endTime)
                        {
                            startTime = Math.Max(e.Time, 0);
                            startValue = e is SetTargetEvent stEvt
                                ? stEvt.Evaluate(startTime, _value, this, i)
                                : (e is SetValueEvent svEvt ? svEvt.Evaluate(startTime, _value, this, i) : _value);
                            break;
                        }
                    }
                    ramp.SetStart(startTime, startValue);
                }
                AddEvent(ramp);
            }

            public void ExponentialRampToValueAtTime(double value, double endTime)
            {
                if (value <= 0) value = 1e-9;
                var ramp = new ExponentialRampEvent(endTime, value);
                lock (_lock)
                {
                    double startTime = 0;
                    double startValue = Math.Max(_value, 1e-9);
                    for (int i = _events.Count - 1; i >= 0; i--)
                    {
                        var e = _events[i];
                        if (e.Time < endTime)
                        {
                            startTime = Math.Max(e.Time, 0);
                            startValue = Math.Max(e is SetTargetEvent stEvt
                                ? stEvt.Evaluate(startTime, _value, this, i)
                                : (e is SetValueEvent svEvt ? svEvt.Evaluate(startTime, _value, this, i) : _value), 1e-9);
                            break;
                        }
                    }
                    ramp.SetStart(startTime, startValue);
                }
                AddEvent(ramp);
            }

            public void SetTargetAtTime(double target, double startTime, double timeConstant)
            {
                AddEvent(new SetTargetEvent(startTime, target, Math.Max(timeConstant, 0.001)));
            }

            public void CancelScheduledValues(double cancelTime)
            {
                lock (_lock)
                {
                    _events.RemoveAll(e => e.Time > cancelTime);
                }
            }

            public void CancelAndHoldAtTime(double cancelTime)
            {
                CancelAfterTime(cancelTime);
            }

            internal void SetValueInternal(double val)
            {
                _value = val;
            }
        }

        internal class FnAudioNode : FenObject
        {
            private static int _nextNodeId;
            internal readonly int _nodeId;
            internal IObject _context;
            internal readonly Dictionary<int, FnAudioNode> _connections = new Dictionary<int, FnAudioNode>();
            private readonly object _connLock = new object();

            public FnAudioNode(IObject context)
            {
                _nodeId = Interlocked.Increment(ref _nextNodeId);
                _context = context;
            }

            internal void RegisterConnection(FnAudioNode destination)
            {
                lock (_connLock)
                {
                    _connections[destination._nodeId] = destination;
                }
            }

            internal void RemoveConnection(FnAudioNode destination)
            {
                lock (_connLock)
                {
                    _connections.Remove(destination._nodeId);
                }
            }

            internal void UnregisterFromAll()
            {
                lock (_connLock)
                {
                    _connections.Clear();
                }
            }
        }

        internal class FnAudioDestinationNode : FnAudioNode
        {
            public FnAudioDestinationNode(IObject context) : base(context)
            {
                Set("context", FenValue.FromObject(context));
                Set("numberOfInputs", FenValue.FromNumber(1));
                Set("numberOfOutputs", FenValue.FromNumber(0));
                Set("channelCount", FenValue.FromNumber(2));
                Set("channelCountMode", FenValue.FromString("explicit"));
                Set("channelInterpretation", FenValue.FromString("speakers"));
                Set("maxChannelCount", FenValue.FromNumber(2));
            }
        }

        internal class FnOscillatorNode : FnAudioNode
        {
            internal readonly FnAudioParam _frequency;
            internal readonly FnAudioParam _detune;
            private string _type = "sine";
            private bool _started;
            private bool _stopped;
            private FenValue _periodicWave = FenValue.Undefined;
            private double _startTime = -1;
            private double _stopTime = -1;

            public FnOscillatorNode(IObject context) : base(context)
            {
                _frequency = new FnAudioParam(440);
                _detune = new FnAudioParam(0);

                Set("context", FenValue.FromObject(context));
                Set("numberOfInputs", FenValue.FromNumber(0));
                Set("numberOfOutputs", FenValue.FromNumber(1));
                Set("channelCount", FenValue.FromNumber(2));
                Set("channelCountMode", FenValue.FromString("max"));
                Set("channelInterpretation", FenValue.FromString("speakers"));
                Set("type", FenValue.FromString(_type));

                WebAudioApi.BindParam(this, "frequency", _frequency);
                WebAudioApi.BindParam(this, "detune", _detune);

                Set("start", FenValue.FromFunction(new FenFunction("start", (args, thisVal) =>
                {
                    if (_started) throw new InvalidOperationException("InvalidStateError");
                    _started = true;
                    _startTime = args.Length > 0 && args[0].IsNumber ? args[0].ToNumber() : 0;
                    return FenValue.Undefined;
                })));

                Set("stop", FenValue.FromFunction(new FenFunction("stop", (args, thisVal) =>
                {
                    if (!_started || _stopped)
                        throw new InvalidOperationException("InvalidStateError");
                    _stopped = true;
                    _stopTime = args.Length > 0 && args[0].IsNumber ? args[0].ToNumber() : 0;
                    _startTime = -1;
                    return FenValue.Undefined;
                })));

                Set("setPeriodicWave", FenValue.FromFunction(new FenFunction("setPeriodicWave", (args, thisVal) =>
                {
                    if (args.Length > 0 && !args[0].IsUndefined)
                    {
                        _periodicWave = args[0];
                        _type = "custom";
                        Set("type", FenValue.FromString("custom"));
                    }
                    return FenValue.Undefined;
                })));
            }

            public string OscillatorType
            {
                get => _type;
                set
                {
                    if (value == "custom" && _periodicWave.IsUndefined)
                        throw new InvalidOperationException("InvalidStateError: periodic wave must be set before type 'custom'.");
                    var valid = new HashSet<string> { "sine", "square", "sawtooth", "triangle", "custom" };
                    if (!valid.Contains(value))
                        throw new InvalidOperationException("NotSupportedError: Unsupported oscillator type.");
                    _type = value;
                    Set("type", FenValue.FromString(_type));
                }
            }
        }

        internal class FnGainNode : FnAudioNode
        {
            internal readonly FnAudioParam _gain;

            public FnGainNode(IObject context) : base(context)
            {
                _gain = new FnAudioParam(1.0);

                Set("context", FenValue.FromObject(context));
                Set("numberOfInputs", FenValue.FromNumber(1));
                Set("numberOfOutputs", FenValue.FromNumber(1));
                Set("channelCount", FenValue.FromNumber(2));
                Set("channelCountMode", FenValue.FromString("max"));
                Set("channelInterpretation", FenValue.FromString("speakers"));

                WebAudioApi.BindParam(this, "gain", _gain);
            }
        }

        internal class FnBiquadFilterNode : FnAudioNode
        {
            internal readonly FnAudioParam _frequency;
            internal readonly FnAudioParam _detune;
            internal readonly FnAudioParam _Q;
            internal readonly FnAudioParam _gain;
            private string _type = "lowpass";

            public FnBiquadFilterNode(IObject context) : base(context)
            {
                _frequency = new FnAudioParam(350);
                _detune = new FnAudioParam(0);
                _Q = new FnAudioParam(1);
                _gain = new FnAudioParam(0);

                Set("context", FenValue.FromObject(context));
                Set("numberOfInputs", FenValue.FromNumber(1));
                Set("numberOfOutputs", FenValue.FromNumber(1));
                Set("channelCount", FenValue.FromNumber(2));
                Set("channelCountMode", FenValue.FromString("max"));
                Set("channelInterpretation", FenValue.FromString("speakers"));
                Set("type", FenValue.FromString(_type));

                WebAudioApi.BindParam(this, "frequency", _frequency);
                WebAudioApi.BindParam(this, "detune", _detune);
                WebAudioApi.BindParam(this, "Q", _Q);
                WebAudioApi.BindParam(this, "gain", _gain);
            }

            public string FilterType
            {
                get => _type;
                set
                {
                    var valid = new HashSet<string> { "lowpass", "highpass", "bandpass", "lowshelf", "highshelf", "peaking", "notch", "allpass" };
                    if (!valid.Contains(value))
                        throw new InvalidOperationException("NotSupportedError: Unsupported filter type.");
                    _type = value;
                    Set("type", FenValue.FromString(_type));
                }
            }
        }

        internal class FnDelayNode : FnAudioNode
        {
            internal readonly FnAudioParam _delayTime;

            public FnDelayNode(IObject context, double maxDelayTime) : base(context)
            {
                _delayTime = new FnAudioParam(0);

                Set("context", FenValue.FromObject(context));
                Set("numberOfInputs", FenValue.FromNumber(1));
                Set("numberOfOutputs", FenValue.FromNumber(1));
                Set("channelCount", FenValue.FromNumber(2));
                Set("channelCountMode", FenValue.FromString("max"));
                Set("channelInterpretation", FenValue.FromString("speakers"));
                Set("delayTime", FenValue.FromObject(WebAudioApi.BindParamObj(_delayTime)));

                WebAudioApi.BindParam(this, "delayTime", _delayTime);
            }
        }

        internal class FnAudioBuffer : FenObject
        {
            private readonly float[][] _channels;
            public int SampleRate { get; }
            public int Length { get; }
            public int NumberOfChannels => _channels.Length;
            public double Duration => Length > 0 && SampleRate > 0 ? (double)Length / SampleRate : 0;

            public FnAudioBuffer(int numberOfChannels, int length, int sampleRate)
            {
                SampleRate = sampleRate;
                Length = length;
                _channels = new float[numberOfChannels][];
                for (int i = 0; i < numberOfChannels; i++)
                    _channels[i] = new float[length];

                Set("sampleRate", FenValue.FromNumber(sampleRate));
                Set("length", FenValue.FromNumber(length));
                Set("duration", FenValue.FromNumber(Duration));
                Set("numberOfChannels", FenValue.FromNumber(numberOfChannels));

                Set("getChannelData", FenValue.FromFunction(new FenFunction("getChannelData", (args, thisVal) =>
                {
                    if (args.Length < 1) return FenValue.Undefined;
                    int channel = (int)args[0].ToNumber();
                    if (channel < 0 || channel >= _channels.Length)
                        throw new InvalidOperationException("IndexSizeError: channel out of range.");
                    return CreateFloat32Array(_channels[channel]);
                })));

                Set("copyFromChannel", FenValue.FromFunction(new FenFunction("copyFromChannel", (args, thisVal) =>
                {
                    if (args.Length < 2) return FenValue.Undefined;
                    var dest = args[0];
                    int channelNum = (int)args[1].ToNumber();
                    int startInChannel = args.Length > 2 && args[2].IsNumber ? (int)args[2].ToNumber() : 0;
                    if (channelNum < 0 || channelNum >= _channels.Length)
                        throw new InvalidOperationException("IndexSizeError: channel out of range.");
                    if (dest.IsObject && dest.AsObject() != null)
                    {
                        CopyToArray(dest.AsObject(), _channels[channelNum], startInChannel);
                    }
                    return FenValue.Undefined;
                })));

                Set("copyToChannel", FenValue.FromFunction(new FenFunction("copyToChannel", (args, thisVal) =>
                {
                    if (args.Length < 2) return FenValue.Undefined;
                    var source = args[0];
                    int channelNum = (int)args[1].ToNumber();
                    int startInChannel = args.Length > 2 && args[2].IsNumber ? (int)args[2].ToNumber() : 0;
                    if (channelNum < 0 || channelNum >= _channels.Length)
                        throw new InvalidOperationException("IndexSizeError: channel out of range.");
                    if (source.IsObject && source.AsObject() != null)
                    {
                        CopyFromArray(source.AsObject(), _channels[channelNum], startInChannel);
                    }
                    return FenValue.Undefined;
                })));
            }

            public float[] GetChannelData(int channel) => _channels[channel];

            private static void CopyToArray(IObject dest, float[] source, int startInChannel)
            {
                var lenObj = dest.Get("length");
                int destLen = lenObj.IsNumber ? (int)lenObj.ToNumber() : 0;
                for (int i = 0; i < destLen && (startInChannel + i) < source.Length; i++)
                    dest.Set(i.ToString(), FenValue.FromNumber(source[startInChannel + i]));
            }

            private static void CopyFromArray(IObject source, float[] dest, int startInChannel)
            {
                var lenObj = source.Get("length");
                int srcLen = lenObj.IsNumber ? (int)lenObj.ToNumber() : 0;
                for (int i = 0; i < srcLen && (startInChannel + i) < dest.Length; i++)
                {
                    var v = source.Get(i.ToString());
                    dest[startInChannel + i] = (float)(v.IsNumber ? v.ToNumber() : 0);
                }
            }

            private static FenValue CreateFloat32Array(float[] data)
            {
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(data.Length));
                arr.Set("BYTES_PER_ELEMENT", FenValue.FromNumber(4));
                arr.Set("__className", FenValue.FromString("Float32Array"));
                for (int i = 0; i < data.Length; i++)
                    arr.Set(i.ToString(), FenValue.FromNumber(data[i]));

                arr.Set("buffer", FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsArrayBuffer(data.Length * 4)));

                return FenValue.FromObject(arr);
            }
        }

        internal class FnAudioContext : FenObject
        {
            private readonly IExecutionContext _executionContext;
            private readonly Stopwatch _clock;
            private readonly FnAudioDestinationNode _destination;
            private string _state = "running";
            private Timer _autoSuspendTimer;
            private readonly object _stateLock = new object();
            private readonly List<WeakReference<FnAudioNode>> _activeNodes = new List<WeakReference<FnAudioNode>>();
            private readonly List<FnAudioBuffer> _buffers = new List<FnAudioBuffer>();

            public FnAudioContext(IExecutionContext context)
            {
                _executionContext = context;
                _clock = Stopwatch.StartNew();
                _destination = new FnAudioDestinationNode(this);

                Set("sampleRate", FenValue.FromNumber(44100));
                Set("destination", FenValue.FromObject(_destination));
                Set("state", FenValue.FromString(_state));
                Set("onstatechange", FenValue.Null);

                Set("createOscillator", FenValue.FromFunction(new FenFunction("createOscillator", (args, thisVal) =>
                {
                    CheckNotClosed();
                    var node = new FnOscillatorNode(this);
                    RegisterNode(node);
                    return FenValue.FromObject(node);
                })));

                Set("createGain", FenValue.FromFunction(new FenFunction("createGain", (args, thisVal) =>
                {
                    CheckNotClosed();
                    var node = new FnGainNode(this);
                    RegisterNode(node);
                    return FenValue.FromObject(node);
                })));

                Set("createBiquadFilter", FenValue.FromFunction(new FenFunction("createBiquadFilter", (args, thisVal) =>
                {
                    CheckNotClosed();
                    var node = new FnBiquadFilterNode(this);
                    RegisterNode(node);
                    return FenValue.FromObject(node);
                })));

                Set("createDelay", FenValue.FromFunction(new FenFunction("createDelay", (args, thisVal) =>
                {
                    CheckNotClosed();
                    double max = args.Length > 0 && args[0].IsNumber ? args[0].ToNumber() : 1.0;
                    var node = new FnDelayNode(this, max);
                    RegisterNode(node);
                    return FenValue.FromObject(node);
                })));

                Set("createBuffer", FenValue.FromFunction(new FenFunction("createBuffer", (args, thisVal) =>
                {
                    CheckNotClosed();
                    int numChannels = args.Length > 0 && args[0].IsNumber ? (int)args[0].ToNumber() : 1;
                    int length = args.Length > 1 && args[1].IsNumber ? (int)args[1].ToNumber() : 0;
                    int sampleRate = args.Length > 2 && args[2].IsNumber ? (int)args[2].ToNumber() : 44100;
                    if (numChannels <= 0) numChannels = 1;
                    if (length < 0) length = 0;
                    if (sampleRate <= 0) sampleRate = 44100;
                    var buffer = new FnAudioBuffer(numChannels, length, sampleRate);
                    RegisterBuffer(buffer);
                    return FenValue.FromObject(buffer);
                })));

                Set("createBufferSource", FenValue.FromFunction(new FenFunction("createBufferSource", (args, thisVal) =>
                {
                    CheckNotClosed();
                    var node = new FnAudioBufferSourceNode(this);
                    RegisterNode(node);
                    return FenValue.FromObject(node);
                })));

                Set("createAnalyser", FenValue.FromFunction(new FenFunction("createAnalyser", (args, thisVal) =>
                {
                    CheckNotClosed();
                    var node = new FnAnalyserNode(this);
                    RegisterNode(node);
                    return FenValue.FromObject(node);
                })));

                Set("createStereoPanner", FenValue.FromFunction(new FenFunction("createStereoPanner", (args, thisVal) =>
                {
                    CheckNotClosed();
                    var node = new FnStereoPannerNode(this);
                    RegisterNode(node);
                    return FenValue.FromObject(node);
                })));

                Set("createPanner", FenValue.FromFunction(new FenFunction("createPanner", (args, thisVal) =>
                {
                    CheckNotClosed();
                    var node = new FnPannerNode(this);
                    RegisterNode(node);
                    return FenValue.FromObject(node);
                })));

                Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
                {
                    lock (_stateLock)
                    {
                        if (_state == "closed")
                            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, _executionContext));
                        SetState("closed");
                    }
                    ReleaseResources();
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, _executionContext));
                })));

                Set("suspend", FenValue.FromFunction(new FenFunction("suspend", (args, thisVal) =>
                {
                    lock (_stateLock)
                    {
                        if (_state == "closed")
                            return FenValue.FromObject(ResolvedThenable.Rejected("InvalidStateError: context is closed.", _executionContext));
                        if (_state == "suspended")
                            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, _executionContext));
                        SetState("suspended");
                    }
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, _executionContext));
                })));

                Set("resume", FenValue.FromFunction(new FenFunction("resume", (args, thisVal) =>
                {
                    lock (_stateLock)
                    {
                        if (_state == "closed")
                            return FenValue.FromObject(ResolvedThenable.Rejected("InvalidStateError: context is closed.", _executionContext));
                        if (_state == "running")
                            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, _executionContext));
                        SetState("running");
                        ResetAutoSuspendTimer();
                    }
                    return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, _executionContext));
                })));

                Set("decodeAudioData", FenValue.FromFunction(new FenFunction("decodeAudioData", (args, thisVal) =>
                {
                    if (args.Length < 1) return FenValue.FromObject(ResolvedThenable.Rejected(
                        "EncodingError: Invalid argument for decodeAudioData.", _executionContext));

                    var success = args.Length > 1 && args[1].IsFunction ? args[1].AsFunction() : null;
                    var error = args.Length > 2 && args[2].IsFunction ? args[2].AsFunction() : null;

                    if (_state == "closed")
                    {
                        if (error != null)
                            error.Invoke(new[] { FenValue.FromString("EncodingError: AudioContext is closed.") }, _executionContext);
                        return FenValue.FromObject(ResolvedThenable.Rejected(
                            "EncodingError: AudioContext is closed.", _executionContext));
                    }

                    int numChannels = 1;
                    int length = 0;
                    int sampleRate = 44100;
                    var arrayBuffer = args[0];
                    if (arrayBuffer.IsObject && arrayBuffer.AsObject() != null)
                    {
                        var bufObj = arrayBuffer.AsObject();
                        var byteLen = bufObj.Get("byteLength");
                        if (byteLen.IsNumber)
                            length = Math.Max(1, (int)(byteLen.ToNumber() / 4 / numChannels));
                    }

                    var buffer = new FnAudioBuffer(numChannels, length, sampleRate);
                    RegisterBuffer(buffer);
                    var result = FenValue.FromObject(buffer);

                    if (success != null)
                        success.Invoke(new[] { result }, _executionContext);

                    return FenValue.FromObject(ResolvedThenable.Resolved(result, _executionContext));
                })));

                Set("currentTime", FenValue.FromNumber(0));

                ResetAutoSuspendTimer();
            }

            public double CurrentTime => _clock.Elapsed.TotalSeconds;

            private void SetState(string newState)
            {
                _state = newState;
                Set("state", FenValue.FromString(_state));
                FireOnStateChange();
            }

            private void FireOnStateChange()
            {
                var handler = Get("onstatechange");
                if (handler.IsFunction)
                {
                    try { handler.AsFunction().Invoke(Array.Empty<FenValue>(), _executionContext); }
                    catch { }
                }
            }

            private void CheckNotClosed()
            {
                lock (_stateLock)
                {
                    if (_state == "closed")
                        throw new InvalidOperationException("InvalidStateError: AudioContext is closed.");
                }
            }

            private void RegisterNode(FnAudioNode node)
            {
                lock (_activeNodes)
                {
                    _activeNodes.Add(new WeakReference<FnAudioNode>(node));
                }
            }

            private void RegisterBuffer(FnAudioBuffer buffer)
            {
                lock (_buffers)
                {
                    _buffers.Add(buffer);
                }
            }

            private void ResetAutoSuspendTimer()
            {
                _autoSuspendTimer?.Dispose();
                _autoSuspendTimer = new Timer(_ =>
                {
                    lock (_stateLock)
                    {
                        if (_state == "running")
                            SetState("suspended");
                    }
                }, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
            }

            private void ReleaseResources()
            {
                _autoSuspendTimer?.Dispose();
                _clock.Stop();
                lock (_activeNodes)
                {
                    foreach (var wr in _activeNodes)
                    {
                        if (wr.TryGetTarget(out var node))
                            node.UnregisterFromAll();
                    }
                    _activeNodes.Clear();
                }
                lock (_buffers)
                {
                    _buffers.Clear();
                }
            }
        }

        internal class FnAudioBufferSourceNode : FnAudioNode
        {
            internal readonly FnAudioParam _playbackRate;
            internal readonly FnAudioParam _detune;
            private FnAudioBuffer _buffer;
            private bool _started;
            private bool _stopped;
            private bool _loop;
            private double _loopStart;
            private double _loopEnd;

            public FnAudioBufferSourceNode(IObject context) : base(context)
            {
                _playbackRate = new FnAudioParam(1.0);
                _detune = new FnAudioParam(0);

                Set("context", FenValue.FromObject(context));
                Set("numberOfInputs", FenValue.FromNumber(0));
                Set("numberOfOutputs", FenValue.FromNumber(1));
                Set("channelCount", FenValue.FromNumber(2));
                Set("channelCountMode", FenValue.FromString("max"));
                Set("channelInterpretation", FenValue.FromString("speakers"));

                WebAudioApi.BindParam(this, "playbackRate", _playbackRate);
                WebAudioApi.BindParam(this, "detune", _detune);

                Set("buffer", FenValue.Null);
                Set("loop", FenValue.FromBoolean(false));
                Set("loopStart", FenValue.FromNumber(0));
                Set("loopEnd", FenValue.FromNumber(0));
                Set("onended", FenValue.Null);

                Set("start", FenValue.FromFunction(new FenFunction("start", (args, thisVal) =>
                {
                    if (_started) throw new InvalidOperationException("InvalidStateError: Already started.");
                    _started = true;
                    return FenValue.Undefined;
                })));

                Set("stop", FenValue.FromFunction(new FenFunction("stop", (args, thisVal) =>
                {
                    if (!_started || _stopped)
                        throw new InvalidOperationException("InvalidStateError");
                    _stopped = true;
                    var onended = Get("onended");
                    if (onended.IsFunction)
                    {
                        try { onended.AsFunction().Invoke(Array.Empty<FenValue>(), null); } catch { }
                    }
                    return FenValue.Undefined;
                })));
            }
        }

        internal class FnStereoPannerNode : FnAudioNode
        {
            internal readonly FnAudioParam _pan;

            public FnStereoPannerNode(IObject context) : base(context)
            {
                _pan = new FnAudioParam(0);

                Set("context", FenValue.FromObject(context));
                Set("numberOfInputs", FenValue.FromNumber(1));
                Set("numberOfOutputs", FenValue.FromNumber(1));
                Set("channelCount", FenValue.FromNumber(2));
                Set("channelCountMode", FenValue.FromString("clamped-max"));
                Set("channelInterpretation", FenValue.FromString("speakers"));

                WebAudioApi.BindParam(this, "pan", _pan);
            }
        }

        internal class FnPannerNode : FnAudioNode
        {
            public FnPannerNode(IObject context) : base(context)
            {
                Set("context", FenValue.FromObject(context));
                Set("numberOfInputs", FenValue.FromNumber(1));
                Set("numberOfOutputs", FenValue.FromNumber(1));
                Set("channelCount", FenValue.FromNumber(2));
                Set("channelCountMode", FenValue.FromString("clamped-max"));
                Set("channelInterpretation", FenValue.FromString("speakers"));
                Set("panningModel", FenValue.FromString("equalpower"));
                Set("distanceModel", FenValue.FromString("inverse"));
                Set("refDistance", FenValue.FromNumber(1));
                Set("maxDistance", FenValue.FromNumber(10000));
                Set("rolloffFactor", FenValue.FromNumber(1));
                Set("coneInnerAngle", FenValue.FromNumber(360));
                Set("coneOuterAngle", FenValue.FromNumber(360));
                Set("coneOuterGain", FenValue.FromNumber(0));

                WebAudioApi.BindParam(this, "positionX", new FnAudioParam(0));
                WebAudioApi.BindParam(this, "positionY", new FnAudioParam(0));
                WebAudioApi.BindParam(this, "positionZ", new FnAudioParam(0));
                WebAudioApi.BindParam(this, "orientationX", new FnAudioParam(1));
                WebAudioApi.BindParam(this, "orientationY", new FnAudioParam(0));
                WebAudioApi.BindParam(this, "orientationZ", new FnAudioParam(0));
            }
        }

        internal class FnAnalyserNode : FnAudioNode
        {
            private int _fftSize = 2048;
            private double _minDecibels = -100;
            private double _maxDecibels = -30;
            private double _smoothingTimeConstant = 0.8;

            public FnAnalyserNode(IObject context) : base(context)
            {
                Set("context", FenValue.FromObject(context));
                Set("numberOfInputs", FenValue.FromNumber(1));
                Set("numberOfOutputs", FenValue.FromNumber(1));
                Set("channelCount", FenValue.FromNumber(2));
                Set("channelCountMode", FenValue.FromString("max"));
                Set("channelInterpretation", FenValue.FromString("speakers"));
                Set("fftSize", FenValue.FromNumber(_fftSize));
                Set("frequencyBinCount", FenValue.FromNumber(_fftSize / 2));
                Set("minDecibels", FenValue.FromNumber(_minDecibels));
                Set("maxDecibels", FenValue.FromNumber(_maxDecibels));
                Set("smoothingTimeConstant", FenValue.FromNumber(_smoothingTimeConstant));

                Set("getByteFrequencyData", FenValue.FromFunction(new FenFunction("getByteFrequencyData", (args, thisVal) =>
                {
                    if (args.Length > 0 && args[0].IsObject)
                        FillArrayWithZeros(args[0].AsObject(), _fftSize / 2);
                    return FenValue.Undefined;
                })));

                Set("getByteTimeDomainData", FenValue.FromFunction(new FenFunction("getByteTimeDomainData", (args, thisVal) =>
                {
                    if (args.Length > 0 && args[0].IsObject)
                        FillArrayWithZeros(args[0].AsObject(), _fftSize);
                    return FenValue.Undefined;
                })));

                Set("getFloatFrequencyData", FenValue.FromFunction(new FenFunction("getFloatFrequencyData", (args, thisVal) =>
                {
                    if (args.Length > 0 && args[0].IsObject)
                    {
                        var arr = args[0].AsObject();
                        var len = arr.Get("length");
                        int count = len.IsNumber ? (int)len.ToNumber() : _fftSize / 2;
                        for (int i = 0; i < count; i++)
                            arr.Set(i.ToString(), FenValue.FromNumber(_minDecibels));
                    }
                    return FenValue.Undefined;
                })));
            }

            private static void FillArrayWithZeros(IObject arr, int count)
            {
                var len = arr.Get("length");
                int actual = Math.Min(len.IsNumber ? (int)len.ToNumber() : count, count);
                for (int i = 0; i < actual; i++)
                    arr.Set(i.ToString(), FenValue.FromNumber(0));
            }
        }

        private static FenObject CreateAudioNodePrototype(IExecutionContext context, FenObject ctor, string name)
        {
            var proto = new FenObject();
            proto.Set("constructor", FenValue.FromFunction(ctor is FenFunction fn ? fn : null));

            proto.Set("connect", FenValue.FromFunction(new FenFunction("connect", (args, thisVal) =>
            {
                if (thisVal.IsObject && thisVal.AsObject() is FnAudioNode sourceNode)
                {
                    if (args.Length > 0 && args[0].IsObject && args[0].AsObject() is FnAudioNode destNode)
                    {
                        sourceNode.RegisterConnection(destNode);
                        return args[0];
                    }
                    else if (args.Length > 0 && args[0].IsObject)
                    {
                        return args[0];
                    }
                }
                return FenValue.Undefined;
            })));

            proto.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (args, thisVal) =>
            {
                if (thisVal.IsObject && thisVal.AsObject() is FnAudioNode sourceNode)
                {
                    if (args.Length > 0 && args[0].IsObject && args[0].AsObject() is FnAudioNode destNode)
                    {
                        sourceNode.RemoveConnection(destNode);
                    }
                    else
                    {
                        sourceNode.UnregisterFromAll();
                    }
                }
                return FenValue.Undefined;
            })));

            proto.Set("numberOfInputs", FenValue.FromNumber(0));
            proto.Set("numberOfOutputs", FenValue.FromNumber(0));
            proto.Set("channelCount", FenValue.FromNumber(2));
            proto.Set("channelCountMode", FenValue.FromString("max"));
            proto.Set("channelInterpretation", FenValue.FromString("speakers"));
            proto.Set("context", FenValue.Null);

            return proto;
        }

        internal static void BindParam(FenObject owner, string name, FnAudioParam param)
        {
            DefineAudioParamProperty(owner, name, param);
        }

        internal static FenObject BindParamObj(FnAudioParam param)
        {
            var obj = new FenObject();
            obj.Set("value", FenValue.FromNumber(param.Value));
            obj.Set("setValueAtTime", FenValue.FromFunction(new FenFunction("setValueAtTime", (args, thisVal) =>
            {
                if (args.Length >= 2 && args[0].IsNumber && args[1].IsNumber)
                    param.SetValueAtTime(args[0].ToNumber(), args[1].ToNumber());
                return FenValue.FromObject(obj);
            })));
            obj.Set("linearRampToValueAtTime", FenValue.FromFunction(new FenFunction("linearRampToValueAtTime", (args, thisVal) =>
            {
                if (args.Length >= 2 && args[0].IsNumber && args[1].IsNumber)
                    param.LinearRampToValueAtTime(args[0].ToNumber(), args[1].ToNumber());
                return FenValue.FromObject(obj);
            })));
            obj.Set("exponentialRampToValueAtTime", FenValue.FromFunction(new FenFunction("exponentialRampToValueAtTime", (args, thisVal) =>
            {
                if (args.Length >= 2 && args[0].IsNumber && args[1].IsNumber)
                    param.ExponentialRampToValueAtTime(args[0].ToNumber(), args[1].ToNumber());
                return FenValue.FromObject(obj);
            })));
            obj.Set("setTargetAtTime", FenValue.FromFunction(new FenFunction("setTargetAtTime", (args, thisVal) =>
            {
                if (args.Length >= 3 && args[0].IsNumber && args[1].IsNumber && args[2].IsNumber)
                    param.SetTargetAtTime(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber());
                return FenValue.FromObject(obj);
            })));
            obj.Set("cancelScheduledValues", FenValue.FromFunction(new FenFunction("cancelScheduledValues", (args, thisVal) =>
            {
                if (args.Length >= 1 && args[0].IsNumber)
                    param.CancelScheduledValues(args[0].ToNumber());
                return FenValue.FromObject(obj);
            })));
            obj.Set("cancelAndHoldAtTime", FenValue.FromFunction(new FenFunction("cancelAndHoldAtTime", (args, thisVal) =>
            {
                if (args.Length >= 1 && args[0].IsNumber)
                    param.CancelAndHoldAtTime(args[0].ToNumber());
                return FenValue.FromObject(obj);
            })));
            param.Set("__audioParamTarget", FenValue.FromObject(obj));
            return obj;
        }

        private static void DefineAudioParamProperty(FenObject owner, string name, FnAudioParam param)
        {
            var paramObj = BindParamObj(param);
            owner.Set(name, FenValue.FromObject(paramObj));
        }
    }
}
