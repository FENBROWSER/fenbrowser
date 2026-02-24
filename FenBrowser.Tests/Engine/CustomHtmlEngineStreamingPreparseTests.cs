using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineStreamingPreparseTests
    {
        private static bool InvokeShouldRun(bool enabled, int htmlLength)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "ShouldRunStreamingParsePrepass",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { enabled, htmlLength });
            return Assert.IsType<bool>(result);
        }

        private static bool InvokeShouldEmit(int checkpointOrdinal, int repaintCount)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "ShouldEmitStreamingPreparseRepaint",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { checkpointOrdinal, repaintCount });
            return Assert.IsType<bool>(result);
        }

        [Fact]
        public void ShouldRunStreamingParsePrepass_RequiresFeatureEnabledAndThresholdLength()
        {
            Assert.False(InvokeShouldRun(enabled: false, htmlLength: 100000));
            Assert.False(InvokeShouldRun(enabled: true, htmlLength: 32767));
            Assert.True(InvokeShouldRun(enabled: true, htmlLength: 32768));
        }

        [Fact]
        public void ShouldEmitStreamingPreparseRepaint_EmitsFirstAndStrideCheckpoints()
        {
            Assert.True(InvokeShouldEmit(checkpointOrdinal: 1, repaintCount: 0));
            Assert.True(InvokeShouldEmit(checkpointOrdinal: 3, repaintCount: 1));
            Assert.False(InvokeShouldEmit(checkpointOrdinal: 4, repaintCount: 1));
        }

        [Fact]
        public void ShouldEmitStreamingPreparseRepaint_StopsAtCap()
        {
            Assert.False(InvokeShouldEmit(checkpointOrdinal: 9, repaintCount: 4));
        }
    }
}
