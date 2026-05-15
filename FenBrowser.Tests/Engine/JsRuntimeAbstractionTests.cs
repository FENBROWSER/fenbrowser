using System;
using System.Linq;
using System.Reflection;
using FenBrowser.FenEngine.Core;
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

        [Fact]
        public void LegacyJitSurface_IsQuarantinedAndNotRuntimeWired()
        {
            var assembly = typeof(FenRuntime).Assembly;
            var jitTypes = new[]
            {
                "FenBrowser.FenEngine.Jit.BytecodeCompiler",
                "FenBrowser.FenEngine.Jit.BytecodeUnit",
                "FenBrowser.FenEngine.Jit.FenJittedDelegate",
                "FenBrowser.FenEngine.Jit.Instruction",
                "FenBrowser.FenEngine.Jit.JitCompiler",
                "FenBrowser.FenEngine.Jit.JitRuntime",
                "FenBrowser.FenEngine.Jit.OpCode"
            };

            foreach (var typeName in jitTypes)
            {
                var type = assembly.GetType(typeName, throwOnError: true);
                var obsolete = type.GetCustomAttribute<ObsoleteAttribute>();

                Assert.NotNull(obsolete);
                Assert.Contains("quarantined", obsolete.Message, StringComparison.OrdinalIgnoreCase);
            }

            var runtimeType = typeof(FenRuntime);
            var runtimeMemberTypes = runtimeType
                .GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(GetReferencedTypes)
                .Where(type => type?.Namespace == "FenBrowser.FenEngine.Jit")
                .Select(type => type.FullName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Empty(runtimeMemberTypes);
        }

        private static Type[] GetReferencedTypes(MemberInfo member)
        {
            switch (member)
            {
                case FieldInfo field:
                    return new[] { field.FieldType };
                case PropertyInfo property:
                    return new[] { property.PropertyType };
                case MethodInfo method:
                    return method.GetParameters()
                        .Select(parameter => parameter.ParameterType)
                        .Concat(new[] { method.ReturnType })
                        .ToArray();
                default:
                    return Array.Empty<Type>();
            }
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
