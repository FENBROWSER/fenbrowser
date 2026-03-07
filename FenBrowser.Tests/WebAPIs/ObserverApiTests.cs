using System;
using System.Reflection;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Observers;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    [Collection("Engine Tests")]
    public class ObserverApiTests : IDisposable
    {
        public ObserverApiTests()
        {
            ObserverCoordinator.Instance.Clear();
        }

        public void Dispose()
        {
            ObserverCoordinator.Instance.Clear();
        }

        [Fact]
        public void IntersectionObserverConstructor_IsConstructor_AndCreatesInstance()
        {
            var context = CreateTestContext();
            var constructorObject = IntersectionObserverAPI.CreateConstructor();
            var constructor = Assert.IsType<FenFunction>(constructorObject);

            Assert.True(constructor.IsConstructor);

            var options = new FenObject();
            var thresholds = new FenObject();
            thresholds.Set("0", FenValue.FromNumber(0));
            thresholds.Set("1", FenValue.FromNumber(0.5));
            thresholds.Set("length", FenValue.FromNumber(2));
            options.Set("threshold", FenValue.FromObject(thresholds));
            options.Set("rootMargin", FenValue.FromString("10px"));

            var instanceValue = constructor.Invoke(new[] { CreateCallback(), FenValue.FromObject(options) }, context);
            Assert.True(instanceValue.IsObject);

            var instance = instanceValue.AsObject();
            Assert.True(instance.Get("observe").IsFunction);
            Assert.True(instance.Get("unobserve").IsFunction);
            Assert.True(instance.Get("disconnect").IsFunction);
            Assert.True(instance.Get("takeRecords").IsFunction);
            Assert.Equal("10px", instance.Get("rootMargin").AsString());
            Assert.True(instance.Get("thresholds").IsObject);
        }

        [Fact]
        public void IntersectionObserverConstructor_RejectsOutOfRangeThreshold()
        {
            var context = CreateTestContext();
            var constructor = Assert.IsType<FenFunction>(IntersectionObserverAPI.CreateConstructor());

            var options = new FenObject();
            options.Set("threshold", FenValue.FromNumber(1.5));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                constructor.Invoke(new[] { CreateCallback(), FenValue.FromObject(options) }, context));

            Assert.Contains("threshold", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ResizeObserverConstructor_IsConstructor_AndCreatesInstance()
        {
            var context = CreateTestContext();
            var constructorObject = ResizeObserverAPI.CreateConstructor();
            var constructor = Assert.IsType<FenFunction>(constructorObject);

            Assert.True(constructor.IsConstructor);

            var instanceValue = constructor.Invoke(new[] { CreateCallback() }, context);
            Assert.True(instanceValue.IsObject);

            var instance = instanceValue.AsObject();
            Assert.True(instance.Get("observe").IsFunction);
            Assert.True(instance.Get("unobserve").IsFunction);
            Assert.True(instance.Get("disconnect").IsFunction);
        }

        [Fact]
        public void ResizeObserverConstructor_RequiresCallbackFunction()
        {
            var context = CreateTestContext();
            var constructor = Assert.IsType<FenFunction>(ResizeObserverAPI.CreateConstructor());

            var ex = Assert.Throws<InvalidOperationException>(() =>
                constructor.Invoke(Array.Empty<FenValue>(), context));

            Assert.Contains("callback", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void JavaScriptEngine_ExposesObserverConstructors_OnGlobalAndWindow()
        {
            var engine = new JavaScriptEngine(CreateHost());
            engine.Reset(new JsContext { BaseUri = new Uri("https://example.com/page") });

            var runtimeField = typeof(JavaScriptEngine).GetField("_fenRuntime", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(runtimeField);

            var runtime = runtimeField.GetValue(engine) as FenRuntime;
            Assert.NotNull(runtime);

            var intersectionObserverCtor = runtime.GetGlobal("IntersectionObserver");
            var resizeObserverCtor = runtime.GetGlobal("ResizeObserver");
            Assert.True(intersectionObserverCtor.IsFunction);
            Assert.True(resizeObserverCtor.IsFunction);

            var window = runtime.GetGlobal("window");
            Assert.True(window.IsObject);
            Assert.True(window.AsObject().Get("IntersectionObserver").IsFunction);
            Assert.True(window.AsObject().Get("ResizeObserver").IsFunction);

            engine.Evaluate("var __observerCtorOk = (typeof IntersectionObserver === 'function') && (typeof ResizeObserver === 'function') && (typeof window.IntersectionObserver === 'function') && (typeof window.ResizeObserver === 'function');");
            Assert.True(runtime.GetGlobal("__observerCtorOk").ToBoolean());
        }

        private static FenBrowser.FenEngine.Core.ExecutionContext CreateTestContext()
        {
            return new FenBrowser.FenEngine.Core.ExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
        }

        private static FenValue CreateCallback()
        {
            return FenValue.FromFunction(new FenFunction("observerCallback", (args, thisVal) => FenValue.Undefined));
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
