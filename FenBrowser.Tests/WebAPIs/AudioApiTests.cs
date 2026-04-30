using System;
using System.Reflection;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Scripting;
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
        public void JavaScriptEngine_DoesNotExpose_WebAudioSimulation_Constructors_ButKeepsHtmlAudio()
        {
            var engine = new JavaScriptEngine(CreateHost());
            engine.Reset(new JsContext { BaseUri = new Uri("https://example.com/page") });

            var runtime = GetRuntime(engine);
            var window = Assert.IsAssignableFrom<FenObject>(runtime.GetGlobal("window").AsObject());

            Assert.False(runtime.GetGlobal("Audio").IsUndefined);
            Assert.True(runtime.GetGlobal("AudioContext").IsUndefined);
            Assert.True(runtime.GetGlobal("webkitAudioContext").IsUndefined);

            Assert.False(window.Get("Audio").IsUndefined);
            Assert.True(window.Get("AudioContext").IsUndefined);
            Assert.True(window.Get("webkitAudioContext").IsUndefined);

            engine.Evaluate("""
                var __audioSurfaceShapeValid =
                    (typeof Audio === 'function') &&
                    (typeof AudioContext === 'undefined') &&
                    (typeof webkitAudioContext === 'undefined') &&
                    (typeof window.Audio === 'function') &&
                    (typeof window.AudioContext === 'undefined') &&
                    (typeof window.webkitAudioContext === 'undefined');
                """);

            Assert.True(runtime.GetGlobal("__audioSurfaceShapeValid").ToBoolean());
        }

        [Fact]
        public void JavaScriptEngine_Source_DoesNotRegister_WebAudio_Simulation_Surface()
        {
            var source = System.IO.File.ReadAllText(GetJavaScriptEngineSourcePath());

            Assert.DoesNotContain("WebAudioAPI", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SetGlobal(\"AudioContext\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SetGlobal(\"webkitAudioContext\"", source, StringComparison.Ordinal);
        }

        private static FenRuntime GetRuntime(JavaScriptEngine engine)
        {
            var runtimeField = typeof(JavaScriptEngine).GetField("_fenRuntime", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(runtimeField);

            var runtime = runtimeField.GetValue(engine) as FenRuntime;
            Assert.NotNull(runtime);
            return runtime;
        }

        private static string GetJavaScriptEngineSourcePath()
        {
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "FenBrowser.FenEngine",
                "Scripting",
                "JavaScriptEngine.cs"));
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
