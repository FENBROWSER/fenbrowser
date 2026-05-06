using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    [Collection("Engine Tests")]
    public class NavigationApiTests
    {
        [Fact]
        public void NavigationState_IsIsolatedPerInstance()
        {
            var contextA = CreateContext("https://a.example/start");
            var contextB = CreateContext("https://b.example/home");
            var navigationA = NavigationAPI.CreateNavigation(contextA);
            var navigationB = NavigationAPI.CreateNavigation(contextB);

            var navigateA = navigationA.Get("navigate").AsFunction();
            var resolved = CaptureResolvedValue(navigateA.Invoke(new[] { FenValue.FromString("https://a.example/next") }, contextA));
            Assert.True(resolved.IsObject);

            var entriesA = navigationA.Get("entries").AsFunction().Invoke(Array.Empty<FenValue>(), contextA).AsObject();
            var entriesB = navigationB.Get("entries").AsFunction().Invoke(Array.Empty<FenValue>(), contextB).AsObject();

            Assert.Equal(2, (int)entriesA.Get("length").ToNumber());
            Assert.Equal(1, (int)entriesB.Get("length").ToNumber());

            var currentB = navigationB.Get("getCurrentEntry").AsFunction().Invoke(Array.Empty<FenValue>(), contextB).AsObject();
            Assert.Equal("https://b.example/home", currentB.Get("url").AsString());
        }

        [Fact]
        public void Navigate_AddsExactlyOneEntry()
        {
            var context = CreateContext("https://example.com/start");
            var navigation = NavigationAPI.CreateNavigation(context);

            var navigate = navigation.Get("navigate").AsFunction();
            var resolved = CaptureResolvedValue(navigate.Invoke(new[] { FenValue.FromString("https://example.com/next") }, context));
            var entry = Assert.IsAssignableFrom<FenObject>(resolved.AsObject());

            Assert.Equal("https://example.com/next", entry.Get("url").AsString());
            Assert.Equal(1d, entry.Get("index").ToNumber());

            var entries = navigation.Get("entries").AsFunction().Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.Equal(2, (int)entries.Get("length").ToNumber());

            var currentEntry = navigation.Get("currentEntry").AsObject();
            Assert.Equal("https://example.com/next", currentEntry.Get("url").AsString());
        }

        [Fact]
        public void TraverseTo_InvalidKey_RejectsWithInvalidAccessError()
        {
            var context = CreateContext("https://example.com/start");
            var navigation = NavigationAPI.CreateNavigation(context);
            var traverseTo = navigation.Get("traverseTo").AsFunction();

            var reason = CaptureRejectedReason(traverseTo.Invoke(new[] { FenValue.FromString("missing-key") }, context));

            Assert.Contains("InvalidAccessError", reason, StringComparison.Ordinal);
        }

        private static FenBrowser.FenEngine.Core.ExecutionContext CreateContext(string currentUrl)
        {
            return new FenBrowser.FenEngine.Core.ExecutionContext(new PermissionManager(JsPermissions.StandardWeb))
            {
                DocumentUrl = new Uri(currentUrl),
                CurrentUrl = currentUrl
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
