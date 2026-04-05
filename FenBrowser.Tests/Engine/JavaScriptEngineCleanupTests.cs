using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JavaScriptEngineCleanupTests
    {
        [Fact]
        public void JavaScriptEngine_HasNoDeadPhase123BuiltinsHandler()
        {
            var method = typeof(FenBrowser.FenEngine.Scripting.JavaScriptEngine)
                .GetMethod("HandlePhase123Builtins", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.Null(method);
        }

        [Fact]
        public void JavaScriptEngine_HasNoUnusedMiniPrattModeFlag()
        {
            var property = typeof(FenBrowser.FenEngine.Scripting.JavaScriptEngine)
                .GetProperty("UseMiniPrattEngine", BindingFlags.Instance | BindingFlags.Public);

            Assert.Null(property);
        }

        [Fact]
        public void LegacyJavaScriptRuntimeWrapper_IsDeleted()
        {
            var legacyType = typeof(FenBrowser.FenEngine.Scripting.JavaScriptEngine)
                .Assembly
                .GetType("FenBrowser.FenEngine.Scripting.JavaScriptRuntime");

            Assert.Null(legacyType);
        }

        [Fact]
        public void ApproximateHostSurfaces_AreExplicitlyClassified()
        {
            var catalogType = typeof(FenBrowser.FenEngine.Scripting.JavaScriptEngine)
                .Assembly
                .GetType("FenBrowser.FenEngine.Compatibility.HostApiSurfaceCatalog");

            Assert.NotNull(catalogType);

            var getEntries = catalogType.GetMethod("GetEntries", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(getEntries);

            var entries = ((System.Collections.IEnumerable)getEntries.Invoke(null, null))
                .Cast<object>()
                .Select(entry => entry.ToString())
                .ToArray();

            Assert.Contains(entries, value => value.Contains("navigator.userAgentData [ProductionImplementation]", StringComparison.Ordinal));
            Assert.Contains(entries, value => value.Contains("crypto.subtle", StringComparison.Ordinal));
            Assert.Contains(entries, value => value.Contains("window.open", StringComparison.Ordinal));
            Assert.Contains(entries, value => value.Contains("window.matchMedia", StringComparison.Ordinal));
            Assert.Contains(entries, value => value.Contains("window.requestIdleCallback", StringComparison.Ordinal));
            Assert.Contains(entries, value => value.Contains("Intl", StringComparison.Ordinal));
        }
    }
}
