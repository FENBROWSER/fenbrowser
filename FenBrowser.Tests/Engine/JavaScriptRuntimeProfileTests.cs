using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public sealed class JavaScriptRuntimeProfileTests : IDisposable
    {
        public JavaScriptRuntimeProfileTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
            LogManager.Initialize(true, LogCategory.All, LogLevel.Debug);
            LogManager.ClearLogs();
        }

        public void Dispose()
        {
            EventLoopCoordinator.Instance.Clear();
            EngineContext.Reset();
            LogManager.ClearLogs();
        }

        [Fact]
        public void LockedDownProfile_FreezesIntrinsics_And_DisablesDynamicCodeEvaluation()
        {
            var engine = new JavaScriptEngine(CreateHost(), JavaScriptRuntimeProfile.CreateLockedDown());

            Assert.False(engine.GlobalContext.Permissions.Check(JsPermissions.Eval));
            Assert.Equal(true, engine.Evaluate("Object.isFrozen(Object.prototype)"));
            Assert.Equal("EvalError", engine.Evaluate("try { eval('1+1'); } catch (e) { e.name; }")?.ToString());
            Assert.Equal("EvalError", engine.Evaluate("try { (new Function('return 1'))(); } catch (e) { e.name; }")?.ToString());
        }

        [Fact]
        public void Evaluate_EmitsStructuredExecutionLogEntries_WhenEnabled()
        {
            var engine = new JavaScriptEngine(
                CreateHost(),
                new JavaScriptRuntimeProfile
                {
                    Name = "test-profile",
                    EnableExecutionLogging = true,
                    EnableStructuredExecutionLogs = true,
                    WriteExecutionArtifacts = false
                });

            LogManager.ClearLogs();

            Assert.Equal(3.0, engine.Evaluate("1 + 2"));

            var entry = LogManager.GetRecentLogs()
                .LastOrDefault(log =>
                    log.Category == LogCategory.JsExecution &&
                    log.Message.Contains("[JS-EXEC]", StringComparison.Ordinal));

            Assert.NotNull(entry);
            Assert.NotNull(entry.Data);
            Assert.Equal("test-profile", entry.Data["profile"]?.ToString());
            Assert.Equal("eval", entry.Data["sourceKind"]?.ToString());
            Assert.Equal("success", entry.Data["outcome"]?.ToString());
            Assert.Equal("Number", entry.Data["resultType"]?.ToString());
            Assert.True(entry.DurationMs.HasValue);
        }

        [Fact]
        public async Task BalancedProfile_DoesNotDeferOversizedExternalPageScripts()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script src=\"/assets/large.js\"></script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (_, _) => Task.FromResult(BuildLargeExternalScript("globalThis.__largeScriptRan = true;"))
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__largeScriptRan"));
        }

        [Fact]
        public async Task LockedDownProfile_DefersOversizedExternalPageScripts()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script src=\"/assets/large.js\"></script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost(), JavaScriptRuntimeProfile.CreateLockedDown())
            {
                ExternalScriptFetcher = (_, _) => Task.FromResult(BuildLargeExternalScript("globalThis.__largeScriptRan = true;"))
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("undefined", engine.Evaluate("typeof globalThis.__largeScriptRan")?.ToString());
        }

        private static string BuildLargeExternalScript(string prefixStatement)
        {
            const int targetBytes = 300_000;
            var fillerLength = Math.Max(0, targetBytes - (prefixStatement?.Length ?? 0) - 6);
            return (prefixStatement ?? string.Empty) + "/*" + new string('x', fillerLength) + "*/";
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
