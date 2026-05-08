using System;
using System.Reflection;
using FenBrowser.Host.ProcessIsolation;
using Xunit;

namespace FenBrowser.Tests.Host
{
    public class BrokeredProcessIsolationPolicyTests
    {
        private const string EnvKey = "FEN_RENDERER_ASSIGNMENT_POLICY";
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
    }
}
