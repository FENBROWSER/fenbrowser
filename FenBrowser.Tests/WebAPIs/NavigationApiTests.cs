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
            var navigationA = NavigationAPI.CreateNavigationObject(contextA, (_, __) => { });
            var navigationB = NavigationAPI.CreateNavigationObject(contextB, (_, __) => { });

            var navigateA = navigationA.Get("navigate").AsFunction();
            var resolved = CaptureResolvedValue(navigateA.Invoke(new[] { FenValue.FromString("https://a.example/next") }, contextA));
            Assert.True(resolved.IsObject);

            var entriesA = navigationA.Get("entries").AsFunction().Invoke(Array.Empty<FenValue>(), contextA).AsObject();
            var entriesB = navigationB.Get("entries").AsFunction().Invoke(Array.Empty<FenValue>(), contextB).AsObject();

            Assert.Equal(2, (int)entriesA.Get("length").ToNumber());
            Assert.Equal(1, (int)entriesB.Get("length").ToNumber());

            var currentB = navigationB.Get("currentEntry").AsFunction().Invoke(Array.Empty<FenValue>(), contextB).AsObject();
            Assert.Equal("https://b.example/home", ReadEntryUrl(currentB, contextB));
        }

        [Fact]
        public void Navigate_AddsExactlyOneEntry()
        {
            var context = CreateContext("https://example.com/start");
            var navigation = NavigationAPI.CreateNavigationObject(context, (_, __) => { });

            var navigate = navigation.Get("navigate").AsFunction();
            var resolved = CaptureResolvedValue(navigate.Invoke(new[] { FenValue.FromString("https://example.com/next") }, context));
            var entry = Assert.IsAssignableFrom<FenObject>(resolved.AsObject());

            Assert.Equal("https://example.com/next", ReadEntryUrl(entry, context));
            Assert.Equal(1d, ReadEntryIndex(entry, context));

            var entries = navigation.Get("entries").AsFunction().Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.Equal(2, (int)entries.Get("length").ToNumber());

            var currentEntry = navigation.Get("currentEntry").AsFunction().Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.Equal("https://example.com/next", ReadEntryUrl(currentEntry, context));
        }

        [Fact]
        public void TraverseTo_InvalidKey_RejectsWithInvalidAccessError()
        {
            var context = CreateContext("https://example.com/start");
            var navigation = NavigationAPI.CreateNavigationObject(context, (_, __) => { });
            var traverseTo = navigation.Get("traverseTo").AsFunction();

            var reason = CaptureRejectedReason(traverseTo.Invoke(new[] { FenValue.FromString("missing-key") }, context));

            Assert.Contains("InvalidAccessError", reason, StringComparison.Ordinal);
        }

        [Fact]
        public void BackAndForward_UpdateCurrentEntryDeterministically()
        {
            var context = CreateContext("https://example.com/start");
            var navigation = NavigationAPI.CreateNavigationObject(context, (_, __) => { });
            var navigate = navigation.Get("navigate").AsFunction();

            CaptureResolvedValue(navigate.Invoke(new[] { FenValue.FromString("https://example.com/one") }, context));
            CaptureResolvedValue(navigate.Invoke(new[] { FenValue.FromString("https://example.com/two") }, context));

            var entriesBefore = navigation.Get("entries").AsFunction().Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.Equal(3, (int)entriesBefore.Get("length").ToNumber());

            var back = navigation.Get("back").AsFunction();
            CaptureResolvedValue(back.Invoke(Array.Empty<FenValue>(), context));

            var currentAfterBack = navigation.Get("currentEntry").AsFunction().Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.Equal("https://example.com/one", ReadEntryUrl(currentAfterBack, context));
            Assert.True(navigation.Get("canGoBack").AsFunction().Invoke(Array.Empty<FenValue>(), context).ToBoolean());
            Assert.True(navigation.Get("canGoForward").AsFunction().Invoke(Array.Empty<FenValue>(), context).ToBoolean());

            var forward = navigation.Get("forward").AsFunction();
            CaptureResolvedValue(forward.Invoke(Array.Empty<FenValue>(), context));

            var currentAfterForward = navigation.Get("currentEntry").AsFunction().Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.Equal("https://example.com/two", ReadEntryUrl(currentAfterForward, context));
        }

        [Fact]
        public void UpdateCurrentEntry_PersistsState()
        {
            var context = CreateContext("https://example.com/start");
            var navigation = NavigationAPI.CreateNavigationObject(context, (_, __) => { });
            var navigate = navigation.Get("navigate").AsFunction();
            CaptureResolvedValue(navigate.Invoke(new[] { FenValue.FromString("https://example.com/next") }, context));

            var statePayload = new FenObject();
            statePayload.Set("token", FenValue.FromString("abc123"));
            var options = new FenObject();
            options.Set("state", FenValue.FromObject(statePayload));

            navigation.Get("updateCurrentEntry").AsFunction().Invoke(new[] { FenValue.FromObject(options) }, context);

            var current = navigation.Get("currentEntry").AsFunction().Invoke(Array.Empty<FenValue>(), context).AsObject();
            var state = current.Get("getState").AsFunction().Invoke(Array.Empty<FenValue>(), context);
            Assert.True(state.IsObject);
            Assert.Equal("abc123", state.AsObject().Get("token").AsString());
        }

        [Fact]
        public void TraverseTo_ValidKey_ResolvesAndMovesToRequestedEntry()
        {
            var context = CreateContext("https://example.com/start");
            var navigation = NavigationAPI.CreateNavigationObject(context, (_, __) => { });
            var navigate = navigation.Get("navigate").AsFunction();

            CaptureResolvedValue(navigate.Invoke(new[] { FenValue.FromString("https://example.com/one") }, context));
            CaptureResolvedValue(navigate.Invoke(new[] { FenValue.FromString("https://example.com/two") }, context));

            var entries = navigation.Get("entries").AsFunction().Invoke(Array.Empty<FenValue>(), context).AsObject();
            var firstEntry = entries.Get("0").AsObject();
            var firstKey = ReadEntryKey(firstEntry, context);

            var traverseTo = navigation.Get("traverseTo").AsFunction();
            CaptureResolvedValue(traverseTo.Invoke(new[] { FenValue.FromString(firstKey) }, context));

            var current = navigation.Get("currentEntry").AsFunction().Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.Equal("https://example.com/start", ReadEntryUrl(current, context));
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
            var catchTarget = promise;
            var catchMethod = catchTarget.Get("catch");
            if (!catchMethod.IsFunction)
            {
                var committed = promise.Get("committed");
                Assert.True(committed.IsObject, "Expected a committed promise for rejected results.");
                catchTarget = committed.AsObject();
                catchMethod = catchTarget.Get("catch");
            }
            Assert.True(catchMethod.IsFunction, "Expected a catch() method.");

            string reason = null;
            catchMethod.AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("onRejected", (args, _) =>
                    {
                        reason = args.Length > 0 ? FormatPromiseValue(args[0]) : string.Empty;
                        return FenValue.Undefined;
                    }))
                },
                null,
                FenValue.FromObject(catchTarget));

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

        private static string ReadEntryUrl(FenBrowser.FenEngine.Core.Interfaces.IObject entry, FenBrowser.FenEngine.Core.ExecutionContext context)
        {
            var url = entry.Get("url");
            if (url.IsFunction)
            {
                return url.AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(entry)).ToString();
            }

            return url.ToString();
        }

        private static double ReadEntryIndex(FenBrowser.FenEngine.Core.Interfaces.IObject entry, FenBrowser.FenEngine.Core.ExecutionContext context)
        {
            var index = entry.Get("index");
            if (index.IsFunction)
            {
                return index.AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(entry)).ToNumber();
            }

            return index.ToNumber();
        }

        private static string ReadEntryKey(FenBrowser.FenEngine.Core.Interfaces.IObject entry, FenBrowser.FenEngine.Core.ExecutionContext context)
        {
            var key = entry.Get("key");
            if (key.IsFunction)
            {
                var value = key.AsFunction().Invoke(Array.Empty<FenValue>(), context);
                return value.ToString();
            }

            return key.ToString();
        }
    }
}

