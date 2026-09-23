using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Media;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Media.WebAudio;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Web Audio natives (docs/WEB_AUDIO_DESIGN.md). The realm's <see cref="WebAudioPrelude"/>
/// validates every call; these create and change the rendering-thread graph through its
/// control message queue (WA-D3), run offline rendering on a worker thread (WA-D5), and
/// queue what the rendering thread reports back onto the media element task source.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    /// <summary>The default rate of an AudioContext that asked for none.</summary>
    private const float DefaultAudioContextSampleRate = 48000f;

    private sealed class WebAudioContextEntry : IWebAudioEventSink
    {
        private readonly FenJsBrowserScriptEngine _owner;

        public WebAudioContextEntry(FenJsBrowserScriptEngine owner, int id)
        {
            _owner = owner;
            Id = id;
        }

        public int Id { get; }

        public AudioGraph Graph;
        public bool Offline;
        public int Channels;
        public int Length;
        public readonly Dictionary<int, AudioNodeKernel> Nodes = new();
        public readonly Dictionary<int, PeriodicWaveData> Waves = new();
        public int NextWaveId = 1;

        // Offline rendering (WA-D5).
        public OfflineAudioRenderer Renderer;
        public readonly SortedSet<long> SuspendFrames = new();
        public readonly SemaphoreSlim ResumeSignal = new(0, int.MaxValue);
        public int WaitingForResume;

        // AudioContext rendering: paced to real time until device output takes over (WA4).
        public Thread Pacer;
        public volatile bool Running;
        public volatile bool Closed;
        public readonly AutoResetEvent Wake = new(false);

        public void SourceEnded(int nodeId) =>
            _owner.QueueWebAudioHook("__fenWaOnEnded", JsValue.FromInt32(Id), JsValue.FromInt32(nodeId));
    }

    private readonly Dictionary<int, WebAudioContextEntry> _webAudioContexts = new();
    private int _nextWebAudioContextId = 1;

    private void InstallFenJsWebAudio()
    {
        Native("__fenWaCreateContext", 5, args =>
        {
            bool offline = args.Count > 0 && args[0].Tag == JsValueTag.Boolean && args[0].AsBoolean();
            int channels = ArgInt(args, 1);
            int length = ArgInt(args, 2);
            float rate = (float)ArgNumber(args, 3);
            int quantum = ArgInt(args, 4);
            if (rate <= 0)
                rate = DefaultAudioContextSampleRate;
            if (!WebAudioLimits.IsValidSampleRate(rate) || channels < 1 || channels > WebAudioLimits.MaxChannels ||
                quantum < 1 || quantum > WebAudioLimits.MaxRenderQuantumFor(rate))
            {
                return JsValue.FromInt32(0);
            }

            if (offline && (length < 1 || (long)length * channels > WebAudioLimits.MaxBufferSamples))
                return JsValue.FromInt32(0);

            var entry = new WebAudioContextEntry(this, _nextWebAudioContextId++)
            {
                Offline = offline,
                Channels = channels,
                Length = length,
            };
            entry.Graph = new AudioGraph(rate, channels, entry, quantum);
            entry.Nodes[entry.Graph.Destination.Id] = entry.Graph.Destination;
            if (offline)
                entry.Renderer = new OfflineAudioRenderer(entry.Graph, channels, length);
            _webAudioContexts[entry.Id] = entry;
            return JsValue.FromInt32(entry.Id);
        });

        Native("__fenWaSampleRate", 1, args =>
            WebAudioEntry(args) is { } entry ? JsValue.FromNumber(entry.Graph.SampleRate) : JsValue.FromNumber(0));

        Native("__fenWaCurrentTime", 1, args =>
            WebAudioEntry(args) is { } entry ? JsValue.FromNumber(entry.Graph.CurrentTime) : JsValue.FromNumber(0));

        Native("__fenWaCurrentFrame", 1, args =>
            WebAudioEntry(args) is { } entry ? JsValue.FromNumber(entry.Graph.CurrentFrame) : JsValue.FromNumber(0));

        Native("__fenWaOutputLatency", 1, _ => JsValue.FromNumber(0));

        Native("__fenWaAllowedToStart", 0, _ =>
            JsValue.FromBoolean(MediaEngineServices.Autoplay.IsAllowedToPlay(HasTransientActivation, inaudible: false)));

        Native("__fenWaCreateNode", 3, args =>
        {
            if (WebAudioEntry(args) is not { } entry)
                return JsValue.FromInt32(0);

            string kind = ArgString(args, 1);
            double argNumber = ArgNumber(args, 2);
            int arg = ArgInt(args, 2);
            var graph = entry.Graph;
            AudioNodeKernel kernel = kind switch
            {
                "destination" => graph.Destination,
                "gain" => new GainKernel(graph),
                "constant" => new ConstantSourceKernel(graph),
                "buffersource" => new BufferSourceKernel(graph),
                "splitter" => new ChannelSplitterKernel(graph, Math.Clamp(arg, 1, WebAudioLimits.MaxNodePorts)),
                "merger" => new ChannelMergerKernel(graph, Math.Clamp(arg, 1, WebAudioLimits.MaxNodePorts)),
                "oscillator" => new OscillatorKernel(graph),
                "delay" => argNumber > 0 && argNumber < 180 ? new DelayKernel(graph, argNumber) : null,
                "biquad" => new BiquadKernel(graph),
                "iir" => new IirFilterKernel(graph),
                "waveshaper" => new WaveShaperKernel(graph),
                "stereopanner" => new StereoPannerKernel(graph),
                _ => null,
            };
            if (kernel == null)
                return JsValue.FromInt32(0);

            if (!ReferenceEquals(kernel, graph.Destination))
            {
                entry.Nodes[kernel.Id] = kernel;
                graph.Post(g => g.AddNode(kernel));
            }

            return JsValue.FromInt32(kernel.Id);
        });

        Native("__fenWaConnect", 5, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is { } source && Kernel(entry, args, 3) is { } destination)
            {
                int output = ArgInt(args, 2), input = ArgInt(args, 4);
                if (output < source.Outputs.Length && input < destination.Inputs.Length)
                    entry.Graph.Post(g => g.Connect(source, output, destination, input));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaConnectParam", 5, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is { } source && Kernel(entry, args, 3) is { } owner &&
                owner.FindParam(ArgString(args, 4)) is { } param)
            {
                int output = ArgInt(args, 2);
                if (output < source.Outputs.Length)
                    entry.Graph.Post(g => g.Connect(source, output, param));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaDisconnect", 5, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is { } source && Kernel(entry, args, 3) is { } destination)
            {
                int output = ArgInt(args, 2), input = ArgInt(args, 4);
                entry.Graph.Post(g => g.Disconnect(source, output, destination, input));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaDisconnectParam", 5, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is { } source && Kernel(entry, args, 3) is { } owner &&
                owner.FindParam(ArgString(args, 4)) is { } param)
            {
                int output = ArgInt(args, 2);
                entry.Graph.Post(g => g.Disconnect(source, output, param));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaChannelConfig", 5, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is { } node)
            {
                int count = Math.Clamp(ArgInt(args, 2), 1, WebAudioLimits.MaxChannels);
                var mode = (ChannelCountMode)Math.Clamp(ArgInt(args, 3), 0, 2);
                var interpretation = (ChannelInterpretation)Math.Clamp(ArgInt(args, 4), 0, 1);
                entry.Graph.Post(_ =>
                {
                    node.ChannelCount = count;
                    node.ChannelCountMode = mode;
                    node.ChannelInterpretation = interpretation;
                });
            }

            return JsValue.Undefined;
        });

        Native("__fenWaParam", 7, args => WebAudioParamCall(args));

        Native("__fenWaParamCurve", 6, args =>
        {
            if (WebAudioEntry(args) is not { } entry || Kernel(entry, args, 1) is not { } node || node.FindParam(ArgString(args, 2)) is not { } param)
                return JsValue.Undefined;

            var values = ReadFloats(args.Count > 3 ? args[3] : JsValue.Undefined);
            if (values.Length < 2)
                return JsValue.Undefined;

            double start = Math.Max(ArgNumber(args, 4), entry.Graph.CurrentTime);
            double duration = ArgNumber(args, 5);
            var error = param.Timeline.SetValueCurveAtTime(values, start, duration);
            return error == AutomationError.None ? JsValue.Undefined : JsValue.FromString("NotSupportedError");
        });

        Native("__fenWaSourceStart", 5, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is ScheduledSourceKernel source)
            {
                double when = ArgNumber(args, 2);
                double offset = ArgNumber(args, 3);
                double duration = args.Count > 4 && (args[4].Tag == JsValueTag.Number || args[4].Tag == JsValueTag.Int32)
                    ? args[4].AsNumber()
                    : double.PositiveInfinity;

                // WA 1.3.2 start(): a time already past means as soon as possible, which is
                // the next quantum the rendering thread renders.
                entry.Graph.Post(g =>
                {
                    double at = Math.Max(when, g.CurrentTime);
                    if (source is BufferSourceKernel bufferSource)
                        bufferSource.Start(at, offset, duration);
                    else
                        source.Start(at);
                });
            }

            return JsValue.Undefined;
        });

        Native("__fenWaSourceStop", 3, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is ScheduledSourceKernel source)
            {
                double when = ArgNumber(args, 2);
                entry.Graph.Post(g => source.Stop(Math.Max(when, g.CurrentTime)));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaSetBuffer", 4, args =>
        {
            if (WebAudioEntry(args) is not { } entry || Kernel(entry, args, 1) is not BufferSourceKernel source)
                return JsValue.Undefined;

            AudioBufferData data = null;
            if (args.Count > 2 && args[2].Tag == JsValueTag.Object)
            {
                float rate = (float)ArgNumber(args, 3);
                int channelCount = (int)ReadJsProperty(args[2], "length").AsNumber();
                if (channelCount >= 1 && channelCount <= WebAudioLimits.MaxChannels && WebAudioLimits.IsValidSampleRate(rate))
                {
                    var channels = new float[channelCount][];
                    for (int c = 0; c < channelCount; c++)
                        channels[c] = ReadFloats(ReadJsProperty(args[2], c.ToString(CultureInfo.InvariantCulture)));
                    try
                    {
                        data = new AudioBufferData(channels, rate);
                    }
                    catch (ArgumentException ex)
                    {
                        EngineLogCompat.Warn($"[WebAudio] buffer refused: {ex.Message}", LogCategory.JavaScript);
                    }
                }
            }

            entry.Graph.Post(_ => source.Buffer = data);
            return JsValue.Undefined;
        });

        Native("__fenWaSetLoop", 5, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is BufferSourceKernel source)
            {
                bool loop = args.Count > 2 && args[2].Tag == JsValueTag.Boolean && args[2].AsBoolean();
                double loopStart = ArgNumber(args, 3), loopEnd = ArgNumber(args, 4);
                entry.Graph.Post(_ =>
                {
                    source.Loop = loop;
                    source.LoopStart = loopStart;
                    source.LoopEnd = loopEnd;
                });
            }

            return JsValue.Undefined;
        });

        // ---- WA2 ------------------------------------------------------------------------

        Native("__fenWaCreatePeriodicWave", 4, args =>
        {
            if (WebAudioEntry(args) is not { } entry)
                return JsValue.FromInt32(0);
            var real = ReadFloats(args.Count > 1 ? args[1] : JsValue.Undefined);
            var imag = ReadFloats(args.Count > 2 ? args[2] : JsValue.Undefined);
            bool disable = args.Count > 3 && args[3].Tag == JsValueTag.Boolean && args[3].AsBoolean();
            if (real.Length != imag.Length || real.Length < 2 || real.Length > 1 << 20)
                return JsValue.FromInt32(0);
            int id = entry.NextWaveId++;
            entry.Waves[id] = new PeriodicWaveData(real, imag, disable);
            return JsValue.FromInt32(id);
        });

        Native("__fenWaOscillatorType", 3, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is OscillatorKernel oscillator)
            {
                var waveform = (OscillatorWaveform)Math.Clamp(ArgInt(args, 2), 0, 3);
                var wave = PeriodicWaveData.BuiltIn(waveform, PeriodicWaveData.TableSizeFor(entry.Graph.SampleRate));
                entry.Graph.Post(_ => oscillator.SetWave(wave));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaOscillatorWave", 3, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is OscillatorKernel oscillator &&
                entry.Waves.TryGetValue(ArgInt(args, 2), out var wave))
            {
                entry.Graph.Post(_ => oscillator.SetWave(wave));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaBiquadType", 3, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is BiquadKernel biquad)
            {
                var type = (BiquadFilterType)Math.Clamp(ArgInt(args, 2), 0, 7);
                entry.Graph.Post(_ => biquad.Type = type);
            }

            return JsValue.Undefined;
        });

        // WA 1.10.3 getFrequencyResponse: from the params' current values, at the given
        // frequencies; one outside [0, Nyquist] answers NaN.
        Native("__fenWaBiquadResponse", 5, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is BiquadKernel biquad && args.Count > 4)
            {
                // The realm's type, not the kernel's: a type change reaches the kernel only
                // when the rendering thread next runs, and this answer is wanted now.
                var type = args.Count > 5 ? (BiquadFilterType)Math.Clamp(ArgInt(args, 5), 0, 7) : biquad.Type;
                var coefficients = BiquadKernel.Coefficients(
                    type, biquad.Frequency.CurrentValue, biquad.Detune.CurrentValue, biquad.Q.CurrentValue, biquad.Gain.CurrentValue, entry.Graph.SampleRate);
                WriteFrequencyResponse(entry, args[2], args[3], args[4], omega => coefficients.Response(omega));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaIirCoefficients", 4, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is IirFilterKernel iir)
            {
                var feedforward = ReadDoubles(args.Count > 2 ? args[2] : JsValue.Undefined);
                var feedback = ReadDoubles(args.Count > 3 ? args[3] : JsValue.Undefined);
                if (feedforward.Length is >= 1 and <= 20 && feedback.Length is >= 1 and <= 20 && feedback[0] != 0)
                    entry.Graph.Post(_ => iir.SetCoefficients(feedforward, feedback));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaIirResponse", 6, args =>
        {
            if (WebAudioEntry(args) is { } entry && args.Count > 5)
            {
                var feedforward = ReadDoubles(args[1]);
                var feedback = ReadDoubles(args[2]);
                if (feedforward.Length > 0 && feedback.Length > 0 && feedback[0] != 0)
                    WriteFrequencyResponse(entry, args[3], args[4], args[5], omega => IirFilterKernel.Response(feedforward, feedback, omega));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaWaveShaperCurve", 3, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is WaveShaperKernel shaper)
            {
                float[] curve = args.Count > 2 && args[2].Tag == JsValueTag.Object ? ReadFloats(args[2]) : null;
                if (curve is { Length: < 2 })
                    curve = null;
                entry.Graph.Post(_ => shaper.Curve = curve);
            }

            return JsValue.Undefined;
        });

        Native("__fenWaWaveShaperOversample", 3, args =>
        {
            if (WebAudioEntry(args) is { } entry && Kernel(entry, args, 1) is WaveShaperKernel shaper)
            {
                var oversample = (OverSampleType)Math.Clamp(ArgInt(args, 2), 0, 2);
                entry.Graph.Post(_ => shaper.Oversample = oversample);
            }

            return JsValue.Undefined;
        });

        Native("__fenWaStartRendering", 1, args =>
        {
            if (WebAudioEntry(args) is { Offline: true } entry)
                StartOfflineRendering(entry);
            return JsValue.Undefined;
        });

        Native("__fenWaScheduleSuspend", 2, args =>
        {
            if (WebAudioEntry(args) is { Offline: true } entry)
            {
                lock (entry.SuspendFrames)
                    entry.SuspendFrames.Add((long)ArgNumber(args, 1));
            }

            return JsValue.Undefined;
        });

        Native("__fenWaResume", 1, args =>
        {
            if (WebAudioEntry(args) is not { } entry)
                return JsValue.Undefined;

            if (entry.Offline)
            {
                if (Interlocked.Exchange(ref entry.WaitingForResume, 0) == 1)
                    entry.ResumeSignal.Release();
            }
            else if (!entry.Closed)
            {
                entry.Running = true;
                EnsureWebAudioPacer(entry);
                entry.Wake.Set();
            }

            return JsValue.Undefined;
        });

        Native("__fenWaSuspend", 1, args =>
        {
            if (WebAudioEntry(args) is { Offline: false } entry)
                entry.Running = false;
            return JsValue.Undefined;
        });

        Native("__fenWaClose", 1, args =>
        {
            if (WebAudioEntry(args) is { Offline: false } entry)
            {
                entry.Running = false;
                entry.Closed = true;
                entry.Wake.Set();
            }

            return JsValue.Undefined;
        });

        Native("__fenWaReadResult", 3, args =>
        {
            if (WebAudioEntry(args) is { Offline: true, Renderer: { IsComplete: true } renderer } && args.Count > 2)
            {
                int channel = ArgInt(args, 1);
                if (channel >= 0 && channel < renderer.Result.Length)
                    WriteFloats(args[2], renderer.Result[channel]);
            }

            return JsValue.Undefined;
        });

        try
        {
            EvaluateWithFenJsRaw(WebAudioPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] web audio prelude failed: {ex.Message}", LogCategory.JavaScript);
        }

        void Native(string name, int length, Func<IReadOnlyList<JsValue>, JsValue> body) =>
            _interpreter.RegisterGlobalValue(name, _interpreter.AllocateNativeFunction(name, (_, args) => body(args), length: length));
    }

    // AudioParam operations (WA 1.6.2). Times before currentTime are clamped to it; the
    // realm has already turned bad arguments into exceptions.
    private JsValue WebAudioParamCall(IReadOnlyList<JsValue> args)
    {
        if (WebAudioEntry(args) is not { } entry || Kernel(entry, args, 1) is not { } node || node.FindParam(ArgString(args, 2)) is not { } param)
            return JsValue.Undefined;

        string op = ArgString(args, 3);
        double a = ArgNumber(args, 4), b = ArgNumber(args, 5), c = ArgNumber(args, 6);
        double now = entry.Graph.CurrentTime;
        var timeline = param.Timeline;
        AutomationError error = AutomationError.None;
        switch (op)
        {
            case "get":
                return JsValue.FromNumber(param.CurrentValue);
            case "setValue":
                // WA 1.6 value setter: set [[current value]], then setValueAtTime(value, currentTime).
                param.CurrentValue = (float)a;
                error = timeline.SetValueAtTime(a, now);
                break;
            case "setValueAtTime":
                error = timeline.SetValueAtTime(a, Math.Max(b, now));
                break;
            case "linearRamp":
                error = timeline.LinearRampToValueAtTime(a, Math.Max(b, now), now, param.CurrentValue);
                break;
            case "exponentialRamp":
                error = timeline.ExponentialRampToValueAtTime(a, Math.Max(b, now), now, param.CurrentValue);
                break;
            case "setTarget":
                error = timeline.SetTargetAtTime(a, Math.Max(b, now), c);
                break;
            case "cancel":
                timeline.CancelScheduledValues(Math.Max(b, now));
                break;
            case "cancelAndHold":
                timeline.CancelAndHoldAtTime(Math.Max(b, now));
                break;
            case "rate":
                var rate = a == 1 ? AutomationRate.KRate : AutomationRate.ARate;
                if (!param.RateFixed)
                    entry.Graph.Post(_ => param.Rate = rate);
                break;
        }

        return error == AutomationError.None ? JsValue.Undefined : JsValue.FromString("NotSupportedError");
    }

    // WA-D5: offline rendering runs on a worker thread, stopping before any quantum a
    // suspend() asked for until resume().
    private void StartOfflineRendering(WebAudioContextEntry entry)
    {
        var renderer = entry.Renderer;
        _ = Task.Run(() =>
        {
            try
            {
                while (!renderer.IsComplete && !_realmAbandoned)
                {
                    long frame = entry.Graph.CurrentFrame;
                    bool suspend;
                    lock (entry.SuspendFrames)
                        suspend = entry.SuspendFrames.Remove(frame);

                    if (suspend)
                    {
                        // Messages posted before the suspend belong to the state script sees.
                        entry.Graph.DrainControlMessages();
                        Volatile.Write(ref entry.WaitingForResume, 1);
                        QueueWebAudioHook("__fenWaOnSuspended", JsValue.FromInt32(entry.Id), JsValue.FromNumber(frame));
                        while (!entry.ResumeSignal.Wait(250))
                        {
                            if (_realmAbandoned)
                                return;
                        }
                    }

                    renderer.RenderQuantum();
                }

                if (renderer.IsComplete)
                    QueueWebAudioHook("__fenWaOnComplete", JsValue.FromInt32(entry.Id));
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[WebAudio] offline rendering failed: {ex.Message}", LogCategory.JavaScript);
            }
        });
    }

    // Until device output (WA4) an AudioContext renders on a thread paced to real time, so
    // currentTime advances and sources end as they would when heard.
    private void EnsureWebAudioPacer(WebAudioContextEntry entry)
    {
        if (entry.Pacer != null)
            return;

        var graph = entry.Graph;
        var thread = new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            long renderedFrames = 0;
            double startOffset = 0;
            bool wasRunning = false;
            try
            {
                while (!entry.Closed && !_realmAbandoned)
                {
                    if (!entry.Running)
                    {
                        wasRunning = false;
                        graph.DrainControlMessages();
                        entry.Wake.WaitOne(50);
                        continue;
                    }

                    if (!wasRunning)
                    {
                        wasRunning = true;
                        clock.Restart();
                        startOffset = renderedFrames;
                    }

                    double due = startOffset + clock.Elapsed.TotalSeconds * graph.SampleRate;
                    if (renderedFrames + graph.QuantumFrames <= due)
                    {
                        graph.RenderQuantum();
                        renderedFrames += graph.QuantumFrames;
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[WebAudio] audio context rendering failed: {ex.Message}", LogCategory.JavaScript);
            }
        })
        {
            IsBackground = true,
            Name = "WebAudio context " + entry.Id.ToString(CultureInfo.InvariantCulture),
        };
        entry.Pacer = thread;
        thread.Start();
    }

    // Everything the rendering thread tells the page is a task on the media element task
    // source (WA 2.2), so an event never fires inside the script call that caused it.
    private void QueueWebAudioHook(string name, params JsValue[] args)
    {
        if (_realmAbandoned)
            return;

        QueueMediaTask(null, () =>
        {
            var hook = ReadGlobalValueOrUndefined(name);
            if (!_interpreter.CanCallValue(hook))
                return;
            try
            {
                _ = _interpreter.InvokeFunction(hook, args, JsValue.Undefined);
            }
            catch (Js.Interpreter.JsThrownException ex)
            {
                EngineLogCompat.Warn($"[FenJsBridge] {name} failed: {ex.Description ?? ex.Message}", LogCategory.JavaScript);
                ReportFenJsException(ex);
            }
        });
    }

    private WebAudioContextEntry WebAudioEntry(IReadOnlyList<JsValue> args) =>
        args.Count > 0 && _webAudioContexts.TryGetValue(ArgInt(args, 0), out var entry) ? entry : null;

    private static AudioNodeKernel Kernel(WebAudioContextEntry entry, IReadOnlyList<JsValue> args, int index) =>
        entry.Nodes.TryGetValue(ArgInt(args, index), out var node) ? node : null;

    private static int ArgInt(IReadOnlyList<JsValue> args, int index)
    {
        double n = ArgNumber(args, index);
        return double.IsFinite(n) ? (int)Math.Clamp(n, int.MinValue, int.MaxValue) : 0;
    }

    private static double ArgNumber(IReadOnlyList<JsValue> args, int index)
    {
        if (index >= args.Count)
            return 0;
        var v = args[index];
        return v.Tag == JsValueTag.Number || v.Tag == JsValueTag.Int32 ? v.AsNumber() : v.Tag == JsValueTag.Boolean && v.AsBoolean() ? 1 : 0;
    }

    private string ArgString(IReadOnlyList<JsValue> args, int index) =>
        index < args.Count && args[index].Tag == JsValueTag.String ? CoerceToHostString(args[index]) : string.Empty;

    // Writes a filter's magnitude and phase response at each requested frequency.
    private void WriteFrequencyResponse(WebAudioContextEntry entry, JsValue frequencies, JsValue magnitudes, JsValue phases, Func<double, (double Magnitude, double Phase)> response)
    {
        var hz = ReadFloats(frequencies);
        var magnitude = new float[hz.Length];
        var phase = new float[hz.Length];
        double nyquist = entry.Graph.SampleRate / 2.0;
        for (int i = 0; i < hz.Length; i++)
        {
            double f = hz[i];
            if (!(f >= 0 && f <= nyquist))
            {
                magnitude[i] = float.NaN;
                phase[i] = float.NaN;
                continue;
            }

            var (m, p) = response(Math.PI * f / nyquist);
            magnitude[i] = (float)m;
            phase[i] = (float)p;
        }

        WriteFloats(magnitudes, magnitude);
        WriteFloats(phases, phase);
    }

    // A Float64Array's values, copied.
    private double[] ReadDoubles(JsValue value)
    {
        var bytes = ExtractBytesFromArrayLike(value);
        return MemoryMarshal.Cast<byte, double>(bytes.AsSpan(0, bytes.Length & ~7)).ToArray();
    }

    // A Float32Array's samples, copied (WA 1.4 "acquire the content").
    private float[] ReadFloats(JsValue value)
    {
        var bytes = ExtractBytesFromArrayLike(value);
        return MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, bytes.Length & ~3)).ToArray();
    }

    // Writes samples straight into a Float32Array the realm allocated.
    private void WriteFloats(JsValue target, float[] samples)
    {
        if (target.Tag != JsValueTag.Object)
            return;
        if (_interpreter.Heap.GetObject(target.AsObjectHandle()) is not TypedArrayView view || view.IsViewDetached || view.IsViewOutOfBounds())
            return;
        var destination = view.Buffer.Data.AsSpan(view.ByteOffset, view.ByteLength);
        var source = MemoryMarshal.AsBytes(samples.AsSpan());
        source[..Math.Min(source.Length, destination.Length)].CopyTo(destination);
    }
}
