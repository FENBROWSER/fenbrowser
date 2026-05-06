using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// MediaDevices API per W3C Media Capture and Streams spec.
    /// https://www.w3.org/TR/mediacapture-streams/#mediadevices
    /// </summary>
    public static class MediaDevicesAPI
    {
        private sealed class MediaDevicesState
        {
            public FenValue OnDeviceChangeHandler { get; set; } = FenValue.Null;
        }

        public static FenObject CreateMediaDevices(IExecutionContext context)
        {
            var mediaDevices = new FenObject();
            var state = new MediaDevicesState();

            mediaDevices.Set("ondevicechange", state.OnDeviceChangeHandler);

            mediaDevices.Set("getUserMedia", FenValue.FromFunction(new FenFunction("getUserMedia", (args, thisVal) => GetUserMedia(args, context))));
            mediaDevices.Set("enumerateDevices", FenValue.FromFunction(new FenFunction("enumerateDevices", (args, thisVal) => EnumerateDevices(context))));
            mediaDevices.Set("getDisplayMedia", FenValue.FromFunction(new FenFunction("getDisplayMedia", (args, thisVal) => GetDisplayMedia(context))));

            SetupEventTargetMethods(mediaDevices, state);
            return mediaDevices;
        }

        private static FenValue GetUserMedia(FenValue[] args, IExecutionContext context)
        {
            if (!TryParseRequestedMedia(args, out var audioRequested, out var videoRequested, out var parseError))
                return Reject(parseError, context);

            if (!IsSecureContext(context))
                return Reject("NotAllowedError: getUserMedia() requires a secure context.", context);

            if (!HasCameraPermission(context, "navigator.mediaDevices.getUserMedia"))
                return Reject("NotAllowedError: Permission denied for camera/microphone access.", context);

            EngineLogCompat.Warn(
                $"[MediaDevicesAPI] getUserMedia blocked: audio={audioRequested}, video={videoRequested}, no capture backend registered",
                LogCategory.JavaScript);

            return Reject("NotFoundError: No camera or microphone devices are available.", context);
        }

        private static FenValue EnumerateDevices(IExecutionContext context)
        {
            if (!IsSecureContext(context))
                return Reject("NotAllowedError: enumerateDevices() requires a secure context.", context);

            // Privacy-first: without a host capture backend we do not invent fake devices
            // or labels. Return a deterministic empty list.
            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(CreateDeviceArray(Array.Empty<FenObject>())), context));
        }

        private static FenValue GetDisplayMedia(IExecutionContext context)
        {
            if (!IsSecureContext(context))
                return Reject("NotAllowedError: getDisplayMedia() requires a secure context.", context);

            if (!HasCameraPermission(context, "navigator.mediaDevices.getDisplayMedia"))
                return Reject("NotAllowedError: Permission denied for screen capture.", context);

            EngineLogCompat.Warn("[MediaDevicesAPI] getDisplayMedia blocked: no screen picker backend registered", LogCategory.JavaScript);
            return Reject("NotFoundError: Screen capture backend is not available.", context);
        }

        private static void SetupEventTargetMethods(FenObject mediaDevices, MediaDevicesState state)
        {
            mediaDevices.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                var listener = args[1];
                if (type == "devicechange")
                {
                    state.OnDeviceChangeHandler = listener;
                    mediaDevices.Set("ondevicechange", listener);
                }
                return FenValue.Undefined;
            })));

            mediaDevices.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                if (type == "devicechange")
                {
                    state.OnDeviceChangeHandler = FenValue.Null;
                    mediaDevices.Set("ondevicechange", FenValue.Null);
                }
                return FenValue.Undefined;
            })));
        }

        private static bool TryParseRequestedMedia(FenValue[] args, out bool audioRequested, out bool videoRequested, out string error)
        {
            audioRequested = false;
            videoRequested = false;
            error = null;

            if (args.Length == 0 || !args[0].IsObject)
            {
                error = "TypeError: getUserMedia() requires a constraints object.";
                return false;
            }

            var constraints = args[0].AsObject();
            if (constraints == null)
            {
                error = "TypeError: getUserMedia() requires a constraints object.";
                return false;
            }

            audioRequested = IsTrackRequested(constraints.Get("audio"));
            videoRequested = IsTrackRequested(constraints.Get("video"));

            if (!audioRequested && !videoRequested)
            {
                error = "TypeError: getUserMedia() requires at least one of audio/video to be requested.";
                return false;
            }

            return true;
        }

        private static bool IsTrackRequested(FenValue trackConstraint)
        {
            if (trackConstraint.IsUndefined || trackConstraint.IsNull)
                return false;

            if (trackConstraint.IsBoolean)
                return trackConstraint.AsBoolean();

            if (!trackConstraint.IsObject)
                return false;

            var obj = trackConstraint.AsObject();
            if (obj == null)
                return false;

            var exact = obj.Get("exact");
            if (exact.IsBoolean && !exact.AsBoolean())
                return false;

            return true;
        }

        private static bool HasCameraPermission(IExecutionContext context, string operation)
        {
            var permissions = context?.Permissions;
            if (permissions == null)
                return false;

            return permissions.CheckAndLog(JsPermissions.Camera, operation);
        }

        private static bool IsSecureContext(IExecutionContext context)
        {
            var documentUrl = context?.DocumentUrl;
            if (documentUrl == null || !documentUrl.IsAbsoluteUri)
                return false;

            var scheme = documentUrl.Scheme ?? string.Empty;
            if (scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return true;

            if (scheme.Equals("fen", StringComparison.OrdinalIgnoreCase))
                return true;

            if (scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                return IsLoopbackHost(documentUrl.Host);

            return false;
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            if (host.Equals("::1", StringComparison.OrdinalIgnoreCase))
                return true;

            if (host.StartsWith("127.", StringComparison.Ordinal))
                return true;

            return false;
        }

        private static FenObject CreateDeviceArray(IReadOnlyList<FenObject> devices)
        {
            var arr = new FenObject();
            arr.Set("length", FenValue.FromNumber(devices.Count));
            for (int i = 0; i < devices.Count; i++)
                arr.Set(i.ToString(), FenValue.FromObject(devices[i]));
            return arr;
        }

        private static FenValue Reject(string reason, IExecutionContext context)
        {
            return FenValue.FromObject(ResolvedThenable.Rejected(reason, context));
        }
    }
}
