using System;
using System.Reflection;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;
using JsExecutionContext = FenBrowser.FenEngine.Core.ExecutionContext;

namespace FenBrowser.Tests.WebAPIs
{
    [Collection("Engine Tests")]
    public class AudioApiTests : IDisposable
    {
        public AudioApiTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        public void Dispose()
        {
            EventLoopCoordinator.Instance.Clear();
            EngineContext.Reset();
        }

        [Fact]
        public void JavaScriptEngine_Exposes_AudioContext_OnGlobalAndWindow()
        {
            var engine = new JavaScriptEngine(CreateHost());
            engine.Reset(new JsContext { BaseUri = new Uri("https://example.com/page") });

            var runtime = GetRuntime(engine);
            var window = Assert.IsAssignableFrom<FenObject>(runtime.GetGlobal("window").AsObject());

            Assert.True(runtime.GetGlobal("AudioContext").IsFunction);
            Assert.True(window.Get("AudioContext").IsFunction);
            Assert.True(runtime.GetGlobal("Audio").IsFunction);

            engine.Evaluate("""
                var __audioContextSurfaceOk =
                    (typeof AudioContext === 'function') &&
                    (typeof window.AudioContext === 'function') &&
                    (typeof (new AudioContext()).createOscillator === 'function') &&
                    (typeof (new AudioContext()).createGain === 'function');
                """);

            Assert.True(runtime.GetGlobal("__audioContextSurfaceOk").ToBoolean());
        }

        [Fact]
        public void AudioContext_CreatesCoreNodes_AndSupportsConnectDisconnect()
        {
            var context = new JsExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
            var ctor = Assert.IsType<FenFunction>(WebAudioApi.CreateAudioContextConstructor(context));

            var ctxObject = Assert.IsAssignableFrom<FenObject>(ctor.Invoke(Array.Empty<FenValue>(), context).AsObject());
            Assert.Equal("running", ctxObject.Get("state").AsString());
            Assert.True(ctxObject.Get("destination").IsObject);

            var createOscillator = Assert.IsType<FenFunction>(ctxObject.Get("createOscillator").AsFunction());
            var createGain = Assert.IsType<FenFunction>(ctxObject.Get("createGain").AsFunction());

            var oscillator = Assert.IsAssignableFrom<FenObject>(createOscillator.Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(ctxObject)).AsObject());
            var gain = Assert.IsAssignableFrom<FenObject>(createGain.Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(ctxObject)).AsObject());

            Assert.True(oscillator.Get("frequency").IsObject);
            Assert.True(oscillator.Get("detune").IsObject);
            Assert.True(gain.Get("gain").IsObject);

            var connectValue = oscillator.Get("connect");
            var disconnectValue = oscillator.Get("disconnect");
            if (connectValue.IsFunction && disconnectValue.IsFunction)
            {
                var connectResult = connectValue.AsFunction().Invoke(new[] { FenValue.FromObject(gain) }, context, FenValue.FromObject(oscillator));
                Assert.True(connectResult.IsObject);
                Assert.Same(gain, connectResult.AsObject());

                disconnectValue.AsFunction().Invoke(new[] { FenValue.FromObject(gain) }, context, FenValue.FromObject(oscillator));
            }
        }

        [Fact]
        public void AudioContext_LifecycleMethods_ResolveAndReject_AsExpected()
        {
            var context = new JsExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
            var ctor = Assert.IsType<FenFunction>(WebAudioApi.CreateAudioContextConstructor(context));
            var audioContext = Assert.IsAssignableFrom<FenObject>(ctor.Invoke(Array.Empty<FenValue>(), context).AsObject());

            var suspendResult = audioContext.Get("suspend").AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(audioContext));
            CaptureResolvedValue(suspendResult);
            Assert.Equal("suspended", audioContext.Get("state").AsString());

            var resumeResult = audioContext.Get("resume").AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(audioContext));
            CaptureResolvedValue(resumeResult);
            Assert.Equal("running", audioContext.Get("state").AsString());

            var closeResult = audioContext.Get("close").AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(audioContext));
            CaptureResolvedValue(closeResult);
            Assert.Equal("closed", audioContext.Get("state").AsString());

            var rejectedReason = CaptureRejectedReason(
                audioContext.Get("resume").AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(audioContext)));
            Assert.Contains("InvalidStateError", rejectedReason, StringComparison.Ordinal);
        }

        private static FenRuntime GetRuntime(JavaScriptEngine engine)
        {
            var runtimeField = typeof(JavaScriptEngine).GetField("_fenRuntime", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(runtimeField);

            var runtime = runtimeField.GetValue(engine) as FenRuntime;
            Assert.NotNull(runtime);
            return runtime;
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
                        reason = args.Length > 0 ? args[0].ToString() : string.Empty;
                        return FenValue.Undefined;
                    }))
                },
                null);

            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
            Assert.NotNull(reason);
            return reason;
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
