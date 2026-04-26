using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// MediaDevices API per W3C Media Capture and Streams spec.
    /// https://www.w3.org/TR/mediacapture-streams/#mediadevices
    /// </summary>
    public static class MediaDevicesAPI
    {
        private static FenValue _onDeviceChangeHandler;
        private static IExecutionContext _context;

        public static FenObject CreateMediaDevices(IExecutionContext context)
        {
            _context = context;
            var mediaDevices = new FenObject();

            _onDeviceChangeHandler = FenValue.Null;
            mediaDevices.Set("ondevicechange", _onDeviceChangeHandler);

            mediaDevices.Set("getUserMedia", FenValue.FromFunction(new FenFunction("getUserMedia", (args, thisVal) => GetUserMedia(args, thisVal, context))));
            mediaDevices.Set("enumerateDevices", FenValue.FromFunction(new FenFunction("enumerateDevices", (args, thisVal) => EnumerateDevices(args, thisVal, context))));
            mediaDevices.Set("getDisplayMedia", FenValue.FromFunction(new FenFunction("getDisplayMedia", (args, thisVal) => GetDisplayMedia(args, thisVal, context))));

            SetupEventTargetMethods(mediaDevices, context);
            return mediaDevices;
        }

        private static FenValue GetUserMedia(FenValue[] args, FenValue thisVal, IExecutionContext context)
        {
            bool audioRequested = false;
            bool videoRequested = false;

            if (args.Length > 0 && args[0].IsObject)
            {
                var constraints = args[0].AsObject();
                var audio = constraints.Get("audio");
                var video = constraints.Get("video");

                audioRequested = !audio.IsNull && audio.IsObject
                    ? !IsConstraintFalse(audio.AsObject().Get("exact"))
                    : audio.IsBoolean && audio.AsBoolean();

                videoRequested = !video.IsNull && video.IsObject
                    ? !IsConstraintFalse(video.AsObject().Get("exact"))
                    : video.IsBoolean && video.AsBoolean();
            }

            EngineLogCompat.Warn($"[MediaDevicesAPI] getUserMedia called - audio={audioRequested}, video={videoRequested} (stub implementation)", LogCategory.JavaScript);

            var stream = new MediaStream();

            if (audioRequested)
            {
                var audioTrack = new MediaStreamTrack("audio", "Microphone (FAKE)");
                stream.AddTrack(audioTrack);
            }

            if (videoRequested)
            {
                var videoTrack = new MediaStreamTrack("video", "Camera (FAKE)");
                stream.AddTrack(videoTrack);
            }

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(stream), context));
        }

        private static FenValue EnumerateDevices(FenValue[] args, FenValue thisVal, IExecutionContext context)
        {
            var devices = new FenObject();
            var deviceList = new List<FenObject>();

            var videoInput = new FenObject();
            videoInput.Set("kind", FenValue.FromString("videoinput"));
            videoInput.Set("label", FenValue.FromString("Camera (FAKE - permission denied)"));
            videoInput.Set("deviceId", FenValue.FromString("fake-camera-id"));
            videoInput.Set("groupId", FenValue.FromString("fake-group-id"));
            deviceList.Add(videoInput);

            var audioInput = new FenObject();
            audioInput.Set("kind", FenValue.FromString("audioinput"));
            audioInput.Set("label", FenValue.FromString("Microphone (FAKE - permission denied)"));
            audioInput.Set("deviceId", FenValue.FromString("fake-mic-id"));
            audioInput.Set("groupId", FenValue.FromString("fake-group-id"));
            deviceList.Add(audioInput);

            var audioOutput = new FenObject();
            audioOutput.Set("kind", FenValue.FromString("audiooutput"));
            audioOutput.Set("label", FenValue.FromString("Speaker (FAKE - permission denied)"));
            audioOutput.Set("deviceId", FenValue.FromString("fake-speaker-id"));
            audioOutput.Set("groupId", FenValue.FromString("fake-group-id"));
            deviceList.Add(audioOutput);

            devices.Set("length", FenValue.FromNumber(deviceList.Count));
            for (int i = 0; i < deviceList.Count; i++)
                devices.Set(i.ToString(), FenValue.FromObject(deviceList[i]));

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(devices), context));
        }

        private static FenValue GetDisplayMedia(FenValue[] args, FenValue thisVal, IExecutionContext context)
        {
            EngineLogCompat.Warn("[MediaDevicesAPI] getDisplayMedia called - screen picker UI not yet implemented", LogCategory.JavaScript);
            return FenValue.FromObject(ResolvedThenable.Rejected("NotAllowedError: getDisplayMedia() requires user permission", context));
        }

        private static void SetupEventTargetMethods(FenObject mediaDevices, IExecutionContext context)
        {
            mediaDevices.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                var listener = args[1];
                if (type == "devicechange") _onDeviceChangeHandler = listener;
                return FenValue.Undefined;
            })));

            mediaDevices.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                if (type == "devicechange") _onDeviceChangeHandler = FenValue.Null;
                return FenValue.Undefined;
            })));
        }

        private static bool IsConstraintFalse(FenValue val)
        {
            return val.IsBoolean && !val.AsBoolean();
        }
    }
}