using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    [Collection("Engine Tests")]
    public class MediaDevicesApiTests
    {
        [Fact]
        public void GetUserMedia_RequiresConstraintsObject()
        {
            var context = CreateContext("https://example.com/page", JsPermissions.StandardWeb | JsPermissions.Camera);
            var mediaDevices = MediaDevicesAPI.CreateMediaDevices(context);
            var getUserMedia = mediaDevices.Get("getUserMedia").AsFunction();

            var reason = CaptureRejectedReason(getUserMedia.Invoke(Array.Empty<FenValue>(), context));

            Assert.Contains("TypeError", reason, StringComparison.Ordinal);
        }

        [Fact]
        public void GetUserMedia_RejectsOnInsecureContext()
        {
            var context = CreateContext("http://example.com/page", JsPermissions.StandardWeb | JsPermissions.Camera);
            var mediaDevices = MediaDevicesAPI.CreateMediaDevices(context);
            var getUserMedia = mediaDevices.Get("getUserMedia").AsFunction();

            var reason = CaptureRejectedReason(getUserMedia.Invoke(new[] { CreateConstraints(audio: true, video: false) }, context));

            Assert.Contains("NotAllowedError", reason, StringComparison.Ordinal);
            Assert.Contains("secure context", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GetUserMedia_RejectsWithoutCameraPermission()
        {
            var context = CreateContext("https://example.com/page", JsPermissions.StandardWeb);
            var mediaDevices = MediaDevicesAPI.CreateMediaDevices(context);
            var getUserMedia = mediaDevices.Get("getUserMedia").AsFunction();

            var reason = CaptureRejectedReason(getUserMedia.Invoke(new[] { CreateConstraints(audio: true, video: false) }, context));

            Assert.Contains("NotAllowedError", reason, StringComparison.Ordinal);
            Assert.Contains("Permission denied", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GetUserMedia_WithPermissionButNoBackend_RejectsNotFound()
        {
            var context = CreateContext("https://example.com/page", JsPermissions.StandardWeb | JsPermissions.Camera);
            var mediaDevices = MediaDevicesAPI.CreateMediaDevices(context);
            var getUserMedia = mediaDevices.Get("getUserMedia").AsFunction();

            var reason = CaptureRejectedReason(getUserMedia.Invoke(new[] { CreateConstraints(audio: true, video: true) }, context));

            Assert.Contains("NotFoundError", reason, StringComparison.Ordinal);
        }

        [Fact]
        public void EnumerateDevices_ReturnsDeterministicEmptyList()
        {
            var context = CreateContext("https://example.com/page", JsPermissions.StandardWeb | JsPermissions.Camera);
            var mediaDevices = MediaDevicesAPI.CreateMediaDevices(context);
            var enumerateDevices = mediaDevices.Get("enumerateDevices").AsFunction();

            var resolved = CaptureResolvedValue(enumerateDevices.Invoke(Array.Empty<FenValue>(), context));
            var list = Assert.IsAssignableFrom<FenObject>(resolved.AsObject());

            Assert.Equal(0, (int)list.Get("length").ToNumber());
            Assert.True(list.Get("0").IsUndefined);
        }

        private static FenBrowser.FenEngine.Core.ExecutionContext CreateContext(string documentUrl, JsPermissions permissions)
        {
            var permissionManager = new PermissionManager(permissions);
            return new FenBrowser.FenEngine.Core.ExecutionContext(permissionManager)
            {
                DocumentUrl = new Uri(documentUrl)
            };
        }

        private static FenValue CreateConstraints(bool audio, bool video)
        {
            var constraints = new FenObject();
            constraints.Set("audio", FenValue.FromBoolean(audio));
            constraints.Set("video", FenValue.FromBoolean(video));
            return FenValue.FromObject(constraints);
        }

        private static string CaptureRejectedReason(FenValue promiseValue)
        {
            Assert.True(promiseValue.IsObject, "Expected a promise-like object.");
            var promise = promiseValue.AsObject();
            Assert.True(promise.Get("catch").IsFunction, "Expected a catch() method.");

            string reason = null;
            promise.Get("catch").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("onRejected", (args, _) =>
                    {
                        reason = args.Length > 0 ? FormatPromiseValue(args[0]) : string.Empty;
                        return FenValue.Undefined;
                    }))
                },
                null);

            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
            Assert.NotNull(reason);
            return reason;
        }

        private static FenValue CaptureResolvedValue(FenValue promiseValue)
        {
            Assert.True(promiseValue.IsObject, "Expected a promise-like object.");
            var promise = promiseValue.AsObject();
            Assert.True(promise.Get("then").IsFunction, "Expected a then() method.");

            FenValue resolved = FenValue.Undefined;
            var called = false;

            promise.Get("then").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("onFulfilled", (args, _) =>
                    {
                        called = true;
                        resolved = args.Length > 0 ? args[0] : FenValue.Undefined;
                        return FenValue.Undefined;
                    }))
                },
                null);

            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
            Assert.True(called);
            return resolved;
        }

        private static string FormatPromiseValue(FenValue value)
        {
            if (value.IsObject)
            {
                var obj = value.AsObject();
                if (obj != null)
                {
                    var name = obj.Get("name");
                    var message = obj.Get("message");
                    if (name.IsString && message.IsString)
                        return $"{name.AsString()}: {message.AsString()}";
                }
            }

            return value.ToString();
        }
    }
}
