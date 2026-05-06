using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    [Collection("Engine Tests")]
    public class SerialApiTests
    {
        [Fact]
        public void GetPorts_RejectsOnInsecureContext()
        {
            var context = CreateContext("http://example.com/page", JsPermissions.StandardWeb | JsPermissions.Serial);
            var serial = SerialAPI.CreateSerial(context);

            var reason = CaptureRejectedReason(serial.Get("getPorts").AsFunction().Invoke(Array.Empty<FenValue>(), context));

            Assert.Contains("NotAllowedError", reason, StringComparison.Ordinal);
            Assert.Contains("secure context", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GetPorts_WithoutSerialPermission_ResolvesEmptyArray()
        {
            var context = CreateContext("https://example.com/page", JsPermissions.StandardWeb);
            var serial = SerialAPI.CreateSerial(context);

            var resolved = CaptureResolvedValue(serial.Get("getPorts").AsFunction().Invoke(Array.Empty<FenValue>(), context));
            var ports = Assert.IsAssignableFrom<FenObject>(resolved.AsObject());

            Assert.Equal(0, (int)ports.Get("length").ToNumber());
        }

        [Fact]
        public void GetPorts_WithPermission_ResolvesEmptyArray()
        {
            var context = CreateContext("https://example.com/page", JsPermissions.StandardWeb | JsPermissions.Serial);
            var serial = SerialAPI.CreateSerial(context);

            var resolved = CaptureResolvedValue(serial.Get("getPorts").AsFunction().Invoke(Array.Empty<FenValue>(), context));
            var ports = Assert.IsAssignableFrom<FenObject>(resolved.AsObject());

            Assert.Equal(0, (int)ports.Get("length").ToNumber());
            Assert.True(ports.Get("0").IsUndefined);
        }

        [Fact]
        public void GetPorts_AboutBlankContext_ResolvesEmptyArray()
        {
            var context = CreateContext("about:blank", JsPermissions.StandardWeb);
            var serial = SerialAPI.CreateSerial(context);

            var resolved = CaptureResolvedValue(serial.Get("getPorts").AsFunction().Invoke(Array.Empty<FenValue>(), context));
            var ports = Assert.IsAssignableFrom<FenObject>(resolved.AsObject());
            Assert.Equal(0, (int)ports.Get("length").ToNumber());
        }

        [Fact]
        public void RequestPort_WithPermissionButNoBackend_RejectsNotFound()
        {
            var context = CreateContext("https://example.com/page", JsPermissions.StandardWeb | JsPermissions.Serial);
            var serial = SerialAPI.CreateSerial(context);

            var reason = CaptureRejectedReason(serial.Get("requestPort").AsFunction().Invoke(Array.Empty<FenValue>(), context));

            Assert.Contains("NotFoundError", reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RequestPort_InvalidOptions_RejectsTypeError()
        {
            var context = CreateContext("https://example.com/page", JsPermissions.StandardWeb | JsPermissions.Serial);
            var serial = SerialAPI.CreateSerial(context);

            var reason = CaptureRejectedReason(serial.Get("requestPort").AsFunction().Invoke(new[] { FenValue.FromNumber(42) }, context));

            Assert.Contains("TypeError", reason, StringComparison.Ordinal);
        }

        private static FenBrowser.FenEngine.Core.ExecutionContext CreateContext(string documentUrl, JsPermissions permissions)
        {
            var permissionManager = new PermissionManager(permissions);
            return new FenBrowser.FenEngine.Core.ExecutionContext(permissionManager)
            {
                DocumentUrl = new Uri(documentUrl)
            };
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
