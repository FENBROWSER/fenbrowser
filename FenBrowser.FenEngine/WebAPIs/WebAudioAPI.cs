using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Web Audio API implementation
    /// Provides audio processing and synthesis capabilities
    /// </summary>
    public static class WebAudioAPI
    {
        private static int _contextIdCounter = 0;

        /// <summary>
        /// Creates the AudioContext constructor
        /// </summary>
        public static FenObject CreateAudioContextConstructor(IExecutionContext context)
        {
            var constructor = new FenObject();

            // Make it callable as new AudioContext()
            constructor.Set("__call__", FenValue.FromFunction(new FenFunction("AudioContext", (args, thisVal) =>
            {
                return FenValue.FromObject(CreateAudioContext(context));
            })));

            return constructor;
        }

        /// <summary>
        /// Creates an AudioContext instance
        /// </summary>
        public static FenObject CreateAudioContext(IExecutionContext context)
        {
            var contextId = ++_contextIdCounter;
            var ctx = new FenObject();
            var state = "running";
            var sampleRate = 44100.0;
            var currentTime = 0.0;

            // Properties
            ctx.Set("sampleRate", FenValue.FromNumber(sampleRate));
            ctx.Set("state", FenValue.FromString(state));
            ctx.Set("currentTime", FenValue.FromNumber(currentTime));
            ctx.Set("baseLatency", FenValue.FromNumber(0.01));
            ctx.Set("outputLatency", FenValue.FromNumber(0.02));

            // destination (AudioDestinationNode)
            var destination = CreateAudioDestinationNode(ctx);
            ctx.Set("destination", FenValue.FromObject(destination));

            // listener (AudioListener)
            var listener = CreateAudioListener();
            ctx.Set("listener", FenValue.FromObject(listener));

            // Methods
            ctx.Set("createOscillator", FenValue.FromFunction(new FenFunction("createOscillator", (args, thisVal) =>
            {
                FenLogger.Debug("[WebAudio] createOscillator()", LogCategory.JavaScript);
                return FenValue.FromObject(CreateOscillatorNode(ctx));
            })));

            ctx.Set("createGain", FenValue.FromFunction(new FenFunction("createGain", (args, thisVal) =>
            {
                FenLogger.Debug("[WebAudio] createGain()", LogCategory.JavaScript);
                return FenValue.FromObject(CreateGainNode(ctx));
            })));

            ctx.Set("createAnalyser", FenValue.FromFunction(new FenFunction("createAnalyser", (args, thisVal) =>
            {
                FenLogger.Debug("[WebAudio] createAnalyser()", LogCategory.JavaScript);
                return FenValue.FromObject(CreateAnalyserNode(ctx));
            })));

            ctx.Set("createBufferSource", FenValue.FromFunction(new FenFunction("createBufferSource", (args, thisVal) =>
            {
                FenLogger.Debug("[WebAudio] createBufferSource()", LogCategory.JavaScript);
                return FenValue.FromObject(CreateBufferSourceNode(ctx));
            })));

            ctx.Set("createBiquadFilter", FenValue.FromFunction(new FenFunction("createBiquadFilter", (args, thisVal) =>
            {
                return FenValue.FromObject(CreateBiquadFilterNode(ctx));
            })));

            ctx.Set("createConvolver", FenValue.FromFunction(new FenFunction("createConvolver", (args, thisVal) =>
            {
                return FenValue.FromObject(CreateConvolverNode(ctx));
            })));

            ctx.Set("createDelay", FenValue.FromFunction(new FenFunction("createDelay", (args, thisVal) =>
            {
                double maxDelay = args.Length > 0 ? args[0].ToNumber() : 1.0;
                return FenValue.FromObject(CreateDelayNode(ctx, maxDelay));
            })));

            ctx.Set("createDynamicsCompressor", FenValue.FromFunction(new FenFunction("createDynamicsCompressor", (args, thisVal) =>
            {
                return FenValue.FromObject(CreateDynamicsCompressorNode(ctx));
            })));

            ctx.Set("createBuffer", FenValue.FromFunction(new FenFunction("createBuffer", (args, thisVal) =>
            {
                int channels = args.Length > 0 ? (int)args[0].ToNumber() : 2;
                int length = args.Length > 1 ? (int)args[1].ToNumber() : 44100;
                double sr = args.Length > 2 ? args[2].ToNumber() : sampleRate;
                return FenValue.FromObject(CreateAudioBuffer(channels, length, sr));
            })));

            ctx.Set("decodeAudioData", FenValue.FromFunction(new FenFunction("decodeAudioData", (args, thisVal) =>
            {
                FenLogger.Debug("[WebAudio] decodeAudioData()", LogCategory.JavaScript);
                
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("executor", (execArgs, execThis) => 
                {
                    var resolve = execArgs[0].AsFunction();
                    Task.Run(async () => 
                    {
                        await Task.Delay(50); // Simulate decode
                        var buffer = CreateAudioBuffer(2, 44100, sampleRate);
                        context.ScheduleCallback(() => {
                            resolve.Invoke(new[] { FenValue.FromObject(buffer) }, context);
                        }, 0);
                    });
                    return FenValue.Undefined;
                })), context));
            })));

            ctx.Set("suspend", FenValue.FromFunction(new FenFunction("suspend", (args, thisVal) =>
            {
                FenLogger.Debug("[WebAudio] suspend()", LogCategory.JavaScript);
                ctx.Set("state", FenValue.FromString("suspended"));
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("ex", (eArgs, eThis) => {
                    eArgs[0].AsFunction().Invoke(new FenValue[0], context);
                    return FenValue.Undefined;
                })), context));
            })));

            ctx.Set("resume", FenValue.FromFunction(new FenFunction("resume", (args, thisVal) =>
            {
                FenLogger.Debug("[WebAudio] resume()", LogCategory.JavaScript);
                ctx.Set("state", FenValue.FromString("running"));
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("ex", (eArgs, eThis) => {
                    eArgs[0].AsFunction().Invoke(new FenValue[0], context);
                    return FenValue.Undefined;
                })), context));
            })));

            ctx.Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                FenLogger.Debug("[WebAudio] close()", LogCategory.JavaScript);
                ctx.Set("state", FenValue.FromString("closed"));
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("ex", (eArgs, eThis) => {
                    eArgs[0].AsFunction().Invoke(new FenValue[0], context);
                    return FenValue.Undefined;
                })), context));
            })));

            FenLogger.Debug($"[WebAudio] AudioContext #{contextId} created", LogCategory.JavaScript);
            return ctx;
        }

        private static FenObject CreateAudioDestinationNode(FenObject context)
        {
            var node = CreateBaseAudioNode(context, "destination");
            node.Set("maxChannelCount", FenValue.FromNumber(2));
            return node;
        }

        private static FenObject CreateAudioListener()
        {
            var listener = new FenObject();
            listener.Set("positionX", FenValue.FromNumber(0));
            listener.Set("positionY", FenValue.FromNumber(0));
            listener.Set("positionZ", FenValue.FromNumber(0));
            listener.Set("forwardX", FenValue.FromNumber(0));
            listener.Set("forwardY", FenValue.FromNumber(0));
            listener.Set("forwardZ", FenValue.FromNumber(-1));
            listener.Set("upX", FenValue.FromNumber(0));
            listener.Set("upY", FenValue.FromNumber(1));
            listener.Set("upZ", FenValue.FromNumber(0));
            return listener;
        }

        private static FenObject CreateOscillatorNode(FenObject context)
        {
            var node = CreateBaseAudioNode(context, "oscillator");
            node.Set("type", FenValue.FromString("sine")); // sine, square, sawtooth, triangle
            node.Set("frequency", FenValue.FromObject(CreateAudioParam(440)));
            node.Set("detune", FenValue.FromObject(CreateAudioParam(0)));
            
            node.Set("start", FenValue.FromFunction(new FenFunction("start", (args, thisVal) =>
            {
                FenLogger.Debug("[WebAudio] OscillatorNode.start()", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));
            
            node.Set("stop", FenValue.FromFunction(new FenFunction("stop", (args, thisVal) =>
            {
                FenLogger.Debug("[WebAudio] OscillatorNode.stop()", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));
            
            return node;
        }

        private static FenObject CreateGainNode(FenObject context)
        {
            var node = CreateBaseAudioNode(context, "gain");
            node.Set("gain", FenValue.FromObject(CreateAudioParam(1)));
            return node;
        }

        private static FenObject CreateAnalyserNode(FenObject context)
        {
            var node = CreateBaseAudioNode(context, "analyser");
            node.Set("fftSize", FenValue.FromNumber(2048));
            node.Set("frequencyBinCount", FenValue.FromNumber(1024));
            node.Set("minDecibels", FenValue.FromNumber(-100));
            node.Set("maxDecibels", FenValue.FromNumber(-30));
            node.Set("smoothingTimeConstant", FenValue.FromNumber(0.8));
            
            node.Set("getByteFrequencyData", FenValue.FromFunction(new FenFunction("getByteFrequencyData", (args, thisVal) =>
            {
                // Would fill the Uint8Array with frequency data
                return FenValue.Undefined;
            })));
            
            node.Set("getByteTimeDomainData", FenValue.FromFunction(new FenFunction("getByteTimeDomainData", (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));
            
            return node;
        }

        private static FenObject CreateBufferSourceNode(FenObject context)
        {
            var node = CreateBaseAudioNode(context, "bufferSource");
            node.Set("buffer", FenValue.Null);
            node.Set("loop", FenValue.FromBoolean(false));
            node.Set("loopStart", FenValue.FromNumber(0));
            node.Set("loopEnd", FenValue.FromNumber(0));
            node.Set("playbackRate", FenValue.FromObject(CreateAudioParam(1)));
            
            node.Set("start", FenValue.FromFunction(new FenFunction("start", (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));
            
            node.Set("stop", FenValue.FromFunction(new FenFunction("stop", (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));
            
            return node;
        }

        private static FenObject CreateBiquadFilterNode(FenObject context)
        {
            var node = CreateBaseAudioNode(context, "biquadFilter");
            node.Set("type", FenValue.FromString("lowpass"));
            node.Set("frequency", FenValue.FromObject(CreateAudioParam(350)));
            node.Set("Q", FenValue.FromObject(CreateAudioParam(1)));
            node.Set("gain", FenValue.FromObject(CreateAudioParam(0)));
            node.Set("detune", FenValue.FromObject(CreateAudioParam(0)));
            return node;
        }

        private static FenObject CreateConvolverNode(FenObject context)
        {
            var node = CreateBaseAudioNode(context, "convolver");
            node.Set("buffer", FenValue.Null);
            node.Set("normalize", FenValue.FromBoolean(true));
            return node;
        }

        private static FenObject CreateDelayNode(FenObject context, double maxDelay)
        {
            var node = CreateBaseAudioNode(context, "delay");
            node.Set("delayTime", FenValue.FromObject(CreateAudioParam(0)));
            return node;
        }

        private static FenObject CreateDynamicsCompressorNode(FenObject context)
        {
            var node = CreateBaseAudioNode(context, "dynamicsCompressor");
            node.Set("threshold", FenValue.FromObject(CreateAudioParam(-24)));
            node.Set("knee", FenValue.FromObject(CreateAudioParam(30)));
            node.Set("ratio", FenValue.FromObject(CreateAudioParam(12)));
            node.Set("attack", FenValue.FromObject(CreateAudioParam(0.003)));
            node.Set("release", FenValue.FromObject(CreateAudioParam(0.25)));
            node.Set("reduction", FenValue.FromNumber(0));
            return node;
        }

        private static FenObject CreateBaseAudioNode(FenObject context, string nodeType)
        {
            var node = new FenObject();
            node.Set("context", FenValue.FromObject(context));
            node.Set("numberOfInputs", FenValue.FromNumber(1));
            node.Set("numberOfOutputs", FenValue.FromNumber(1));
            node.Set("channelCount", FenValue.FromNumber(2));
            node.Set("channelCountMode", FenValue.FromString("max"));
            node.Set("channelInterpretation", FenValue.FromString("speakers"));
            node.Set("_nodeType", FenValue.FromString(nodeType));

            node.Set("connect", FenValue.FromFunction(new FenFunction("connect", (args, thisVal) =>
            {
                FenLogger.Debug($"[WebAudio] {nodeType}.connect()", LogCategory.JavaScript);
                if (args.Length > 0) return args[0];
                return FenValue.Undefined;
            })));

            node.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (args, thisVal) =>
            {
                FenLogger.Debug($"[WebAudio] {nodeType}.disconnect()", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));

            return node;
        }

        private static FenObject CreateAudioParam(double defaultValue)
        {
            var param = new FenObject();
            param.Set("value", FenValue.FromNumber(defaultValue));
            param.Set("defaultValue", FenValue.FromNumber(defaultValue));
            param.Set("minValue", FenValue.FromNumber(-3.4028235e38));
            param.Set("maxValue", FenValue.FromNumber(3.4028235e38));

            param.Set("setValueAtTime", FenValue.FromFunction(new FenFunction("setValueAtTime", (args, thisVal) =>
            {
                if (args.Length > 0)
                    param.Set("value", FenValue.FromNumber(args[0].ToNumber()));
                return FenValue.FromObject(param);
            })));

            param.Set("linearRampToValueAtTime", FenValue.FromFunction(new FenFunction("linearRampToValueAtTime", (args, thisVal) =>
            {
                if (args.Length > 0)
                    param.Set("value", FenValue.FromNumber(args[0].ToNumber()));
                return FenValue.FromObject(param);
            })));

            param.Set("exponentialRampToValueAtTime", FenValue.FromFunction(new FenFunction("exponentialRampToValueAtTime", (args, thisVal) =>
            {
                if (args.Length > 0)
                    param.Set("value", FenValue.FromNumber(args[0].ToNumber()));
                return FenValue.FromObject(param);
            })));

            param.Set("setTargetAtTime", FenValue.FromFunction(new FenFunction("setTargetAtTime", (args, thisVal) =>
            {
                if (args.Length > 0)
                    param.Set("value", FenValue.FromNumber(args[0].ToNumber()));
                return FenValue.FromObject(param);
            })));

            param.Set("cancelScheduledValues", FenValue.FromFunction(new FenFunction("cancelScheduledValues", (args, thisVal) =>
            {
                return FenValue.FromObject(param);
            })));

            return param;
        }

        private static FenObject CreateAudioBuffer(int channels, int length, double sampleRate)
        {
            var buffer = new FenObject();
            buffer.Set("sampleRate", FenValue.FromNumber(sampleRate));
            buffer.Set("length", FenValue.FromNumber(length));
            buffer.Set("duration", FenValue.FromNumber(length / sampleRate));
            buffer.Set("numberOfChannels", FenValue.FromNumber(channels));

            buffer.Set("getChannelData", FenValue.FromFunction(new FenFunction("getChannelData", (args, thisVal) =>
            {
                // Return an empty Float32Array-like
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(length));
                return FenValue.FromObject(arr);
            })));

            buffer.Set("copyFromChannel", FenValue.FromFunction(new FenFunction("copyFromChannel", (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));

            buffer.Set("copyToChannel", FenValue.FromFunction(new FenFunction("copyToChannel", (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));

            return buffer;
        }
    }
}
