using System;
using System.Reflection;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    [Collection("Engine Tests")]
    public class NotificationsApiTests : IDisposable
    {
        public NotificationsApiTests()
        {
            NotificationsAPI.SetPermission("default");
        }

        public void Dispose()
        {
            NotificationsAPI.SetPermission("default");
        }

        [Fact]
        public void NotificationConstructor_IsFunctionAndConstructor()
        {
            NotificationsAPI.SetPermission("granted");
            var context = CreateTestContext();
            var constructorObject = NotificationsAPI.CreateNotificationConstructor();

            var constructor = Assert.IsType<FenFunction>(constructorObject);
            Assert.True(constructor.IsConstructor);

            var instance = constructor.Invoke(new[] { FenValue.FromString("Build Ready") }, context);
            Assert.True(instance.IsObject);

            var notification = instance.AsObject();
            Assert.Equal("Build Ready", notification.Get("title").AsString());
            Assert.True(notification.Get("close").IsFunction);
        }

        [Fact]
        public void NotificationConstructor_DeniedPermission_Throws()
        {
            NotificationsAPI.SetPermission("denied");
            var context = CreateTestContext();
            var constructor = Assert.IsType<FenFunction>(NotificationsAPI.CreateNotificationConstructor());

            var ex = Assert.Throws<InvalidOperationException>(() =>
                constructor.Invoke(new[] { FenValue.FromString("blocked") }, context));

            Assert.Contains("permission", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Notification_RequestPermission_InvokesCallback_AndReturnsThenable()
        {
            NotificationsAPI.SetPermission("default");
            var constructor = NotificationsAPI.CreateNotificationConstructor();
            var requestPermission = constructor.Get("requestPermission").AsFunction();

            var callbackCalled = false;
            string callbackValue = null;
            string thenValue = null;

            var callback = FenValue.FromFunction(new FenFunction("legacyCallback", (args, thisVal) =>
            {
                callbackCalled = true;
                callbackValue = args.Length > 0 ? args[0].AsString() : null;
                return FenValue.Undefined;
            }));

            var promiseLike = requestPermission.Invoke(new[] { callback }, (IExecutionContext)null);
            Assert.True(promiseLike.IsObject);
            Assert.True(promiseLike.AsObject().Get("then").IsFunction);

            promiseLike.AsObject().Get("then").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("onFulfilled", (args, thisVal) =>
                    {
                        thenValue = args.Length > 0 ? args[0].AsString() : null;
                        return FenValue.Undefined;
                    }))
                },
                (IExecutionContext)null);

            Assert.True(callbackCalled);
            Assert.Equal("denied", callbackValue);
            Assert.Equal("denied", thenValue);
        }

        [Fact]
        public void JavaScriptEngine_ExposesNotificationConstructor_OnGlobalAndWindow()
        {
            NotificationsAPI.SetPermission("granted");
            var engine = new JavaScriptEngine(CreateHost());
            engine.Reset(new JsContext { BaseUri = new Uri("https://example.com/page") });

            var runtimeField = typeof(JavaScriptEngine).GetField("_fenRuntime", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(runtimeField);

            var runtime = runtimeField.GetValue(engine) as FenRuntime;
            Assert.NotNull(runtime);

            var notificationCtor = runtime.GetGlobal("Notification");
            Assert.True(notificationCtor.IsFunction);

            var window = runtime.GetGlobal("window");
            Assert.True(window.IsObject);
            Assert.True(window.AsObject().Get("Notification").IsFunction);

            engine.Evaluate("var __notificationCtorOk = (typeof Notification === 'function') && (typeof window.Notification === 'function');");
            Assert.True(runtime.GetGlobal("__notificationCtorOk").ToBoolean());
        }

        private static FenBrowser.FenEngine.Core.ExecutionContext CreateTestContext()
        {
            return new FenBrowser.FenEngine.Core.ExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
        }

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }
    }
}
