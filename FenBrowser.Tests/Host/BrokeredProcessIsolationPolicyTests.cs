using System;
using System.Reflection;
using FenBrowser.Host.ProcessIsolation;
using Xunit;

namespace FenBrowser.Tests.Host
{
    public class BrokeredProcessIsolationPolicyTests
    {
        private const string EnvKey = "FEN_RENDERER_ASSIGNMENT_POLICY";
        private const string PoolEnabledEnvKey = "FEN_RENDERER_PROCESS_POOL";
        private const string PoolMaxSizeEnvKey = "FEN_RENDERER_POOL_MAX_SIZE";
        private const string PoolWarmTargetEnvKey = "FEN_RENDERER_POOL_WARM_TARGET";
        private const string PoolPrewarmEnvKey = "FEN_RENDERER_POOL_PREWARM";
        private static readonly object EnvLock = new();

        [Theory]
        [InlineData("site", "site-per-process-lite")]
        [InlineData("site-per-process-lite", "site-per-process-lite")]
        [InlineData("origin", "origin-strict")]
        [InlineData("origin-strict", "origin-strict")]
        public void BrokeredCoordinator_ParsesAssignmentPolicyFromEnvironment(string value, string expected)
        {
            var policy = WithAssignmentPolicy(value, () =>
            {
                var coordinator = new BrokeredProcessIsolationCoordinator();
                var policyField = typeof(BrokeredProcessIsolationCoordinator)
                    .GetField("_assignmentPolicy", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(policyField);
                return policyField!.GetValue(coordinator) as string;
            });

            Assert.Equal(expected, policy);
        }

        [Fact]
        public void BrokeredCoordinator_UnknownAssignmentPolicy_FallsBackToOriginStrict()
        {
            var policy = WithAssignmentPolicy("mystery-policy", () =>
            {
                var coordinator = new BrokeredProcessIsolationCoordinator();
                var policyField = typeof(BrokeredProcessIsolationCoordinator)
                    .GetField("_assignmentPolicy", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(policyField);
                return policyField!.GetValue(coordinator) as string;
            });

            Assert.Equal("origin-strict", policy);
        }

        [Fact]
        public void BrokeredCoordinator_ProcessPoolDisabled_LeavesPoolNull()
        {
            WithEnvironment(new[]
            {
                (PoolEnabledEnvKey, "0")
            }, () =>
            {
                var coordinator = new BrokeredProcessIsolationCoordinator();
                var pool = GetPrivateFieldValue(coordinator, "_rendererProcessPool");
                Assert.Null(pool);
            });
        }

        [Fact]
        public void BrokeredCoordinator_ProcessPoolEnabled_CreatesPool()
        {
            WithEnvironment(new[]
            {
                (PoolEnabledEnvKey, "1")
            }, () =>
            {
                var coordinator = new BrokeredProcessIsolationCoordinator();
                var pool = GetPrivateFieldValue(coordinator, "_rendererProcessPool");
                Assert.NotNull(pool);
            });
        }

        [Fact]
        public void BrokeredCoordinator_PoolConfig_ClampsWarmTargetToMax()
        {
            var config = WithEnvironment(new[]
            {
                (PoolMaxSizeEnvKey, "2"),
                (PoolWarmTargetEnvKey, "7"),
                (PoolPrewarmEnvKey, "1")
            }, InvokeBuildRendererPoolConfig);

            Assert.NotNull(config);
            Assert.Equal(2, config.MaxPoolSize);
            Assert.Equal(2, config.TargetWarmCount);
            Assert.True(config.EnablePreWarm);
        }

        [Fact]
        public void BrokeredCoordinator_PoolConfig_UsesSafeMinimumsForInvalidValues()
        {
            var config = WithEnvironment(new[]
            {
                (PoolMaxSizeEnvKey, "-1"),
                (PoolWarmTargetEnvKey, "-9"),
                (PoolPrewarmEnvKey, "off")
            }, InvokeBuildRendererPoolConfig);

            Assert.NotNull(config);
            Assert.Equal(1, config.MaxPoolSize);
            Assert.Equal(0, config.TargetWarmCount);
            Assert.False(config.EnablePreWarm);
        }

        private static T WithAssignmentPolicy<T>(string value, Func<T> action)
        {
            lock (EnvLock)
            {
                var previous = Environment.GetEnvironmentVariable(EnvKey);
                try
                {
                    Environment.SetEnvironmentVariable(EnvKey, value);
                    return action();
                }
                finally
                {
                    Environment.SetEnvironmentVariable(EnvKey, previous);
                }
            }
        }

        private static void WithEnvironment((string key, string value)[] pairs, Action action)
        {
            WithEnvironment(
                pairs,
                () =>
                {
                    action();
                    return 0;
                });
        }

        private static T WithEnvironment<T>((string key, string value)[] pairs, Func<T> action)
        {
            lock (EnvLock)
            {
                var previous = new string[pairs.Length];
                for (var i = 0; i < pairs.Length; i++)
                {
                    previous[i] = Environment.GetEnvironmentVariable(pairs[i].key);
                    Environment.SetEnvironmentVariable(pairs[i].key, pairs[i].value);
                }

                try
                {
                    return action();
                }
                finally
                {
                    for (var i = 0; i < pairs.Length; i++)
                    {
                        Environment.SetEnvironmentVariable(pairs[i].key, previous[i]);
                    }
                }
            }
        }

        private static object GetPrivateFieldValue(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return field!.GetValue(target);
        }

        private static ProcessIsolationConfig InvokeBuildRendererPoolConfig()
        {
            var method = typeof(BrokeredProcessIsolationCoordinator).GetMethod(
                "BuildRendererPoolConfig",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var result = method!.Invoke(null, null);
            Assert.NotNull(result);
            return Assert.IsType<ProcessIsolationConfig>(result);
        }
    }
}
