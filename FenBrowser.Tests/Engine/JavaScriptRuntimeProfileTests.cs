using System;
using System.Linq;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core.EventLoop;
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
