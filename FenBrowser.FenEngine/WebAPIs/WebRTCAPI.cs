using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// WebRTC API implementation
    /// Provides peer-to-peer communication capabilities
    /// </summary>
    public static class WebRTCAPI
    {
        private const int MaxIceServers = 8;
        private const int MaxIceUrlsPerServer = 8;
        private const int MaxIceUrlLength = 512;

        private static readonly HashSet<string> AllowedIceSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "stun",
            "turn",
            "turns"
        };

        private static int _connectionIdCounter = 0;
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
                    FenLogger.Warn($"[WebRTC] Detached async operation failed: {ex.Message}", LogCategory.JavaScript);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }
        /// <summary>
        /// Creates the RTCPeerConnection constructor
        /// </summary>
        public static FenObject CreateRTCPeerConnectionConstructor(IExecutionContext context)
        {
            var execContext = EnsureExecutionContext(context);
            var constructor = new FenFunction("RTCPeerConnection", (args, thisVal) =>
            {
                var config = args.Length > 0 && args[0].IsObject ? args[0].AsObject() : null;
                if (!TryNormalizeRtcConfiguration(config, out var normalizedConfig, out var validationError))
                {
                    throw new InvalidOperationException(validationError ?? "Invalid RTCPeerConnection configuration.");
                }

                return FenValue.FromObject(CreateRTCPeerConnection(normalizedConfig, execContext));
            })
            {
                IsConstructor = true,
                NativeLength = 1
            };

            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(constructor));
            constructor.Set("prototype", FenValue.FromObject(prototype));
            return constructor;
        }
        /// <summary>
        /// Creates an RTCPeerConnection instance
        /// </summary>
        public static FenObject CreateRTCPeerConnection(IObject configuration, IExecutionContext context)
        {
            var execContext = EnsureExecutionContext(context);
            if (!TryNormalizeRtcConfiguration(configuration, out var normalizedConfig, out var validationError))
            {
                throw new InvalidOperationException(validationError ?? "Invalid RTCPeerConnection configuration.");
            }

            var connectionId = Interlocked.Increment(ref _connectionIdCounter);
            var pc = new FenObject();
            var listeners = new Dictionary<string, List<FenFunction>>(StringComparer.OrdinalIgnoreCase);
            var dataChannels = new List<FenObject>();
            var senders = new List<FenObject>();
            var receivers = new List<FenObject>();
            var transceivers = new List<FenObject>();
            var stateLock = new object();
            var nextDataChannelId = 0;
            var isClosed = false;

            FenObject CreateEvent(string eventType)
            {
                var evt = new FenObject();
                evt.Set("type", FenValue.FromString(eventType));
                evt.Set("target", FenValue.FromObject(pc));
                evt.Set("currentTarget", FenValue.FromObject(pc));
                return evt;
            }

            void Dispatch(string eventType)
            {
                if (string.IsNullOrWhiteSpace(eventType))
                {
                    return;
                }

                var normalizedEventType = eventType.Trim().ToLowerInvariant();
                var evt = CreateEvent(normalizedEventType);
                var eventArgs = new[] { FenValue.FromObject(evt) };
                var inlineHandler = pc.Get("on" + normalizedEventType);
                TryInvokeHandler(inlineHandler, eventArgs, execContext, $"RTCPeerConnection.{normalizedEventType}.inline");

                List<FenFunction> snapshot = null;
                lock (listeners)
                {
                    if (listeners.TryGetValue(normalizedEventType, out var list) && list.Count > 0)
                    {
                        snapshot = new List<FenFunction>(list);
                    }
                }

                if (snapshot == null)
                {
                    return;
                }

                foreach (var listener in snapshot)
                {
                    TryInvokeHandler(FenValue.FromFunction(listener), eventArgs, execContext, $"RTCPeerConnection.{normalizedEventType}.listener");
                }
            }

            FenObject SnapshotArray(List<FenObject> items)
            {
                var arr = FenObject.CreateArray();
                lock (stateLock)
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        arr.Set(i.ToString(), FenValue.FromObject(items[i]));
                    }

                    arr.Set("length", FenValue.FromNumber(items.Count));
                }

                return arr;
            }

            void SetPcState(string propertyName, string nextValue, string eventType)
            {
                var currentValue = pc.Get(propertyName).AsString();
                if (string.Equals(currentValue, nextValue, StringComparison.Ordinal))
                {
                    return;
                }

                pc.Set(propertyName, FenValue.FromString(nextValue));
                if (!string.IsNullOrWhiteSpace(eventType))
                {
                    Dispatch(eventType);
                }
            }
            var connectionState = "new";
            var iceConnectionState = "new";
            var iceGatheringState = "new";
            var signalingState = "stable";

            FenLogger.Debug($"[WebRTC] RTCPeerConnection #{connectionId} created", LogCategory.JavaScript);

            // Properties
            pc.Set("connectionState", FenValue.FromString(connectionState));
            pc.Set("iceConnectionState", FenValue.FromString(iceConnectionState));
            pc.Set("iceGatheringState", FenValue.FromString(iceGatheringState));
            pc.Set("signalingState", FenValue.FromString(signalingState));
            pc.Set("localDescription", FenValue.Null);
            pc.Set("remoteDescription", FenValue.Null);
            pc.Set("currentLocalDescription", FenValue.Null);
            pc.Set("currentRemoteDescription", FenValue.Null);
            pc.Set("pendingLocalDescription", FenValue.Null);
            pc.Set("pendingRemoteDescription", FenValue.Null);
            pc.Set("canTrickleIceCandidates", FenValue.FromBoolean(true));
            pc.Set("__configuration", FenValue.FromObject(normalizedConfig));
            pc.Set("getConfiguration", FenValue.FromFunction(new FenFunction("getConfiguration", (args, thisVal) =>
            {
                return FenValue.FromObject(normalizedConfig);
            })));
            pc.Set("setConfiguration", FenValue.FromFunction(new FenFunction("setConfiguration", (args, thisVal) =>
            {
                var nextConfig = args.Length > 0 && args[0].IsObject ? args[0].AsObject() : null;
                if (!TryNormalizeRtcConfiguration(nextConfig, out var normalizedNextConfig, out var setConfigError))
                {
                    throw new InvalidOperationException(setConfigError ?? "Invalid RTCPeerConnection configuration.");
                }

                normalizedConfig = normalizedNextConfig;
                pc.Set("__configuration", FenValue.FromObject(normalizedConfig));
                return FenValue.Undefined;
            })));

            // Event handlers (null by default)
            pc.Set("onicecandidate", FenValue.Null);
            pc.Set("oniceconnectionstatechange", FenValue.Null);
            pc.Set("onicegatheringstatechange", FenValue.Null);
            pc.Set("onconnectionstatechange", FenValue.Null);
            pc.Set("onsignalingstatechange", FenValue.Null);
            pc.Set("ondatachannel", FenValue.Null);
            pc.Set("ontrack", FenValue.Null);
            pc.Set("onnegotiationneeded", FenValue.Null);
            pc.Set("onclose", FenValue.Null);

            pc.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[1].IsFunction)
                {
                    return FenValue.Undefined;
                }

                var eventType = args[0].AsString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(eventType))
                {
                    return FenValue.Undefined;
                }

                var callback = args[1].AsFunction();
                lock (listeners)
                {
                    if (!listeners.TryGetValue(eventType, out var list))
                    {
                        list = new List<FenFunction>();
                        listeners[eventType] = list;
                    }

                    if (!list.Contains(callback))
                    {
                        list.Add(callback);
                    }
                }

                return FenValue.Undefined;
            })));

            pc.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[1].IsFunction)
                {
                    return FenValue.Undefined;
                }

                var eventType = args[0].AsString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(eventType))
                {
                    return FenValue.Undefined;
                }

                var callback = args[1].AsFunction();
                lock (listeners)
                {
                    if (listeners.TryGetValue(eventType, out var list))
                    {
                        list.RemoveAll(item => ReferenceEquals(item, callback));
                        if (list.Count == 0)
                        {
                            listeners.Remove(eventType);
                        }
                    }
                }

                return FenValue.Undefined;
            })));

            pc.Set("dispatchEvent", FenValue.FromFunction(new FenFunction("dispatchEvent", (args, thisVal) =>
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
                        eventType = (args[0].AsObject()?.Get("type") ?? FenValue.Undefined).AsString();
                    }
                }

                if (string.IsNullOrWhiteSpace(eventType))
                {
                    return FenValue.FromBoolean(false);
                }

                Dispatch(eventType);
                return FenValue.FromBoolean(true);
            })));

            // createOffer()
            pc.Set("createOffer", FenValue.FromFunction(new FenFunction("createOffer", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] createOffer()", LogCategory.JavaScript);
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => 
                {
                    var offer = new FenObject();
                    offer.Set("type", FenValue.FromString("offer"));
                    offer.Set("sdp", FenValue.FromString($"v=0\r\no=- {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"));
                    eArgs[0].AsFunction().Invoke(new[] { FenValue.FromObject(offer) }, execContext);
                    return FenValue.Undefined;
                })), execContext));
            })));

            // createAnswer()
            pc.Set("createAnswer", FenValue.FromFunction(new FenFunction("createAnswer", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] createAnswer()", LogCategory.JavaScript);
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => 
                {
                    var answer = new FenObject();
                    answer.Set("type", FenValue.FromString("answer"));
                    answer.Set("sdp", FenValue.FromString($"v=0\r\no=- {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"));
                    eArgs[0].AsFunction().Invoke(new[] { FenValue.FromObject(answer) }, execContext);
                    return FenValue.Undefined;
                })), execContext));
            })));

            // setLocalDescription()
            pc.Set("setLocalDescription", FenValue.FromFunction(new FenFunction("setLocalDescription", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] setLocalDescription()", LogCategory.JavaScript);
                if (args.Length > 0 && args[0].IsObject)
                {
                    pc.Set("localDescription", args[0]);
                    pc.Set("currentLocalDescription", args[0]);
                    pc.Set("signalingState", FenValue.FromString("have-local-offer"));
                }
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => 
                {
                    eArgs[0].AsFunction().Invoke(new FenValue[0], execContext);
                    return FenValue.Undefined;
                })), execContext));
            })));

            // setRemoteDescription()
            pc.Set("setRemoteDescription", FenValue.FromFunction(new FenFunction("setRemoteDescription", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] setRemoteDescription()", LogCategory.JavaScript);
                if (args.Length > 0 && args[0].IsObject)
                {
                    pc.Set("remoteDescription", args[0]);
                    pc.Set("currentRemoteDescription", args[0]);
                    pc.Set("signalingState", FenValue.FromString("stable"));
                }
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => 
                {
                    eArgs[0].AsFunction().Invoke(new FenValue[0], execContext);
                    return FenValue.Undefined;
                })), execContext));
            })));

            // addIceCandidate()
            pc.Set("addIceCandidate", FenValue.FromFunction(new FenFunction("addIceCandidate", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] addIceCandidate()", LogCategory.JavaScript);
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => 
                {
                    eArgs[0].AsFunction().Invoke(new FenValue[0], execContext);
                    return FenValue.Undefined;
                })), execContext));
            })));

            // createDataChannel()
            pc.Set("createDataChannel", FenValue.FromFunction(new FenFunction("createDataChannel", (args, thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "";
                FenLogger.Debug($"[WebRTC] createDataChannel({label})", LogCategory.JavaScript);
                if (isClosed)
                {
                    return FenValue.FromError("InvalidStateError: RTCPeerConnection is closed.");
                }

                var options = args.Length > 1 && args[1].IsObject ? args[1].AsObject() : null;
                var channel = CreateRTCDataChannel(label, nextDataChannelId++, options, execContext);
                lock (stateLock)
                {
                    dataChannels.Add(channel);
                }

                SetPcState("connectionState", "connecting", "connectionstatechange");
                SetPcState("iceConnectionState", "checking", "iceconnectionstatechange");
                Dispatch("negotiationneeded");
                return FenValue.FromObject(channel);
            })));

            // addTrack()
            pc.Set("addTrack", FenValue.FromFunction(new FenFunction("addTrack", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] addTrack()", LogCategory.JavaScript);
                if (isClosed)
                {
                    return FenValue.FromError("InvalidStateError: RTCPeerConnection is closed.");
                }

                var sender = CreateRTCRtpSender(execContext);
                var receiver = CreateRTCRtpReceiver(execContext);
                var track = args.Length > 0 ? args[0] : FenValue.Null;
                sender.Set("track", track);
                receiver.Set("track", track);
                var transceiver = CreateRTCRtpTransceiver(sender, receiver);

                lock (stateLock)
                {
                    senders.Add(sender);
                    receivers.Add(receiver);
                    transceivers.Add(transceiver);
                }

                SetPcState("connectionState", "connecting", "connectionstatechange");
                SetPcState("iceConnectionState", "checking", "iceconnectionstatechange");
                Dispatch("negotiationneeded");
                return FenValue.FromObject(sender);
            })));

            // removeTrack()
            pc.Set("removeTrack", FenValue.FromFunction(new FenFunction("removeTrack", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] removeTrack()", LogCategory.JavaScript);
                if (args.Length > 0 && args[0].IsObject)
                {
                    var sender = args[0].AsObject() as FenObject;
                    if (sender != null)
                    {
                        lock (stateLock)
                        {
                            var matchedReceivers = new List<FenObject>();
                            foreach (var transceiver in transceivers)
                            {
                                if (ReferenceEquals(transceiver.Get("sender").AsObject(), sender))
                                {
                                    var receiver = transceiver.Get("receiver").AsObject() as FenObject;
                                    if (receiver != null)
                                    {
                                        matchedReceivers.Add(receiver);
                                    }
                                }
                            }
                            senders.RemoveAll(item => ReferenceEquals(item, sender));
                            receivers.RemoveAll(item => matchedReceivers.Exists(receiver => ReferenceEquals(receiver, item)));
                            transceivers.RemoveAll(item => ReferenceEquals(item.Get("sender").AsObject(), sender));
                        }
                        Dispatch("negotiationneeded");
                    }
                }

                return FenValue.Undefined;
            })));

            // getSenders()
            pc.Set("getSenders", FenValue.FromFunction(new FenFunction("getSenders", (args, thisVal) =>
            {
                return FenValue.FromObject(SnapshotArray(senders));
            })));

            // getReceivers()
            pc.Set("getReceivers", FenValue.FromFunction(new FenFunction("getReceivers", (args, thisVal) =>
            {
                return FenValue.FromObject(SnapshotArray(receivers));
            })));

            // getTransceivers()
            pc.Set("getTransceivers", FenValue.FromFunction(new FenFunction("getTransceivers", (args, thisVal) =>
            {
                return FenValue.FromObject(SnapshotArray(transceivers));
            })));

            // getStats()
            pc.Set("getStats", FenValue.FromFunction(new FenFunction("getStats", (args, thisVal) =>
            {
                FenObject stats;
                lock (stateLock)
                {
                    stats = CreateStatsReport(
                        connectionId,
                        pc.Get("connectionState").AsString(),
                        pc.Get("iceConnectionState").AsString(),
                        senders.Count,
                        receivers.Count,
                        dataChannels.Count);
                }

                return FenValue.FromObject(CreateResolvedPromise(FenValue.FromObject(stats), execContext));
            })));

            // close()
            pc.Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                if (isClosed)
                {
                    return FenValue.Undefined;
                }

                isClosed = true;
                FenLogger.Debug($"[WebRTC] RTCPeerConnection #{connectionId} closed", LogCategory.JavaScript);
                lock (stateLock)
                {
                    foreach (var dataChannel in dataChannels)
                    {
                        dataChannel.Set("readyState", FenValue.FromString("closed"));
                        var onclose = dataChannel.Get("onclose");
                        TryInvokeHandler(onclose, Array.Empty<FenValue>(), execContext, "RTCDataChannel.close.inline");
                    }
                }

                SetPcState("connectionState", "closed", "connectionstatechange");
                SetPcState("iceConnectionState", "closed", "iceconnectionstatechange");
                SetPcState("iceGatheringState", "complete", "icegatheringstatechange");
                SetPcState("signalingState", "closed", "signalingstatechange");
                Dispatch("close");
                return FenValue.Undefined;
            })));

            return pc;
        }

        private static FenObject CreateRTCDataChannel(string label, int id, IObject options, IExecutionContext context)
        {
            var execContext = EnsureExecutionContext(context);
            var dc = new FenObject();
            var ordered = options?.Get("ordered") ?? FenValue.Null;
            var maxPacketLifeTime = options?.Get("maxPacketLifeTime") ?? FenValue.Null;
            var maxRetransmits = options?.Get("maxRetransmits") ?? FenValue.Null;
            var protocol = (options?.Get("protocol") ?? FenValue.Undefined).AsString() ?? string.Empty;
            var negotiated = options?.Get("negotiated") ?? FenValue.Null;
            var binaryType = NormalizeBinaryType((options?.Get("binaryType") ?? FenValue.Undefined).AsString());
            double bufferedAmount = 0;
            dc.Set("label", FenValue.FromString(label));
            dc.Set("ordered", FenValue.FromBoolean(!ordered.IsBoolean || ordered.ToBoolean()));
            dc.Set("maxPacketLifeTime", maxPacketLifeTime);
            dc.Set("maxRetransmits", maxRetransmits);
            dc.Set("protocol", FenValue.FromString(protocol));
            dc.Set("negotiated", FenValue.FromBoolean(negotiated.IsBoolean && negotiated.ToBoolean()));
            dc.Set("id", FenValue.FromNumber(id));
            dc.Set("readyState", FenValue.FromString("connecting"));
            dc.Set("bufferedAmount", FenValue.FromNumber(0));
            dc.Set("bufferedAmountLowThreshold", FenValue.FromNumber(0));
            dc.Set("binaryType", FenValue.FromString(binaryType));

            // Event handlers
            dc.Set("onopen", FenValue.Null);
            dc.Set("onclose", FenValue.Null);
            dc.Set("onerror", FenValue.Null);
            dc.Set("onmessage", FenValue.Null);
            dc.Set("onbufferedamountlow", FenValue.Null);

            dc.Set("send", FenValue.FromFunction(new FenFunction("send", (args, thisVal) =>
            {
                if (!string.Equals(dc.Get("readyState").AsString(), "open", StringComparison.Ordinal))
                {
                    return FenValue.FromError("InvalidStateError: RTCDataChannel is not open.");
                }

                var payload = args.Length > 0 ? args[0].ToString() : string.Empty;
                bufferedAmount += Encoding.UTF8.GetByteCount(payload);
                dc.Set("bufferedAmount", FenValue.FromNumber(bufferedAmount));
                FenLogger.Debug("[WebRTC] DataChannel.send()", LogCategory.JavaScript);

                _ = RunDetachedAsync(async () =>
                {
                    await Task.Delay(10).ConfigureAwait(false);
                    bufferedAmount = 0;
                    dc.Set("bufferedAmount", FenValue.FromNumber(0));
                    execContext.ScheduleCallback(() =>
                    {
                        var onbufferedamountlow = dc.Get("onbufferedamountlow");
                        if (onbufferedamountlow.IsFunction)
                        {
                            onbufferedamountlow.AsFunction().Invoke(Array.Empty<FenValue>(), execContext);
                        }
                    }, 0);
                });

                return FenValue.Undefined;
            })));

            dc.Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                dc.Set("readyState", FenValue.FromString("closed"));
                var onclose = dc.Get("onclose");
                TryInvokeHandler(onclose, Array.Empty<FenValue>(), execContext, "RTCDataChannel.close.inline");
                return FenValue.Undefined;
            })));

            // Simulate channel opening
            _ = RunDetachedAsync(async () =>
            {
                await Task.Delay(100);
                dc.Set("readyState", FenValue.FromString("open"));
                execContext.ScheduleCallback(() => {
                    var onopen = dc.Get("onopen");
                    if (onopen.IsFunction) onopen.AsFunction().Invoke(new FenValue[0], execContext);
                }, 0);
            });

            return dc;
        }

        private static FenObject CreateRTCRtpSender(IExecutionContext context)
        {
            var execContext = EnsureExecutionContext(context);
            var sender = new FenObject();
            sender.Set("track", FenValue.Null);
            sender.Set("transport", FenValue.Null);
            sender.Set("dtmf", FenValue.Null);

            sender.Set("getParameters", FenValue.FromFunction(new FenFunction("getParameters", (args, thisVal) =>
            {
                var params_ = new FenObject();
                params_.Set("encodings", FenValue.FromObject(new FenObject()));
                params_.Set("transactionId", FenValue.FromString(""));
                return FenValue.FromObject(params_);
            })));

            sender.Set("setParameters", FenValue.FromFunction(new FenFunction("setParameters", (args, thisVal) =>
            {
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => {
                    eArgs[0].AsFunction().Invoke(new FenValue[0], execContext);
                    return FenValue.Undefined;
                })), execContext));
            })));

            sender.Set("replaceTrack", FenValue.FromFunction(new FenFunction("replaceTrack", (args, thisVal) =>
            {
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => {
                    eArgs[0].AsFunction().Invoke(new FenValue[0], execContext);
                    return FenValue.Undefined;
                })), execContext));
            })));

            sender.Set("getStats", FenValue.FromFunction(new FenFunction("getStats", (args, thisVal) =>
            {
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => {
                    eArgs[0].AsFunction().Invoke(new[] { FenValue.FromObject(new FenObject()) }, execContext);
                    return FenValue.Undefined;
                })), execContext));
            })));

            return sender;
        }

        private static FenObject CreateRTCRtpReceiver(IExecutionContext context)
        {
            var execContext = EnsureExecutionContext(context);
            var receiver = new FenObject();
            receiver.Set("track", FenValue.Null);
            receiver.Set("transport", FenValue.Null);
            receiver.Set("getStats", FenValue.FromFunction(new FenFunction("getStats", (args, thisVal) =>
            {
                return FenValue.FromObject(CreateResolvedPromise(FenValue.FromObject(new FenObject()), execContext));
            })));
            return receiver;
        }

        private static FenObject CreateRTCRtpTransceiver(FenObject sender, FenObject receiver)
        {
            var transceiver = new FenObject();
            transceiver.Set("mid", FenValue.Null);
            transceiver.Set("sender", FenValue.FromObject(sender));
            transceiver.Set("receiver", FenValue.FromObject(receiver));
            transceiver.Set("direction", FenValue.FromString("sendrecv"));
            transceiver.Set("currentDirection", FenValue.FromString("sendrecv"));
            transceiver.Set("stopped", FenValue.FromBoolean(false));
            transceiver.Set("stop", FenValue.FromFunction(new FenFunction("stop", (args, thisVal) =>
            {
                transceiver.Set("stopped", FenValue.FromBoolean(true));
                transceiver.Set("direction", FenValue.FromString("inactive"));
                transceiver.Set("currentDirection", FenValue.FromString("inactive"));
                return FenValue.Undefined;
            })));
            return transceiver;
        }
        /// <summary>
        /// Creates the MediaStream constructor
        /// </summary>
        public static FenObject CreateMediaStreamConstructor()
        {
            var constructor = new FenFunction("MediaStream", (args, thisVal) =>
            {
                return FenValue.FromObject(CreateMediaStream());
            })
            {
                IsConstructor = true,
                NativeLength = 0
            };

            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(constructor));
            constructor.Set("prototype", FenValue.FromObject(prototype));
            return constructor;
        }

        public static FenObject CreateMediaStream()
        {
            var stream = new FenObject();
            stream.Set("id", FenValue.FromString(Guid.NewGuid().ToString()));
            stream.Set("active", FenValue.FromBoolean(true));

            var tracks = new List<FenObject>();

            stream.Set("getTracks", FenValue.FromFunction(new FenFunction("getTracks", (args, thisVal) =>
            {
                var arr = new FenObject();
                for (int i = 0; i < tracks.Count; i++)
                    arr.Set(i.ToString(), FenValue.FromObject(tracks[i]));
                arr.Set("length", FenValue.FromNumber(tracks.Count));
                return FenValue.FromObject(arr);
            })));

            stream.Set("getAudioTracks", FenValue.FromFunction(new FenFunction("getAudioTracks", (args, thisVal) =>
            {
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(0));
                return FenValue.FromObject(arr);
            })));

            stream.Set("getVideoTracks", FenValue.FromFunction(new FenFunction("getVideoTracks", (args, thisVal) =>
            {
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(0));
                return FenValue.FromObject(arr);
            })));

            stream.Set("addTrack", FenValue.FromFunction(new FenFunction("addTrack", (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));

            stream.Set("removeTrack", FenValue.FromFunction(new FenFunction("removeTrack", (args, thisVal) =>
            {
                return FenValue.Undefined;
            })));

            stream.Set("clone", FenValue.FromFunction(new FenFunction("clone", (args, thisVal) =>
            {
                return FenValue.FromObject(CreateMediaStream());
            })));

            return stream;
        }

        private static IExecutionContext EnsureExecutionContext(IExecutionContext context)
        {
            return context ?? new FenBrowser.FenEngine.Core.ExecutionContext();
        }

        private static bool TryNormalizeRtcConfiguration(IObject configuration, out FenObject normalized, out string error)
        {
            normalized = new FenObject();
            error = null;

            var normalizedIceServers = new FenObject();
            var normalizedServerCount = 0;

            if (configuration == null)
            {
                normalizedIceServers.Set("length", FenValue.FromNumber(0));
                normalized.Set("iceServers", FenValue.FromObject(normalizedIceServers));
                return true;
            }

            var iceServersValue = configuration.Get("iceServers");
            if (!iceServersValue.IsObject)
            {
                normalizedIceServers.Set("length", FenValue.FromNumber(0));
                normalized.Set("iceServers", FenValue.FromObject(normalizedIceServers));
                return true;
            }

            var serversObject = iceServersValue.AsObject();
            var serverCount = ParseArrayLength(serversObject, MaxIceServers + 1);
            if (serverCount > MaxIceServers)
            {
                error = $"RTCPeerConnection supports at most {MaxIceServers} ICE servers.";
                return false;
            }

            for (var i = 0; i < serverCount; i++)
            {
                var serverValue = serversObject.Get(i.ToString());
                if (!serverValue.IsObject)
                {
                    continue;
                }

                var serverObject = serverValue.AsObject();
                var urlsValue = serverObject.Get("urls");
                var normalizedUrls = new FenObject();
                var normalizedUrlCount = 0;

                if (urlsValue.IsString)
                {
                    if (!TryValidateIceUrl(urlsValue.AsString(), out var normalizedUrl, out error))
                    {
                        return false;
                    }

                    normalizedUrls.Set("0", FenValue.FromString(normalizedUrl));
                    normalizedUrlCount = 1;
                }
                else if (urlsValue.IsObject)
                {
                    var urlsObject = urlsValue.AsObject();
                    var urlsCount = ParseArrayLength(urlsObject, MaxIceUrlsPerServer + 1);
                    if (urlsCount > MaxIceUrlsPerServer)
                    {
                        error = $"Each ICE server supports at most {MaxIceUrlsPerServer} URLs.";
                        return false;
                    }

                    for (var u = 0; u < urlsCount; u++)
                    {
                        var urlValue = urlsObject.Get(u.ToString());
                        if (!urlValue.IsString)
                        {
                            continue;
                        }

                        if (!TryValidateIceUrl(urlValue.AsString(), out var normalizedUrl, out error))
                        {
                            return false;
                        }

                        normalizedUrls.Set(normalizedUrlCount.ToString(), FenValue.FromString(normalizedUrl));
                        normalizedUrlCount++;
                    }
                }

                normalizedUrls.Set("length", FenValue.FromNumber(normalizedUrlCount));

                var normalizedServer = new FenObject();
                normalizedServer.Set("urls", FenValue.FromObject(normalizedUrls));

                var usernameValue = serverObject.Get("username");
                if (usernameValue.IsString)
                {
                    normalizedServer.Set("username", FenValue.FromString(SanitizeConfigText(usernameValue.AsString(), 256)));
                }

                var credentialValue = serverObject.Get("credential");
                if (credentialValue.IsString)
                {
                    normalizedServer.Set("credential", FenValue.FromString(SanitizeConfigText(credentialValue.AsString(), 512)));
                }

                normalizedIceServers.Set(normalizedServerCount.ToString(), FenValue.FromObject(normalizedServer));
                normalizedServerCount++;
            }

            normalizedIceServers.Set("length", FenValue.FromNumber(normalizedServerCount));
            normalized.Set("iceServers", FenValue.FromObject(normalizedIceServers));
            return true;
        }

        private static int ParseArrayLength(IObject arrayLike, int clamp)
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

            var rawLength = (int)Math.Round(lengthValue.ToNumber());
            if (rawLength <= 0)
            {
                return 0;
            }

            return Math.Min(rawLength, clamp);
        }

        private static string SanitizeConfigText(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
        }

        private static bool TryValidateIceUrl(string value, out string normalized, out string error)
        {
            normalized = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                error = "ICE URL cannot be empty.";
                return false;
            }

            var trimmed = value.Trim();
            if (trimmed.Length > MaxIceUrlLength)
            {
                error = "ICE URL exceeds maximum length.";
                return false;
            }

            foreach (var ch in trimmed)
            {
                if (char.IsControl(ch))
                {
                    error = "ICE URL contains control characters.";
                    return false;
                }
            }

            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                error = "ICE URL missing scheme.";
                return false;
            }

            var scheme = trimmed.Substring(0, separator);
            if (!AllowedIceSchemes.Contains(scheme))
            {
                error = $"ICE URL scheme '{scheme}' is not allowed.";
                return false;
            }

            var endpoint = trimmed.Substring(separator + 1).TrimStart('/');
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                error = "ICE URL missing endpoint.";
                return false;
            }

            var parseCandidate = scheme + "://" + endpoint;
            if (!Uri.TryCreate(parseCandidate, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "ICE URL endpoint is invalid.";
                return false;
            }

            if (IsPrivateOrReservedHost(uri.Host))
            {
                error = "ICE URL host is private or reserved.";
                return false;
            }

            normalized = trimmed;
            return true;
        }

        private static bool IsPrivateOrReservedHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return true;
            }

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!IPAddress.TryParse(host, out var ipAddress))
            {
                return false;
            }

            if (IPAddress.IsLoopback(ipAddress))
            {
                return true;
            }

            if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ipAddress.GetAddressBytes();
                return bytes[0] == 10 ||
                       bytes[0] == 127 ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 169 && bytes[1] == 254);
            }

            if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = ipAddress.GetAddressBytes();
                var isUniqueLocal = (bytes[0] & 0xFE) == 0xFC;
                return isUniqueLocal || ipAddress.IsIPv6LinkLocal || IPAddress.IPv6Loopback.Equals(ipAddress);
            }

            return false;
        }

        private static void TryInvokeHandler(FenValue callback, FenValue[] args, IExecutionContext context, string operation)
        {
            if (!callback.IsFunction)
            {
                return;
            }

            try
            {
                callback.AsFunction().Invoke(args, context);
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[WebRTC] {operation} handler failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private static FenObject CreateStatsReport(int connectionId, string connectionState, string iceConnectionState, int senderCount, int receiverCount, int dataChannelCount)
        {
            var report = new FenObject();
            report.Set("size", FenValue.FromNumber(1));

            var peerConnectionStats = new FenObject();
            peerConnectionStats.Set("id", FenValue.FromString($"pc-{connectionId}"));
            peerConnectionStats.Set("type", FenValue.FromString("peer-connection"));
            peerConnectionStats.Set("timestamp", FenValue.FromNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            peerConnectionStats.Set("connectionState", FenValue.FromString(connectionState ?? "new"));
            peerConnectionStats.Set("iceConnectionState", FenValue.FromString(iceConnectionState ?? "new"));
            peerConnectionStats.Set("senders", FenValue.FromNumber(senderCount));
            peerConnectionStats.Set("receivers", FenValue.FromNumber(receiverCount));
            peerConnectionStats.Set("dataChannels", FenValue.FromNumber(dataChannelCount));

            report.Set("0", FenValue.FromObject(peerConnectionStats));
            report.Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    args[0].AsFunction().Invoke(new[]
                    {
                        FenValue.FromObject(peerConnectionStats),
                        FenValue.FromString($"pc-{connectionId}"),
                        FenValue.FromObject(report)
                    }, null);
                }

                return FenValue.Undefined;
            })));

            return report;
        }

        private static FenObject CreateResolvedPromise(FenValue value, IExecutionContext context)
        {
            var execContext = EnsureExecutionContext(context);
            return new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    args[0].AsFunction().Invoke(new[] { value }, execContext);
                }

                return FenValue.Undefined;
            })), execContext);
        }

        private static string NormalizeBinaryType(string value)
        {
            return string.Equals(value, "blob", StringComparison.OrdinalIgnoreCase) ? "blob" : "arraybuffer";
        }
    }
}



