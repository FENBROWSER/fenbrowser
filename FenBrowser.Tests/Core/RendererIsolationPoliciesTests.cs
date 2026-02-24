using FenBrowser.Core.ProcessIsolation;
using System.Threading;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class RendererIsolationPoliciesTests
    {
        [Theory]
        [InlineData("https://example.com/path?a=1", "https://example.com:443")]
        [InlineData("http://example.com:8080/path", "http://example.com:8080")]
        [InlineData("HTTPS://Sub.Example.com", "https://sub.example.com:443")]
        public void OriginIsolationPolicy_Derives_StableAssignmentKey(string url, string expected)
        {
            var ok = OriginIsolationPolicy.TryGetAssignmentKey(url, out var key);
            Assert.True(ok);
            Assert.Equal(expected, key);
        }

        [Fact]
        public void OriginIsolationPolicy_RequiresReassignment_WhenOriginChanges()
        {
            Assert.True(OriginIsolationPolicy.RequiresReassignment(
                "https://example.com:443",
                "https://other.example.com/page"));
        }

        [Fact]
        public void OriginIsolationPolicy_DoesNotRequireReassignment_ForSameOrigin()
        {
            Assert.False(OriginIsolationPolicy.RequiresReassignment(
                "https://example.com:443",
                "https://example.com/next"));
        }

        [Fact]
        public void OriginIsolationPolicy_RejectsRelativeUrls()
        {
            Assert.False(OriginIsolationPolicy.TryGetAssignmentKey("/relative", out _));
        }

        [Theory]
        [InlineData("about:blank", "about://blank")]
        [InlineData("about:config?x=1", "about://config")]
        [InlineData("file:///C:/tmp/a.html", "file://local")]
        [InlineData("data:text/plain,hello", "opaque://data")]
        [InlineData("blob:https://example.com/id", "opaque://blob")]
        public void OriginIsolationPolicy_AssignsOpaqueAndLocalSchemes(string url, string expected)
        {
            var ok = OriginIsolationPolicy.TryGetAssignmentKey(url, out var key);
            Assert.True(ok);
            Assert.Equal(expected, key);
        }

        [Fact]
        public void OriginIsolationPolicy_RequiresReassignment_OnNetworkToOpaqueTransition()
        {
            Assert.True(OriginIsolationPolicy.RequiresReassignment(
                "https://example.com:443",
                "about:blank"));
        }

        [Fact]
        public void RendererRestartPolicy_AppliesExponentialBackoffWithCap()
        {
            var policy = new RendererRestartPolicy(maxRestartAttempts: 5, baseBackoffMs: 200, maxBackoffMs: 1200);

            Assert.Equal(200, policy.GetBackoffDelayMs(0));
            Assert.Equal(400, policy.GetBackoffDelayMs(1));
            Assert.Equal(800, policy.GetBackoffDelayMs(2));
            Assert.Equal(1200, policy.GetBackoffDelayMs(3));
            Assert.Equal(1200, policy.GetBackoffDelayMs(10));
        }

        [Fact]
        public void RendererRestartPolicy_EnforcesAttemptLimit()
        {
            var policy = new RendererRestartPolicy(maxRestartAttempts: 2, baseBackoffMs: 100, maxBackoffMs: 1000);

            Assert.True(policy.CanRestart(0));
            Assert.True(policy.CanRestart(1));
            Assert.False(policy.CanRestart(2));
            Assert.False(policy.CanRestart(3));
        }

        [Fact]
        public void RendererRestartPolicy_ResetsAttemptsAfterStableRuntime()
        {
            var policy = new RendererRestartPolicy(
                maxRestartAttempts: 2,
                baseBackoffMs: 100,
                maxBackoffMs: 1000,
                stableSessionResetMs: 500);

            Assert.False(policy.ShouldResetAttempts(499));
            Assert.True(policy.ShouldResetAttempts(500));
            Assert.True(policy.ShouldResetAttempts(5000));
        }

        [Fact]
        public void RendererRestartPolicy_QuarantinesOnlyAfterThresholdExceeded()
        {
            var policy = new RendererRestartPolicy(
                maxRestartAttempts: 2,
                baseBackoffMs: 100,
                maxBackoffMs: 1000,
                stableSessionResetMs: 1000,
                crashWindowMs: 10000,
                maxCrashCountInWindow: 2,
                quarantineMs: 5000);

            Assert.False(policy.ShouldQuarantine(1));
            Assert.False(policy.ShouldQuarantine(2));
            Assert.True(policy.ShouldQuarantine(3));
        }

        [Fact]
        public void RendererTabIsolationRegistry_AssignsOriginKeyOnFirstNavigation()
        {
            var registry = new RendererTabIsolationRegistry();

            var decision = registry.ApplyNavigation(1, "https://example.com/page", true);

            Assert.True(decision.HasValidAssignment);
            Assert.False(decision.RequiresReassignment);
            Assert.Equal("https://example.com:443", decision.RequestedAssignmentKey);
            Assert.True(registry.TryGetSnapshot(1, out var snapshot));
            Assert.Equal("https://example.com:443", snapshot.AssignmentKey);
            Assert.Equal("https://example.com/page", snapshot.LastNavigationUrl);
            Assert.True(snapshot.LastNavigationIsUserInput);
        }

        [Fact]
        public void RendererTabIsolationRegistry_FlagsReassignmentOnCrossOriginNavigation()
        {
            var registry = new RendererTabIsolationRegistry();
            registry.ApplyNavigation(2, "https://a.example/path", true);

            var decision = registry.ApplyNavigation(2, "https://b.example/home", false);

            Assert.True(decision.HasValidAssignment);
            Assert.True(decision.RequiresReassignment);
            Assert.Equal("https://a.example:443", decision.PreviousAssignmentKey);
            Assert.Equal("https://b.example:443", decision.RequestedAssignmentKey);
            Assert.True(registry.TryGetSnapshot(2, out var snapshot));
            Assert.Equal("https://b.example:443", snapshot.AssignmentKey);
        }

        [Fact]
        public void RendererTabIsolationRegistry_DoesNotReassignForSameOriginNavigation()
        {
            var registry = new RendererTabIsolationRegistry();
            registry.ApplyNavigation(3, "https://same.example/one", true);

            var decision = registry.ApplyNavigation(3, "https://same.example/two", false);

            Assert.True(decision.HasValidAssignment);
            Assert.False(decision.RequiresReassignment);
            Assert.Equal("https://same.example:443", decision.RequestedAssignmentKey);
        }

        [Fact]
        public void RendererTabIsolationRegistry_ExpectedExit_DoesNotRestart()
        {
            var registry = new RendererTabIsolationRegistry();
            registry.ApplyNavigation(4, "https://app.example/start", true);
            registry.MarkSessionStarted(4, 1001);
            registry.MarkExpectedExit(4, 1001);

            var decision = registry.HandleSessionExit(4, 1001, isShuttingDown: false);

            Assert.True(decision.IsExpectedExit);
            Assert.False(decision.ShouldRestart);
            Assert.Equal("expected-exit", decision.Reason);
        }

        [Fact]
        public void RendererTabIsolationRegistry_UnexpectedExit_SchedulesRestartWithReplay()
        {
            var registry = new RendererTabIsolationRegistry(new RendererRestartPolicy(maxRestartAttempts: 3, baseBackoffMs: 150, maxBackoffMs: 1000));
            registry.ApplyNavigation(5, "https://recover.example/page", false);
            registry.MarkSessionStarted(5, 2001);

            var decision = registry.HandleSessionExit(5, 2001, isShuttingDown: false);

            Assert.False(decision.IsExpectedExit);
            Assert.True(decision.ShouldRestart);
            Assert.Equal(150, decision.RestartDelayMs);
            Assert.Equal(1, decision.RestartAttempt);
            Assert.Equal("https://recover.example/page", decision.ReplayUrl);
            Assert.False(decision.ReplayIsUserInput);
        }

        [Fact]
        public void RendererTabIsolationRegistry_ExhaustedRestartBudget_StopsRestart()
        {
            var registry = new RendererTabIsolationRegistry(new RendererRestartPolicy(maxRestartAttempts: 1, baseBackoffMs: 100, maxBackoffMs: 1000));
            registry.ApplyNavigation(6, "https://budget.example/a", true);
            registry.MarkSessionStarted(6, 3001);

            var first = registry.HandleSessionExit(6, 3001, isShuttingDown: false);
            Assert.True(first.ShouldRestart);

            registry.MarkSessionStarted(6, 3002);
            var second = registry.HandleSessionExit(6, 3002, isShuttingDown: false);

            Assert.False(second.ShouldRestart);
            Assert.Equal("restart-budget-exhausted", second.Reason);
        }

        [Fact]
        public void RendererTabIsolationRegistry_StaleExit_DoesNotRestart()
        {
            var registry = new RendererTabIsolationRegistry();
            registry.ApplyNavigation(7, "https://stale.example", true);
            registry.MarkSessionStarted(7, 4001);

            var decision = registry.HandleSessionExit(7, 4000, isShuttingDown: false);

            Assert.True(decision.IsExpectedExit);
            Assert.False(decision.ShouldRestart);
            Assert.Equal("stale-exit", decision.Reason);
        }

        [Fact]
        public void RendererTabIsolationRegistry_ClosedTab_DoesNotRestart()
        {
            var registry = new RendererTabIsolationRegistry();
            registry.ApplyNavigation(8, "https://closed.example", true);
            registry.MarkSessionStarted(8, 5001);
            registry.CloseTab(8);

            var decision = registry.HandleSessionExit(8, 5001, isShuttingDown: false);

            Assert.True(decision.IsExpectedExit);
            Assert.False(decision.ShouldRestart);
            Assert.Equal("tab-closed", decision.Reason);
        }

        [Fact]
        public void RendererTabIsolationRegistry_Shutdown_DoesNotRestart()
        {
            var registry = new RendererTabIsolationRegistry();
            registry.ApplyNavigation(9, "https://shutdown.example", true);
            registry.MarkSessionStarted(9, 6001);

            var decision = registry.HandleSessionExit(9, 6001, isShuttingDown: true);

            Assert.True(decision.IsExpectedExit);
            Assert.False(decision.ShouldRestart);
            Assert.Equal("host-shutdown", decision.Reason);
        }

        [Fact]
        public void RendererTabIsolationRegistry_MissingTab_ProducesExpectedMissingDecision()
        {
            var registry = new RendererTabIsolationRegistry();

            var decision = registry.HandleSessionExit(404, 1, isShuttingDown: false);

            Assert.True(decision.IsExpectedExit);
            Assert.False(decision.ShouldRestart);
            Assert.Equal("tab-missing", decision.Reason);
        }

        [Fact]
        public void RendererTabIsolationRegistry_TryGetSnapshot_FalseForUnknownTab()
        {
            var registry = new RendererTabIsolationRegistry();

            Assert.False(registry.TryGetSnapshot(12345, out _));
        }

        [Fact]
        public void RendererTabIsolationRegistry_InvalidTabNavigation_ReturnsNoAssignmentDecision()
        {
            var registry = new RendererTabIsolationRegistry();

            var decision = registry.ApplyNavigation(0, "https://example.com", true);

            Assert.False(decision.HasValidAssignment);
            Assert.False(decision.RequiresReassignment);
            Assert.Null(decision.RequestedAssignmentKey);
        }

        [Fact]
        public void RendererTabIsolationRegistry_InvalidTab_CannotStartSession()
        {
            var registry = new RendererTabIsolationRegistry();

            var allowed = registry.CanStartSession(0, out var retryAfterMs, out var reason);

            Assert.False(allowed);
            Assert.Equal(0, retryAfterMs);
            Assert.Equal("invalid-tab", reason);
        }

        [Fact]
        public void RendererTabIsolationRegistry_StableRun_ResetsRestartBudget()
        {
            var policy = new RendererRestartPolicy(
                maxRestartAttempts: 1,
                baseBackoffMs: 0,
                maxBackoffMs: 0,
                stableSessionResetMs: 50,
                crashWindowMs: 5000,
                maxCrashCountInWindow: 50,
                quarantineMs: 0);
            var registry = new RendererTabIsolationRegistry(policy);
            registry.ApplyNavigation(10, "https://stable-reset.example", true);
            registry.MarkSessionStarted(10, 7001);

            var first = registry.HandleSessionExit(10, 7001, isShuttingDown: false);
            Assert.True(first.ShouldRestart);

            registry.MarkSessionStarted(10, 7002);
            Thread.Sleep(80);

            var second = registry.HandleSessionExit(10, 7002, isShuttingDown: false);
            Assert.True(second.ShouldRestart);
            Assert.Equal("unexpected-exit-restart", second.Reason);
        }

        [Fact]
        public void RendererTabIsolationRegistry_QuarantinesCrashLoops()
        {
            var policy = new RendererRestartPolicy(
                maxRestartAttempts: 10,
                baseBackoffMs: 0,
                maxBackoffMs: 0,
                stableSessionResetMs: 0,
                crashWindowMs: 10_000,
                maxCrashCountInWindow: 2,
                quarantineMs: 5_000);
            var registry = new RendererTabIsolationRegistry(policy);
            registry.ApplyNavigation(11, "https://crash-loop.example", false);

            registry.MarkSessionStarted(11, 8001);
            var first = registry.HandleSessionExit(11, 8001, isShuttingDown: false);
            Assert.True(first.ShouldRestart);

            registry.MarkSessionStarted(11, 8002);
            var second = registry.HandleSessionExit(11, 8002, isShuttingDown: false);
            Assert.True(second.ShouldRestart);

            registry.MarkSessionStarted(11, 8003);
            var third = registry.HandleSessionExit(11, 8003, isShuttingDown: false);
            Assert.False(third.ShouldRestart);
            Assert.True(third.IsCrashLoopQuarantined);
            Assert.Equal("quarantined-crash-loop", third.Reason);
            Assert.True(third.RetryAfterMs > 0);

            Assert.False(registry.CanStartSession(11, out var retryAfterMs, out var reason));
            Assert.Equal("quarantined-crash-loop", reason);
            Assert.True(retryAfterMs > 0);
        }

        [Fact]
        public void RendererTabIsolationRegistry_UserNavigation_LiftsQuarantine()
        {
            var policy = new RendererRestartPolicy(
                maxRestartAttempts: 10,
                baseBackoffMs: 0,
                maxBackoffMs: 0,
                stableSessionResetMs: 0,
                crashWindowMs: 10_000,
                maxCrashCountInWindow: 1,
                quarantineMs: 60_000);
            var registry = new RendererTabIsolationRegistry(policy);
            registry.ApplyNavigation(12, "https://recover.example", false);

            registry.MarkSessionStarted(12, 9001);
            Assert.True(registry.HandleSessionExit(12, 9001, isShuttingDown: false).ShouldRestart);
            registry.MarkSessionStarted(12, 9002);
            var quarantined = registry.HandleSessionExit(12, 9002, isShuttingDown: false);
            Assert.True(quarantined.IsCrashLoopQuarantined);

            Assert.False(registry.CanStartSession(12, out _, out _));
            registry.ApplyNavigation(12, "https://recover.example/manual-reload", true);
            Assert.True(registry.CanStartSession(12, out _, out _));
        }
    }
}
