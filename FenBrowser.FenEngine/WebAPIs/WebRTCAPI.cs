using System;
using System.Collections.Generic;
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
        private static int _connectionIdCounter = 0;

        /// <summary>
        /// Creates the RTCPeerConnection constructor
        /// </summary>
        public static FenObject CreateRTCPeerConnectionConstructor(IExecutionContext context)
        {
            var constructor = new FenObject();
            constructor.Set("__call__", FenValue.FromFunction(new FenFunction("RTCPeerConnection", (args, thisVal) =>
            {
                var config = args.Length > 0 && args[0].IsObject ? args[0].AsObject() : null;
                return FenValue.FromObject(CreateRTCPeerConnection(config, context));
            })));
            return constructor;
        }

        /// <summary>
        /// Creates an RTCPeerConnection instance
        /// </summary>
        public static FenObject CreateRTCPeerConnection(IObject configuration, IExecutionContext context)
        {
            var connectionId = ++_connectionIdCounter;
            var pc = new FenObject();
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

            // Event handlers (null by default)
            pc.Set("onicecandidate", FenValue.Null);
            pc.Set("oniceconnectionstatechange", FenValue.Null);
            pc.Set("onicegatheringstatechange", FenValue.Null);
            pc.Set("onconnectionstatechange", FenValue.Null);
            pc.Set("onsignalingstatechange", FenValue.Null);
            pc.Set("ondatachannel", FenValue.Null);
            pc.Set("ontrack", FenValue.Null);
            pc.Set("onnegotiationneeded", FenValue.Null);

            // createOffer()
            pc.Set("createOffer", FenValue.FromFunction(new FenFunction("createOffer", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] createOffer()", LogCategory.JavaScript);
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => 
                {
                    var offer = new FenObject();
                    offer.Set("type", FenValue.FromString("offer"));
                    offer.Set("sdp", FenValue.FromString($"v=0\r\no=- {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"));
                    eArgs[0].AsFunction().Invoke(new[] { FenValue.FromObject(offer) }, context);
                    return FenValue.Undefined;
                })), context));
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
                    eArgs[0].AsFunction().Invoke(new[] { FenValue.FromObject(answer) }, context);
                    return FenValue.Undefined;
                })), context));
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
                    eArgs[0].AsFunction().Invoke(new IValue[0], context);
                    return FenValue.Undefined;
                })), context));
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
                    eArgs[0].AsFunction().Invoke(new IValue[0], context);
                    return FenValue.Undefined;
                })), context));
            })));

            // addIceCandidate()
            pc.Set("addIceCandidate", FenValue.FromFunction(new FenFunction("addIceCandidate", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] addIceCandidate()", LogCategory.JavaScript);
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => 
                {
                    eArgs[0].AsFunction().Invoke(new IValue[0], context);
                    return FenValue.Undefined;
                })), context));
            })));

            // createDataChannel()
            pc.Set("createDataChannel", FenValue.FromFunction(new FenFunction("createDataChannel", (args, thisVal) =>
            {
                var label = args.Length > 0 ? args[0].ToString() : "";
                FenLogger.Debug($"[WebRTC] createDataChannel({label})", LogCategory.JavaScript);
                return FenValue.FromObject(CreateRTCDataChannel(label, context));
            })));

            // addTrack()
            pc.Set("addTrack", FenValue.FromFunction(new FenFunction("addTrack", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] addTrack()", LogCategory.JavaScript);
                return FenValue.FromObject(CreateRTCRtpSender(context));
            })));

            // removeTrack()
            pc.Set("removeTrack", FenValue.FromFunction(new FenFunction("removeTrack", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] removeTrack()", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));

            // getSenders()
            pc.Set("getSenders", FenValue.FromFunction(new FenFunction("getSenders", (args, thisVal) =>
            {
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(0));
                return FenValue.FromObject(arr);
            })));

            // getReceivers()
            pc.Set("getReceivers", FenValue.FromFunction(new FenFunction("getReceivers", (args, thisVal) =>
            {
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(0));
                return FenValue.FromObject(arr);
            })));

            // getTransceivers()
            pc.Set("getTransceivers", FenValue.FromFunction(new FenFunction("getTransceivers", (args, thisVal) =>
            {
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(0));
                return FenValue.FromObject(arr);
            })));

            // getStats()
            pc.Set("getStats", FenValue.FromFunction(new FenFunction("getStats", (args, thisVal) =>
            {
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => 
                {
                    eArgs[0].AsFunction().Invoke(new[] { FenValue.FromObject(new FenObject()) }, context);
                    return FenValue.Undefined;
                })), context));
            })));

            // close()
            pc.Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                FenLogger.Debug($"[WebRTC] RTCPeerConnection #{connectionId} closed", LogCategory.JavaScript);
                pc.Set("connectionState", FenValue.FromString("closed"));
                pc.Set("iceConnectionState", FenValue.FromString("closed"));
                pc.Set("signalingState", FenValue.FromString("closed"));
                return FenValue.Undefined;
            })));

            return pc;
        }

        private static FenObject CreateRTCDataChannel(string label, IExecutionContext context)
        {
            var dc = new FenObject();
            dc.Set("label", FenValue.FromString(label));
            dc.Set("ordered", FenValue.FromBoolean(true));
            dc.Set("maxPacketLifeTime", FenValue.Null);
            dc.Set("maxRetransmits", FenValue.Null);
            dc.Set("protocol", FenValue.FromString(""));
            dc.Set("negotiated", FenValue.FromBoolean(false));
            dc.Set("id", FenValue.FromNumber(0));
            dc.Set("readyState", FenValue.FromString("connecting"));
            dc.Set("bufferedAmount", FenValue.FromNumber(0));
            dc.Set("bufferedAmountLowThreshold", FenValue.FromNumber(0));
            dc.Set("binaryType", FenValue.FromString("arraybuffer"));

            // Event handlers
            dc.Set("onopen", FenValue.Null);
            dc.Set("onclose", FenValue.Null);
            dc.Set("onerror", FenValue.Null);
            dc.Set("onmessage", FenValue.Null);
            dc.Set("onbufferedamountlow", FenValue.Null);

            dc.Set("send", FenValue.FromFunction(new FenFunction("send", (args, thisVal) =>
            {
                FenLogger.Debug("[WebRTC] DataChannel.send()", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));

            dc.Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                dc.Set("readyState", FenValue.FromString("closed"));
                return FenValue.Undefined;
            })));

            // Simulate channel opening
            Task.Run(async () =>
            {
                await Task.Delay(100);
                dc.Set("readyState", FenValue.FromString("open"));
                context.ScheduleCallback(() => {
                    var onopen = dc.Get("onopen");
                    if (onopen.IsFunction) onopen.AsFunction().Invoke(new IValue[0], context);
                }, 0);
            });

            return dc;
        }

        private static FenObject CreateRTCRtpSender(IExecutionContext context)
        {
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
                    eArgs[0].AsFunction().Invoke(new IValue[0], context);
                    return FenValue.Undefined;
                })), context));
            })));

            sender.Set("replaceTrack", FenValue.FromFunction(new FenFunction("replaceTrack", (args, thisVal) =>
            {
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => {
                    eArgs[0].AsFunction().Invoke(new IValue[0], context);
                    return FenValue.Undefined;
                })), context));
            })));

            sender.Set("getStats", FenValue.FromFunction(new FenFunction("getStats", (args, thisVal) =>
            {
                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("exec", (eArgs, eThis) => {
                    eArgs[0].AsFunction().Invoke(new[] { FenValue.FromObject(new FenObject()) }, context);
                    return FenValue.Undefined;
                })), context));
            })));

            return sender;
        }

        /// <summary>
        /// Creates the MediaStream constructor
        /// </summary>
        public static FenObject CreateMediaStreamConstructor()
        {
            var constructor = new FenObject();
            constructor.Set("__call__", FenValue.FromFunction(new FenFunction("MediaStream", (args, thisVal) =>
            {
                return FenValue.FromObject(CreateMediaStream());
            })));
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
    }
}
