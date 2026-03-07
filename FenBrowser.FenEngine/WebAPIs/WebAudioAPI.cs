using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Web Audio API implementation
    /// Provides audio processing and synthesis capabilities
    /// </summary>
    public static class WebAudioAPI
    {
        private const int MaxAudioSourceLength = 2048;
        private const int MaxAudioDataUriLength = 65536;
        private const int MaxConcurrentAudioPlaybacks = 32;

        private static readonly HashSet<string> ProbablyPlayableAudioTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audio/mpeg",
            "audio/mp3",
            "audio/wav",
            "audio/x-wav"
        };

        private static readonly HashSet<string> MaybePlayableAudioTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audio/ogg",
            "audio/webm",
            "audio/aac",
            "audio/mp4"
        };

        private static int _contextIdCounter = 0;
        private static int _activeAudioPlaybacks = 0;

        private static Task RunDetachedAsync(Func<Task> operation)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[WebAudio] Detached async operation failed: {ex.Message}", LogCategory.JavaScript);
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        /// <summary>
        /// Creates the AudioContext constructor
        /// </summary>
        public static FenObject CreateAudioContextConstructor(IExecutionContext context)
        {
            var ctor = new FenFunction("AudioContext", (args, thisVal) =>
            {
                return FenValue.FromObject(CreateAudioContext(context));
            })
            {
                IsConstructor = true
            };

            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(ctor));
            ctor.Set("prototype", FenValue.FromObject(prototype));
            return ctor;
        }

        /// <summary>
        /// Creates the HTMLAudioElement-compatible Audio constructor.
        /// </summary>
        public static FenFunction CreateAudioConstructor(IExecutionContext context)
        {
            var ctor = new FenFunction("Audio", (args, thisVal) =>
            {
                var src = args.Length > 0 ? args[0].AsString() : string.Empty;
                return FenValue.FromObject(CreateAudio(context, src));
            })
            {
                IsConstructor = true
            };

            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(ctor));
            ctor.Set("prototype", FenValue.FromObject(prototype));
            return ctor;
        }

        /// <summary>
        /// Creates an Audio object with secure source validation and promise-based play()/pause() controls.
        /// </summary>
        public static FenObject CreateAudio(IExecutionContext context, string source = null)
        {
            var execContext = EnsureExecutionContext(context);
            var audio = new FenObject();
            var listeners = new Dictionary<string, List<FenFunction>>(StringComparer.OrdinalIgnoreCase);
            var playbackLock = new object();
            var playbackToken = 0;
            var playbackSlotHeld = false;

            string initialNormalizedSrc;
            string initialValidationError;
            var initialSrc = source ?? string.Empty;
            var hasValidInitialSrc = TryValidateAudioSource(initialSrc, execContext.CurrentUrl, out initialNormalizedSrc, out initialValidationError);

            void Dispatch(string eventType)
            {
                if (string.IsNullOrWhiteSpace(eventType))
                {
                    return;
                }

                var handlerName = "on" + eventType.ToLowerInvariant();
                var inlineHandler = audio.Get(handlerName);
                TryInvokeHandler(inlineHandler, Array.Empty<FenValue>(), execContext, $"audio.{handlerName}");

                List<FenFunction> snapshot = null;
                lock (listeners)
                {
                    if (listeners.TryGetValue(eventType, out var registered) && registered.Count > 0)
                    {
                        snapshot = new List<FenFunction>(registered);
                    }
                }

                if (snapshot == null)
                {
                    return;
                }

                foreach (var listener in snapshot)
                {
                    TryInvokeHandler(FenValue.FromFunction(listener), Array.Empty<FenValue>(), execContext, $"audio.{eventType}");
                }
            }

            int BumpPlaybackToken()
            {
                lock (playbackLock)
                {
                    playbackToken++;
                    return playbackToken;
                }
            }

            bool IsCurrentToken(int token)
            {
                lock (playbackLock)
                {
                    return token == playbackToken;
                }
            }

            void ReleasePlaybackSlotIfHeld()
            {
                lock (playbackLock)
                {
                    if (!playbackSlotHeld)
                    {
                        return;
                    }

                    playbackSlotHeld = false;
                    Interlocked.Decrement(ref _activeAudioPlaybacks);
                }
            }

            bool TryAcquirePlaybackSlot()
            {
                lock (playbackLock)
                {
                    if (playbackSlotHeld)
                    {
                        return true;
                    }

                    if (Volatile.Read(ref _activeAudioPlaybacks) >= MaxConcurrentAudioPlaybacks)
                    {
                        return false;
                    }

                    Interlocked.Increment(ref _activeAudioPlaybacks);
                    playbackSlotHeld = true;
                    return true;
                }
            }

            audio.Set("src", FenValue.FromString(initialSrc));
            audio.Set("currentSrc", FenValue.FromString(hasValidInitialSrc ? initialNormalizedSrc : string.Empty));
            audio.Set("networkState", FenValue.FromNumber(hasValidInitialSrc ? 1 : 0));
            audio.Set("readyState", FenValue.FromNumber(hasValidInitialSrc ? 1 : 0));
            audio.Set("paused", FenValue.FromBoolean(true));
            audio.Set("ended", FenValue.FromBoolean(false));
            audio.Set("duration", FenValue.FromNumber(double.NaN));
            audio.Set("currentTime", FenValue.FromNumber(0));
            audio.Set("playbackRate", FenValue.FromNumber(1));
            audio.Set("defaultPlaybackRate", FenValue.FromNumber(1));
            audio.Set("volume", FenValue.FromNumber(1));
            audio.Set("muted", FenValue.FromBoolean(false));
            audio.Set("autoplay", FenValue.FromBoolean(false));
            audio.Set("loop", FenValue.FromBoolean(false));
            audio.Set("controls", FenValue.FromBoolean(false));
            audio.Set("preload", FenValue.FromString("auto"));
            audio.Set("crossOrigin", FenValue.Null);
            audio.Set("error", FenValue.Null);

            audio.Set("onplay", FenValue.Null);
            audio.Set("onplaying", FenValue.Null);
            audio.Set("onpause", FenValue.Null);
            audio.Set("onended", FenValue.Null);
            audio.Set("onerror", FenValue.Null);
            audio.Set("oncanplay", FenValue.Null);
            audio.Set("onloadeddata", FenValue.Null);
            audio.Set("onloadstart", FenValue.Null);

            audio.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[1].IsFunction)
                {
                    return FenValue.Undefined;
                }

                var evt = args[0].AsString()?.Trim();
                if (string.IsNullOrEmpty(evt))
                {
                    return FenValue.Undefined;
                }

                var callback = args[1].AsFunction();
                lock (listeners)
                {
                    if (!listeners.TryGetValue(evt, out var list))
                    {
                        list = new List<FenFunction>();
                        listeners[evt] = list;
                    }

                    if (!list.Contains(callback))
                    {
                        list.Add(callback);
                    }
                }

                return FenValue.Undefined;
            })));

            audio.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[1].IsFunction)
                {
                    return FenValue.Undefined;
                }

                var evt = args[0].AsString()?.Trim();
                if (string.IsNullOrEmpty(evt))
                {
                    return FenValue.Undefined;
                }

                var callback = args[1].AsFunction();
                lock (listeners)
                {
                    if (listeners.TryGetValue(evt, out var list))
                    {
                        list.RemoveAll(fn => ReferenceEquals(fn, callback));
                        if (list.Count == 0)
                        {
                            listeners.Remove(evt);
                        }
                    }
                }

                return FenValue.Undefined;
            })));

            audio.Set("dispatchEvent", FenValue.FromFunction(new FenFunction("dispatchEvent", (args, thisVal) =>
            {
                var eventType = string.Empty;
                if (args.Length > 0)
                {
                    if (args[0].IsString)
                    {
                        eventType = args[0].AsString();
                    }
                    else if (args[0].IsObject)
                    {
                        eventType = args[0].AsObject()?.Get("type").AsString();
                    }
                }

                if (string.IsNullOrWhiteSpace(eventType))
                {
                    return FenValue.FromBoolean(false);
                }

                Dispatch(eventType.Trim());
                return FenValue.FromBoolean(true);
            })));

            audio.Set("load", FenValue.FromFunction(new FenFunction("load", (args, thisVal) =>
            {
                ReleasePlaybackSlotIfHeld();
                BumpPlaybackToken();

                var src = audio.Get("src").AsString();
                if (string.IsNullOrWhiteSpace(src))
                {
                    audio.Set("currentSrc", FenValue.FromString(string.Empty));
                    audio.Set("readyState", FenValue.FromNumber(0));
                    audio.Set("networkState", FenValue.FromNumber(0));
                    audio.Set("paused", FenValue.FromBoolean(true));
                    audio.Set("ended", FenValue.FromBoolean(false));
                    audio.Set("error", FenValue.Null);
                    return FenValue.Undefined;
                }

                string normalizedSrc;
                string validationError;
                if (!TryValidateAudioSource(src, execContext.CurrentUrl, out normalizedSrc, out validationError))
                {
                    var mediaError = CreateMediaError("NotSupportedError", validationError, 4);
                    audio.Set("currentSrc", FenValue.FromString(string.Empty));
                    audio.Set("readyState", FenValue.FromNumber(0));
                    audio.Set("networkState", FenValue.FromNumber(3));
                    audio.Set("paused", FenValue.FromBoolean(true));
                    audio.Set("ended", FenValue.FromBoolean(false));
                    audio.Set("error", FenValue.FromObject(mediaError));
                    Dispatch("error");
                    return FenValue.Undefined;
                }

                audio.Set("currentSrc", FenValue.FromString(normalizedSrc));
                audio.Set("readyState", FenValue.FromNumber(2));
                audio.Set("networkState", FenValue.FromNumber(1));
                audio.Set("paused", FenValue.FromBoolean(true));
                audio.Set("ended", FenValue.FromBoolean(false));
                audio.Set("error", FenValue.Null);
                Dispatch("loadstart");
                Dispatch("loadeddata");
                Dispatch("canplay");
                return FenValue.Undefined;
            })));

            audio.Set("pause", FenValue.FromFunction(new FenFunction("pause", (args, thisVal) =>
            {
                var wasPaused = audio.Get("paused").ToBoolean();
                BumpPlaybackToken();
                audio.Set("paused", FenValue.FromBoolean(true));
                audio.Set("ended", FenValue.FromBoolean(false));
                ReleasePlaybackSlotIfHeld();

                if (!wasPaused)
                {
                    Dispatch("pause");
                }

                return FenValue.Undefined;
            })));

            audio.Set("play", FenValue.FromFunction(new FenFunction("play", (args, thisVal) =>
            {
                return FenValue.FromObject(new JsPromise(FenValue.FromFunction(new FenFunction("executor", (execArgs, execThis) =>
                {
                    if (execArgs.Length < 2 || !execArgs[0].IsFunction || !execArgs[1].IsFunction)
                    {
                        return FenValue.Undefined;
                    }

                    var resolve = execArgs[0].AsFunction();
                    var reject = execArgs[1].AsFunction();

                    execContext.ScheduleCallback(() =>
                    {
                        try
                        {
                            if (!audio.Get("paused").ToBoolean())
                            {
                                resolve.Invoke(Array.Empty<FenValue>(), execContext);
                                return;
                            }

                            var src = audio.Get("src").AsString();
                            string normalizedSrc;
                            string validationError;
                            if (!TryValidateAudioSource(src, execContext.CurrentUrl, out normalizedSrc, out validationError))
                            {
                                BumpPlaybackToken();
                                var mediaError = CreateMediaError("NotSupportedError", validationError, 4);
                                audio.Set("currentSrc", FenValue.FromString(string.Empty));
                                audio.Set("readyState", FenValue.FromNumber(0));
                                audio.Set("networkState", FenValue.FromNumber(3));
                                audio.Set("paused", FenValue.FromBoolean(true));
                                audio.Set("ended", FenValue.FromBoolean(false));
                                audio.Set("error", FenValue.FromObject(mediaError));
                                Dispatch("error");
                                reject.Invoke(new[] { FenValue.FromObject(mediaError) }, execContext);
                                return;
                            }

                            if (!TryAcquirePlaybackSlot())
                            {
                                var throttleError = CreateMediaError("NotAllowedError", "Audio playback concurrency limit reached.", 2);
                                audio.Set("paused", FenValue.FromBoolean(true));
                                audio.Set("ended", FenValue.FromBoolean(false));
                                audio.Set("error", FenValue.FromObject(throttleError));
                                Dispatch("error");
                                reject.Invoke(new[] { FenValue.FromObject(throttleError) }, execContext);
                                return;
                            }

                            var token = BumpPlaybackToken();
                            audio.Set("currentSrc", FenValue.FromString(normalizedSrc));
                            audio.Set("readyState", FenValue.FromNumber(4));
                            audio.Set("networkState", FenValue.FromNumber(1));
                            audio.Set("paused", FenValue.FromBoolean(false));
                            audio.Set("ended", FenValue.FromBoolean(false));
                            audio.Set("error", FenValue.Null);
                            Dispatch("play");
                            Dispatch("playing");
                            resolve.Invoke(Array.Empty<FenValue>(), execContext);

                            var playbackRate = audio.Get("playbackRate").ToNumber();
                            if (double.IsNaN(playbackRate) || double.IsInfinity(playbackRate) || playbackRate <= 0)
                            {
                                playbackRate = 1;
                            }

                            var endDelayMs = Math.Max(80, (int)Math.Round(250 / playbackRate));
                            execContext.ScheduleCallback(() =>
                            {
                                if (!IsCurrentToken(token))
                                {
                                    return;
                                }

                                audio.Set("paused", FenValue.FromBoolean(true));
                                audio.Set("ended", FenValue.FromBoolean(true));
                                Dispatch("ended");
                                ReleasePlaybackSlotIfHeld();
                            }, endDelayMs);
                        }
                        catch (Exception ex)
                        {
                            var runtimeError = CreateMediaError("AbortError", ex.Message, 3);
                            audio.Set("paused", FenValue.FromBoolean(true));
                            audio.Set("ended", FenValue.FromBoolean(false));
                            audio.Set("error", FenValue.FromObject(runtimeError));
                            Dispatch("error");
                            ReleasePlaybackSlotIfHeld();
                            reject.Invoke(new[] { FenValue.FromObject(runtimeError) }, execContext);
                        }
                    }, 0);

                    return FenValue.Undefined;
                })), execContext));
            })));

            audio.Set("canPlayType", FenValue.FromFunction(new FenFunction("canPlayType", (args, thisVal) =>
            {
                var requested = args.Length > 0 ? args[0].AsString() : string.Empty;
                if (string.IsNullOrWhiteSpace(requested))
                {
                    return FenValue.FromString(string.Empty);
                }

                var normalizedType = requested.Trim().ToLowerInvariant();
                if (normalizedType.Length > 256)
                {
                    normalizedType = normalizedType.Substring(0, 256);
                }

                var parameterIndex = normalizedType.IndexOf(';');
                if (parameterIndex >= 0)
                {
                    normalizedType = normalizedType.Substring(0, parameterIndex).Trim();
                }

                if (ProbablyPlayableAudioTypes.Contains(normalizedType))
                {
                    return FenValue.FromString("probably");
                }

                if (MaybePlayableAudioTypes.Contains(normalizedType))
                {
                    return FenValue.FromString("maybe");
                }

                return FenValue.FromString(string.Empty);
            })));

            if (!string.IsNullOrWhiteSpace(initialValidationError))
            {
                FenLogger.Warn($"[WebAudio] Audio source rejected during constructor initialization: {initialValidationError}", LogCategory.JavaScript);
            }

            return audio;
        }

        private static IExecutionContext EnsureExecutionContext(IExecutionContext context)
        {
            return context ?? new FenBrowser.FenEngine.Core.ExecutionContext();
        }

        private static void TryInvokeHandler(FenValue callback, FenValue[] args, IExecutionContext context, string operation)
        {
            if (!callback.IsFunction)
            {
                return;
            }

            try
            {
                callback.AsFunction().Invoke(args ?? Array.Empty<FenValue>(), context);
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[WebAudio] {operation} callback failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private static FenObject CreateMediaError(string name, string message, int code)
        {
            var error = new FenObject();
            error.Set("name", FenValue.FromString(name ?? "MediaError"));
            error.Set("message", FenValue.FromString(message ?? "Unknown audio error"));
            error.Set("code", FenValue.FromNumber(code));
            return error;
        }

        private static bool TryValidateAudioSource(string source, string currentUrl, out string normalizedSource, out string error)
        {
            normalizedSource = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Audio source is empty.";
                return false;
            }

            source = source.Trim();
            if (source.Length > MaxAudioSourceLength)
            {
                error = "Audio source exceeds maximum allowed length.";
                return false;
            }

            if (source.IndexOf('\r') >= 0 || source.IndexOf('\n') >= 0 || source.IndexOf('\0') >= 0)
            {
                error = "Audio source contains invalid control characters.";
                return false;
            }

            if (Uri.TryCreate(source, UriKind.Absolute, out var absolute))
            {
                var scheme = absolute.Scheme?.ToLowerInvariant();
                if (scheme == "http" || scheme == "https")
                {
                    if (IsPrivateOrReservedHost(absolute.Host))
                    {
                        error = "Audio source host is private or reserved.";
                        return false;
                    }

                    normalizedSource = absolute.ToString();
                    return true;
                }

                if (scheme == "data")
                {
                    if (!source.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "Only audio data URIs are allowed.";
                        return false;
                    }

                    if (source.Length > MaxAudioDataUriLength)
                    {
                        error = "Audio data URI exceeds maximum allowed length.";
                        return false;
                    }

                    normalizedSource = source;
                    return true;
                }

                if (scheme == "blob")
                {
                    normalizedSource = source;
                    return true;
                }

                error = $"Audio source scheme '{scheme}' is not allowed.";
                return false;
            }

            if (source.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                error = "Audio source scheme is not allowed.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(currentUrl) &&
                Uri.TryCreate(currentUrl, UriKind.Absolute, out var baseUri) &&
                Uri.TryCreate(baseUri, source, out var resolved))
            {
                if ((resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps) && !IsPrivateOrReservedHost(resolved.Host))
                {
                    normalizedSource = resolved.ToString();
                    return true;
                }

                error = "Resolved audio source is not allowed.";
                return false;
            }

            normalizedSource = source;
            return true;
        }

        private static bool IsPrivateOrReservedHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return true;
            }

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("ip6-localhost", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("ip6-loopback", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!IPAddress.TryParse(host, out var ip))
            {
                return false;
            }

            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return bytes[0] == 10 ||
                       bytes[0] == 127 ||
                       bytes[0] == 0 ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 169 && bytes[1] == 254) ||
                       (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return ip.Equals(IPAddress.IPv6Loopback) ||
                       (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
                       ((bytes[0] & 0xfe) == 0xfc);
            }

            return false;
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
                return FenValue.FromObject(CreateOscillatorNode(ctx, context));
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
                return FenValue.FromObject(CreateBufferSourceNode(ctx, context));
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
                    _ = RunDetachedAsync(async () => 
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

        private static FenObject CreateOscillatorNode(FenObject context, IExecutionContext executionContext)
        {
            var execContext = EnsureExecutionContext(executionContext);
            var node = CreateBaseAudioNode(context, "oscillator");
            var playbackToken = 0;
            var started = false;
            var ended = false;

            void DispatchEnded()
            {
                var onended = node.Get("onended");
                if (onended.IsFunction)
                {
                    TryInvokeHandler(onended, Array.Empty<FenValue>(), execContext, "oscillator.onended");
                }
            }

            node.Set("type", FenValue.FromString("sine")); // sine, square, sawtooth, triangle
            node.Set("frequency", FenValue.FromObject(CreateAudioParam(440)));
            node.Set("detune", FenValue.FromObject(CreateAudioParam(0)));
            node.Set("onended", FenValue.Null);
            node.Set("playbackState", FenValue.FromString("idle"));
            
            node.Set("start", FenValue.FromFunction(new FenFunction("start", (args, thisVal) =>
            {
                if (started && !ended)
                {
                    return FenValue.Undefined;
                }

                playbackToken++;
                started = true;
                ended = false;
                node.Set("playbackState", FenValue.FromString("playing"));
                FenLogger.Debug("[WebAudio] OscillatorNode.start()", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));
            
            node.Set("stop", FenValue.FromFunction(new FenFunction("stop", (args, thisVal) =>
            {
                if (!started || ended)
                {
                    return FenValue.Undefined;
                }

                var localToken = ++playbackToken;
                var stopDelayMs = args.Length > 0 ? Math.Max(0, (int)Math.Round(args[0].ToNumber() * 1000.0)) : 0;
                execContext.ScheduleCallback(() =>
                {
                    if (localToken != playbackToken || ended)
                    {
                        return;
                    }

                    ended = true;
                    node.Set("playbackState", FenValue.FromString("finished"));
                    DispatchEnded();
                }, stopDelayMs);
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
                if (args.Length > 0 && args[0].IsObject)
                {
                    PopulateAnalyserByteData(args[0].AsObject(), isFrequencyDomain: true);
                }
                return FenValue.Undefined;
            })));
            
            node.Set("getByteTimeDomainData", FenValue.FromFunction(new FenFunction("getByteTimeDomainData", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsObject)
                {
                    PopulateAnalyserByteData(args[0].AsObject(), isFrequencyDomain: false);
                }
                return FenValue.Undefined;
            })));

            node.Set("getFloatFrequencyData", FenValue.FromFunction(new FenFunction("getFloatFrequencyData", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsObject)
                {
                    PopulateAnalyserFloatData(args[0].AsObject(), isFrequencyDomain: true);
                }
                return FenValue.Undefined;
            })));

            node.Set("getFloatTimeDomainData", FenValue.FromFunction(new FenFunction("getFloatTimeDomainData", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsObject)
                {
                    PopulateAnalyserFloatData(args[0].AsObject(), isFrequencyDomain: false);
                }
                return FenValue.Undefined;
            })));
            
            return node;
        }

        private static FenObject CreateBufferSourceNode(FenObject context, IExecutionContext executionContext)
        {
            var execContext = EnsureExecutionContext(executionContext);
            var node = CreateBaseAudioNode(context, "bufferSource");
            var playbackToken = 0;
            var started = false;
            var ended = false;

            void DispatchEnded()
            {
                var onended = node.Get("onended");
                if (onended.IsFunction)
                {
                    TryInvokeHandler(onended, Array.Empty<FenValue>(), execContext, "bufferSource.onended");
                }
            }

            node.Set("buffer", FenValue.Null);
            node.Set("loop", FenValue.FromBoolean(false));
            node.Set("loopStart", FenValue.FromNumber(0));
            node.Set("loopEnd", FenValue.FromNumber(0));
            node.Set("playbackRate", FenValue.FromObject(CreateAudioParam(1)));
            node.Set("onended", FenValue.Null);
            node.Set("playbackState", FenValue.FromString("idle"));
            
            node.Set("start", FenValue.FromFunction(new FenFunction("start", (args, thisVal) =>
            {
                if (started && !ended)
                {
                    return FenValue.Undefined;
                }

                playbackToken++;
                started = true;
                ended = false;
                node.Set("playbackState", FenValue.FromString("playing"));

                var localToken = playbackToken;
                var offsetSeconds = args.Length > 1 ? Math.Max(0, args[1].ToNumber()) : 0;
                var maybeBuffer = node.Get("buffer");
                var isLooping = node.Get("loop").ToBoolean();
                if (!isLooping && maybeBuffer.IsObject)
                {
                    var bufferObject = maybeBuffer.AsObject();
                    var durationValue = bufferObject.Get("duration");
                    var durationSeconds = durationValue.IsNumber ? durationValue.ToNumber() : 0;
                    var playbackRateObject = node.Get("playbackRate").AsObject();
                    var playbackRateValue = playbackRateObject?.Get("value").ToNumber() ?? 1;
                    if (playbackRateValue <= 0 || double.IsNaN(playbackRateValue) || double.IsInfinity(playbackRateValue))
                    {
                        playbackRateValue = 1;
                    }

                    var remainingSeconds = Math.Max(0, durationSeconds - offsetSeconds);
                    var endDelayMs = (int)Math.Round((remainingSeconds / playbackRateValue) * 1000.0);
                    execContext.ScheduleCallback(() =>
                    {
                        if (localToken != playbackToken || ended)
                        {
                            return;
                        }

                        ended = true;
                        node.Set("playbackState", FenValue.FromString("finished"));
                        DispatchEnded();
                    }, Math.Max(0, endDelayMs));
                }

                return FenValue.Undefined;
            })));
            
            node.Set("stop", FenValue.FromFunction(new FenFunction("stop", (args, thisVal) =>
            {
                if (!started || ended)
                {
                    return FenValue.Undefined;
                }

                var localToken = ++playbackToken;
                var stopDelayMs = args.Length > 0 ? Math.Max(0, (int)Math.Round(args[0].ToNumber() * 1000.0)) : 0;
                execContext.ScheduleCallback(() =>
                {
                    if (localToken != playbackToken || ended)
                    {
                        return;
                    }

                    ended = true;
                    node.Set("playbackState", FenValue.FromString("finished"));
                    DispatchEnded();
                }, stopDelayMs);
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
            var connectedTargets = new List<IObject>();
            node.Set("context", FenValue.FromObject(context));
            node.Set("numberOfInputs", FenValue.FromNumber(1));
            node.Set("numberOfOutputs", FenValue.FromNumber(1));
            node.Set("channelCount", FenValue.FromNumber(2));
            node.Set("channelCountMode", FenValue.FromString("max"));
            node.Set("channelInterpretation", FenValue.FromString("speakers"));
            node.Set("_nodeType", FenValue.FromString(nodeType));
            node.Set("connectionCount", FenValue.FromNumber(0));

            node.Set("connect", FenValue.FromFunction(new FenFunction("connect", (args, thisVal) =>
            {
                FenLogger.Debug($"[WebAudio] {nodeType}.connect()", LogCategory.JavaScript);
                if (args.Length > 0 && args[0].IsObject)
                {
                    var target = args[0].AsObject();
                    if (target != null && !connectedTargets.Contains(target))
                    {
                        connectedTargets.Add(target);
                        node.Set("connectionCount", FenValue.FromNumber(connectedTargets.Count));
                    }
                    return args[0];
                }
                return FenValue.Undefined;
            })));

            node.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (args, thisVal) =>
            {
                FenLogger.Debug($"[WebAudio] {nodeType}.disconnect()", LogCategory.JavaScript);
                if (args.Length > 0 && args[0].IsObject)
                {
                    var target = args[0].AsObject();
                    connectedTargets.RemoveAll(item => ReferenceEquals(item, target));
                }
                else
                {
                    connectedTargets.Clear();
                }
                node.Set("connectionCount", FenValue.FromNumber(connectedTargets.Count));
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
            var channelData = new float[Math.Max(1, channels)][];
            for (var c = 0; c < channelData.Length; c++)
            {
                channelData[c] = new float[Math.Max(0, length)];
            }

            var buffer = new FenObject();
            buffer.Set("sampleRate", FenValue.FromNumber(sampleRate));
            buffer.Set("length", FenValue.FromNumber(length));
            buffer.Set("duration", FenValue.FromNumber(length / sampleRate));
            buffer.Set("numberOfChannels", FenValue.FromNumber(channels));

            buffer.Set("getChannelData", FenValue.FromFunction(new FenFunction("getChannelData", (args, thisVal) =>
            {
                var channel = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                channel = Math.Max(0, Math.Min(channel, channelData.Length - 1));
                return FenValue.FromObject(CreateFloatArraySnapshot(channelData[channel]));
            })));

            buffer.Set("copyFromChannel", FenValue.FromFunction(new FenFunction("copyFromChannel", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject)
                {
                    return FenValue.Undefined;
                }

                var destination = args[0].AsObject();
                var channelNumber = Math.Max(0, Math.Min((int)args[1].ToNumber(), channelData.Length - 1));
                var startInChannel = args.Length > 2 ? Math.Max(0, (int)args[2].ToNumber()) : 0;
                var source = channelData[channelNumber];
                var copyLength = Math.Max(0, Math.Min(GetArrayLikeLength(destination), source.Length - startInChannel));
                for (var i = 0; i < copyLength; i++)
                {
                    destination.Set(i.ToString(), FenValue.FromNumber(source[startInChannel + i]));
                }
                return FenValue.Undefined;
            })));

            buffer.Set("copyToChannel", FenValue.FromFunction(new FenFunction("copyToChannel", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsObject)
                {
                    return FenValue.Undefined;
                }

                var source = args[0].AsObject();
                var channelNumber = Math.Max(0, Math.Min((int)args[1].ToNumber(), channelData.Length - 1));
                var startInChannel = args.Length > 2 ? Math.Max(0, (int)args[2].ToNumber()) : 0;
                var destination = channelData[channelNumber];
                var copyLength = Math.Max(0, Math.Min(GetArrayLikeLength(source), destination.Length - startInChannel));
                for (var i = 0; i < copyLength; i++)
                {
                    destination[startInChannel + i] = (float)ReadArrayLikeNumber(source, i);
                }
                return FenValue.Undefined;
            })));

            return buffer;
        }

        private static void PopulateAnalyserByteData(IObject target, bool isFrequencyDomain)
        {
            var length = GetArrayLikeLength(target);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            for (var i = 0; i < length; i++)
            {
                var phase = now * 4 + (i / (double)Math.Max(1, length)) * Math.PI * 2;
                var sample = isFrequencyDomain
                    ? Math.Max(0, 255 - (i * 255 / Math.Max(1, length)))
                    : 128 + (int)Math.Round(Math.Sin(phase) * 127);
                target.Set(i.ToString(), FenValue.FromNumber(Math.Max(0, Math.Min(255, sample))));
            }
        }

        private static void PopulateAnalyserFloatData(IObject target, bool isFrequencyDomain)
        {
            var length = GetArrayLikeLength(target);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            for (var i = 0; i < length; i++)
            {
                var phase = now * 4 + (i / (double)Math.Max(1, length)) * Math.PI * 2;
                var sample = isFrequencyDomain
                    ? -100.0 + (70.0 * (1.0 - (i / (double)Math.Max(1, length))))
                    : Math.Sin(phase);
                target.Set(i.ToString(), FenValue.FromNumber(sample));
            }
        }

        private static int GetArrayLikeLength(IObject arrayLike)
        {
            if (arrayLike == null)
            {
                return 0;
            }

            var lengthValue = arrayLike.Get("length");
            if (!lengthValue.IsNumber)
            {
                return 0;
            }

            return Math.Max(0, (int)lengthValue.ToNumber());
        }

        private static double ReadArrayLikeNumber(IObject arrayLike, int index)
        {
            if (arrayLike == null)
            {
                return 0;
            }

            var value = arrayLike.Get(index.ToString());
            return value.IsNumber ? value.ToNumber() : 0;
        }

        private static FenObject CreateFloatArraySnapshot(float[] source)
        {
            var arr = new FenObject();
            var length = source?.Length ?? 0;
            arr.Set("length", FenValue.FromNumber(length));
            for (var i = 0; i < length; i++)
            {
                arr.Set(i.ToString(), FenValue.FromNumber(source[i]));
            }
            return arr;
        }
    }
}



