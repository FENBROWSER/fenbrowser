using System;
using System.Linq;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JsRuntimeAbstractionTests
    {
        [Fact]
        public void IJsRuntime_HasSingleConcreteImplementation()
        {
            var implementations = typeof(IJsRuntime)
                .Assembly
                .GetTypes()
                .Where(type => typeof(IJsRuntime).IsAssignableFrom(type) && type.IsClass && !type.IsAbstract)
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { typeof(JsZeroRuntime).FullName }, implementations);
        }

        [Fact]
        public void JsZeroRuntime_DelegatesToAuthoritativeJavaScriptEngine()
        {
            var context = new JsContext { BaseUri = new Uri("https://example.com/app") };
            var engine = new JavaScriptEngine(CreateHost());
            var runtime = new JsZeroRuntime(engine);

            runtime.Reset(context);

            Assert.True(runtime.RunInline("globalThis.__runtimeBridgeValue = 40 + 2;", context));
            Assert.Equal("42", runtime.EvaluateExpression("globalThis.__runtimeBridgeValue", context));
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
