using System;
using System.Reflection;
using FenBrowser.Core;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

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
        public void AudioConstructor_IsConstructor_AndCreatesInstance()
        {
            var context = CreateTestContext();
            var constructor = WebAudioAPI.CreateAudioConstructor(context);

            Assert.True(constructor.IsConstructor);

            var instance = constructor.Invoke(new[] { FenValue.FromString("https://example.com/media.mp3") }, context);
            Assert.True(instance.IsObject);

            var audio = instance.AsObject();
            Assert.True(audio.Get("play").IsFunction);
            Assert.True(audio.Get("pause").IsFunction);
            Assert.True(audio.Get("canPlayType").IsFunction);
            Assert.Equal("https://example.com/media.mp3", audio.Get("src").AsString());
        }

        [Fact]
        public void Audio_CanPlayType_ReturnsExpectedMatrix()
        {
            var context = CreateTestContext();
            var audio = WebAudioAPI.CreateAudio(context);
            var canPlayType = audio.Get("canPlayType").AsFunction();

            var probably = canPlayType.Invoke(new[] { FenValue.FromString("audio/mpeg") }, context).AsString();
            var maybe = canPlayType.Invoke(new[] { FenValue.FromString("audio/ogg; codecs=vorbis") }, context).AsString();
            var notSupported = canPlayType.Invoke(new[] { FenValue.FromString("video/mp4") }, context).AsString();

            Assert.Equal("probably", probably);
            Assert.Equal("maybe", maybe);
            Assert.Equal(string.Empty, notSupported);
        }

        [Fact]
        public void Audio_Play_RejectsInvalidScheme()
        {
            var context = CreateTestContext();
            var audio = WebAudioAPI.CreateAudio(context, "file:///etc/passwd");

            var playResult = audio.Get("play").AsFunction().Invoke(Array.Empty<FenValue>(), context);
            Assert.True(playResult.IsObject);

            var rejected = false;
            var rejectionMessage = string.Empty;

            var onFulfilled = FenValue.FromFunction(new FenFunction("onFulfilled", (args, thisVal) =>
            {
                return FenValue.Undefined;
            }));
            var onRejected = FenValue.FromFunction(new FenFunction("onRejected", (args, thisVal) =>
            {
                rejected = true;
                if (args.Length > 0 && args[0].IsObject)
                {
                    rejectionMessage = args[0].AsObject()?.Get("message").AsString() ?? string.Empty;
                }
                else if (args.Length > 0)
                {
                    rejectionMessage = args[0].AsString();
                }

                return FenValue.Undefined;
            }));

            playResult.AsObject().Get("then").AsFunction().Invoke(new[] { onFulfilled, onRejected }, context);
            DrainEventLoop();

            Assert.True(rejected);
            Assert.Contains("not allowed", rejectionMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void JavaScriptEngine_ExposesAudio_OnGlobalAndWindow()
        {
            var engine = new JavaScriptEngine(CreateHost());
            engine.Reset(new JsContext { BaseUri = new Uri("https://example.com/page") });

            var runtimeField = typeof(JavaScriptEngine).GetField("_fenRuntime", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(runtimeField);

            var runtime = runtimeField.GetValue(engine) as FenRuntime;
            Assert.NotNull(runtime);

            var audioCtor = runtime.GetGlobal("Audio");
            Assert.True(audioCtor.IsFunction);

            var window = runtime.GetGlobal("window");
            Assert.True(window.IsObject);
            Assert.True(window.AsObject().Get("Audio").IsFunction);

            engine.Evaluate("var __audioCtorOk = (typeof Audio === 'function') && (typeof window.Audio === 'function');");
            Assert.True(runtime.GetGlobal("__audioCtorOk").ToBoolean());
        }

        private static FenBrowser.FenEngine.Core.ExecutionContext CreateTestContext()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(new PermissionManager(JsPermissions.StandardWeb));
            context.ScheduleCallback = (action, delay) =>
            {
                if (action == null)
                {
                    return;
                }

                EventLoopCoordinator.Instance.ScheduleTask(action, TaskSource.Timer, "AudioApiTests.ScheduleCallback");
            };

            context.ScheduleMicrotask = action =>
            {
                if (action == null)
                {
                    return;
                }

                EventLoopCoordinator.Instance.ScheduleMicrotask(action);
            };

            return context;
        }

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }

        private static void DrainEventLoop()
        {
            for (var i = 0; i < 64; i++)
            {
                EventLoopCoordinator.Instance.ProcessNextTask();
                EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();

                if (EventLoopCoordinator.Instance.TaskCount == 0 && EventLoopCoordinator.Instance.MicrotaskCount == 0)
                {
                    break;
                }
            }
        }
    }
}

