using System;
using System.Reflection;
using FenBrowser.Core.Engine;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Security;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JavaScriptEngineGeolocationTests
    {
        public JavaScriptEngineGeolocationTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        [Fact]
        public void WatchPosition_ReturnsDistinctIds()
        {
            var engine = CreateEngine(granted: true);
            var geolocation = GetGeolocation(engine);
            var watchPosition = geolocation.Get("watchPosition").AsFunction();
            var clearWatch = geolocation.Get("clearWatch").AsFunction();
            var callback = FenValue.FromFunction(new FenFunction("success", (args, _) => FenValue.Undefined));

            var id1 = (int)watchPosition.Invoke(new[] { callback }, null).ToNumber();
            var id2 = (int)watchPosition.Invoke(new[] { callback }, null).ToNumber();

            try
            {
                Assert.True(id1 > 0);
                Assert.True(id2 > 0);
                Assert.NotEqual(id1, id2);
            }
            finally
            {
                clearWatch.Invoke(new[] { FenValue.FromNumber(id1) }, null);
                clearWatch.Invoke(new[] { FenValue.FromNumber(id2) }, null);
            }
        }

        [Fact]
        public void WatchPosition_SchedulesCallbacksUntilCleared()
        {
            var engine = CreateEngine(granted: true);
            var geolocation = GetGeolocation(engine);
            var watchPosition = geolocation.Get("watchPosition").AsFunction();
            var clearWatch = geolocation.Get("clearWatch").AsFunction();
            var callbackCount = 0;
            using var callbackEvent = new ManualResetEventSlim(false);

            var callback = FenValue.FromFunction(new FenFunction("success", (args, _) =>
            {
                if (Interlocked.Increment(ref callbackCount) >= 2)
                {
                    callbackEvent.Set();
                }
                return FenValue.Undefined;
            }));

            var options = new FenObject();
            options.Set("timeout", FenValue.FromNumber(250));
            var watchId = (int)watchPosition.Invoke(new[] { callback, FenValue.Undefined, FenValue.FromObject(options) }, null).ToNumber();

            try
            {
                PumpEventLoopUntil(() => callbackEvent.IsSet, TimeSpan.FromSeconds(2));
                var callbacksBeforeClear = Volatile.Read(ref callbackCount);

                clearWatch.Invoke(new[] { FenValue.FromNumber(watchId) }, null);
                Thread.Sleep(350);
                PumpEventLoopUntil(() => false, TimeSpan.FromMilliseconds(150));

                Assert.True(callbacksBeforeClear >= 2);
                Assert.Equal(callbacksBeforeClear, Volatile.Read(ref callbackCount));
            }
            finally
            {
                clearWatch.Invoke(new[] { FenValue.FromNumber(watchId) }, null);
            }
        }

        [Fact]
        public void Reset_ClearsActiveGeolocationWatches()
        {
            var engine = CreateEngine(granted: true);
            var geolocation = GetGeolocation(engine);
            var watchPosition = geolocation.Get("watchPosition").AsFunction();
            var callbackCount = 0;
            using var firstCallback = new ManualResetEventSlim(false);

            var callback = FenValue.FromFunction(new FenFunction("success", (args, _) =>
            {
                if (Interlocked.Increment(ref callbackCount) >= 1)
                {
                    firstCallback.Set();
                }
                return FenValue.Undefined;
            }));

            var options = new FenObject();
            options.Set("timeout", FenValue.FromNumber(250));
            watchPosition.Invoke(new[] { callback, FenValue.Undefined, FenValue.FromObject(options) }, null);

            PumpEventLoopUntil(() => firstCallback.IsSet, TimeSpan.FromSeconds(2));
            var callbacksBeforeReset = Volatile.Read(ref callbackCount);

            engine.Reset(new JsContext { BaseUri = new Uri("https://example.com/after-reset") });
            Thread.Sleep(350);
            PumpEventLoopUntil(() => false, TimeSpan.FromMilliseconds(150));

            Assert.Equal(callbacksBeforeReset, Volatile.Read(ref callbackCount));
        }

        private static JavaScriptEngine CreateEngine(bool granted)
        {
            var engine = new JavaScriptEngine(CreateHost());
            engine.PermissionRequested += (_, permission) =>
            {
                if (permission == JsPermissions.Geolocation)
                {
                    return Task.FromResult(granted);
                }

                return Task.FromResult(false);
            };
            engine.Reset(new JsContext { BaseUri = new Uri("https://example.com/page") });
            return engine;
        }

        private static FenObject GetGeolocation(JavaScriptEngine engine)
        {
            var runtimeField = typeof(JavaScriptEngine).GetField("_fenRuntime", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(runtimeField);

            var runtime = runtimeField!.GetValue(engine) as FenRuntime;
            Assert.NotNull(runtime);

            var navigator = runtime!.GetGlobal("navigator");
            Assert.True(navigator.IsObject);

            var geolocation = navigator.AsObject()?.Get("geolocation");
            Assert.True(geolocation.HasValue && geolocation.Value.IsObject);
            return (FenObject)geolocation.Value.AsObject()!;
        }

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }

        private static void PumpEventLoopUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                EventLoopCoordinator.Instance.ProcessNextTask();
                if (condition())
                {
                    return;
                }
                Thread.Sleep(15);
            }
        }
    }
}
