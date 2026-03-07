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
    public class WebRtcApiTests
    {
        [Fact]
        public void RTCPeerConnectionConstructor_IsConstructor_AndCreatesInstance()
        {
            var context = CreateTestContext();
            var constructorObject = WebRTCAPI.CreateRTCPeerConnectionConstructor(context);

            var constructor = Assert.IsType<FenFunction>(constructorObject);
            Assert.True(constructor.IsConstructor);

            var instance = constructor.Invoke(Array.Empty<FenValue>(), context);
            Assert.True(instance.IsObject);

            var pc = instance.AsObject();
            Assert.True(pc.Get("createOffer").IsFunction);
            Assert.True(pc.Get("createAnswer").IsFunction);
            Assert.True(pc.Get("addEventListener").IsFunction);
            Assert.True(pc.Get("getConfiguration").IsFunction);
        }

        [Fact]
        public void RTCPeerConnectionConstructor_RejectsUnsupportedIceScheme()
        {
            var context = CreateTestContext();
            var constructor = Assert.IsType<FenFunction>(WebRTCAPI.CreateRTCPeerConnectionConstructor(context));

            var config = new FenObject();
            var iceServers = new FenObject();
            var server = new FenObject();
            server.Set("urls", FenValue.FromString("http://example.com/stun"));
            iceServers.Set("0", FenValue.FromObject(server));
            iceServers.Set("length", FenValue.FromNumber(1));
            config.Set("iceServers", FenValue.FromObject(iceServers));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                constructor.Invoke(new[] { FenValue.FromObject(config) }, context));

            Assert.Contains("ICE URL scheme", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MediaStreamConstructor_IsConstructor_AndCreatesStream()
        {
            var constructorObject = WebRTCAPI.CreateMediaStreamConstructor();
            var constructor = Assert.IsType<FenFunction>(constructorObject);

            Assert.True(constructor.IsConstructor);

            var streamValue = constructor.Invoke(Array.Empty<FenValue>(), (IExecutionContext)null);
            Assert.True(streamValue.IsObject);

            var stream = streamValue.AsObject();
            Assert.True(stream.Get("getTracks").IsFunction);
            Assert.True(stream.Get("clone").IsFunction);
            Assert.True(stream.Get("active").IsBoolean);
        }

        [Fact]
        public void JavaScriptEngine_ExposesWebRtcConstructors_OnGlobalAndWindow()
        {
            var engine = new JavaScriptEngine(CreateHost());
            engine.Reset(new JsContext { BaseUri = new Uri("https://example.com/page") });

            var runtimeField = typeof(JavaScriptEngine).GetField("_fenRuntime", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(runtimeField);

            var runtime = runtimeField.GetValue(engine) as FenRuntime;
            Assert.NotNull(runtime);

            var rtcCtor = runtime.GetGlobal("RTCPeerConnection");
            var mediaStreamCtor = runtime.GetGlobal("MediaStream");
            Assert.True(rtcCtor.IsFunction);
            Assert.True(mediaStreamCtor.IsFunction);

            var window = runtime.GetGlobal("window");
            Assert.True(window.IsObject);
            Assert.True(window.AsObject().Get("RTCPeerConnection").IsFunction);
            Assert.True(window.AsObject().Get("webkitRTCPeerConnection").IsFunction);
            Assert.True(window.AsObject().Get("MediaStream").IsFunction);

            engine.Evaluate("var __webrtcCtorOk = (typeof RTCPeerConnection === 'function') && (typeof MediaStream === 'function') && (typeof window.RTCPeerConnection === 'function');");
            Assert.True(runtime.GetGlobal("__webrtcCtorOk").ToBoolean());
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