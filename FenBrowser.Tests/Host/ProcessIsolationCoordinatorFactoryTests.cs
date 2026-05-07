using System;
using FenBrowser.Host.ProcessIsolation;
using Xunit;

namespace FenBrowser.Tests.Host
{
    public class ProcessIsolationCoordinatorFactoryTests
    {
        private const string EnvKey = "FEN_PROCESS_ISOLATION";
        private static readonly object EnvLock = new();

        [Fact]
        public void CreateFromEnvironment_DefaultsToBrokeredMode()
        {
            var coordinator = WithIsolationMode(null, ProcessIsolationCoordinatorFactory.CreateFromEnvironment);
            Assert.IsType<BrokeredProcessIsolationCoordinator>(coordinator);
        }

        [Theory]
        [InlineData("in-process")]
        [InlineData("inproc")]
        [InlineData("off")]
        [InlineData("0")]
        public void CreateFromEnvironment_UsesInProcessMode_WhenRequested(string mode)
        {
            var coordinator = WithIsolationMode(mode, ProcessIsolationCoordinatorFactory.CreateFromEnvironment);
            Assert.IsType<InProcessIsolationCoordinator>(coordinator);
        }

        [Theory]
        [InlineData("brokered")]
        [InlineData("auto")]
        [InlineData("on")]
        [InlineData("1")]
        public void CreateFromEnvironment_UsesBrokeredMode_WhenRequested(string mode)
        {
            var coordinator = WithIsolationMode(mode, ProcessIsolationCoordinatorFactory.CreateFromEnvironment);
            Assert.IsType<BrokeredProcessIsolationCoordinator>(coordinator);
        }

        [Fact]
        public void CreateFromEnvironment_FallsBackToBrokeredMode_ForUnknownValue()
        {
            var coordinator = WithIsolationMode("mystery-mode", ProcessIsolationCoordinatorFactory.CreateFromEnvironment);
            Assert.IsType<BrokeredProcessIsolationCoordinator>(coordinator);
        }

        private static T WithIsolationMode<T>(string mode, Func<T> action)
        {
            lock (EnvLock)
            {
                var previous = Environment.GetEnvironmentVariable(EnvKey);
                try
                {
                    Environment.SetEnvironmentVariable(EnvKey, mode);
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
