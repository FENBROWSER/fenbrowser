using System;
using System.Reflection;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    [Collection("Engine Tests")]
    public class WebRtcApiTests
    {
        [Fact]
        public void JavaScriptEngine_DoesNotExpose_WebRtc_Simulation_Constructors()
        {
            var engine = new JavaScriptEngine(CreateHost());
            engine.Reset(new JsContext { BaseUri = new Uri("https://example.com/page") });

            var runtime = GetRuntime(engine);
            var window = Assert.IsAssignableFrom<FenObject>(runtime.GetGlobal("window").AsObject());

            Assert.True(runtime.GetGlobal("RTCPeerConnection").IsUndefined);
            Assert.True(runtime.GetGlobal("webkitRTCPeerConnection").IsUndefined);
            Assert.True(runtime.GetGlobal("MediaStream").IsUndefined);

            Assert.True(window.Get("RTCPeerConnection").IsUndefined);
            Assert.True(window.Get("webkitRTCPeerConnection").IsUndefined);
            Assert.True(window.Get("MediaStream").IsUndefined);

            engine.Evaluate("""
                var __webrtcSurfaceAbsent =
                    (typeof RTCPeerConnection === 'undefined') &&
                    (typeof webkitRTCPeerConnection === 'undefined') &&
                    (typeof MediaStream === 'undefined') &&
                    (typeof window.RTCPeerConnection === 'undefined') &&
                    (typeof window.webkitRTCPeerConnection === 'undefined') &&
                    (typeof window.MediaStream === 'undefined');
                """);

            Assert.True(runtime.GetGlobal("__webrtcSurfaceAbsent").ToBoolean());
        }

        [Fact]
        public void JavaScriptEngine_Source_DoesNotRegister_WebRtc_Simulation_Surface()
        {
            var source = System.IO.File.ReadAllText(GetJavaScriptEngineSourcePath());

            Assert.DoesNotContain("WebRTCAPI", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SetGlobal(\"RTCPeerConnection\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SetGlobal(\"webkitRTCPeerConnection\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SetGlobal(\"MediaStream\"", source, StringComparison.Ordinal);
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
