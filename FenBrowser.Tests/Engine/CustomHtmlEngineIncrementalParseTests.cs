using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineIncrementalParseTests
    {
        private static bool InvokeShouldEmit(int checkpointOrdinal, bool isFinal, int emittedCount)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "ShouldEmitIncrementalParseRepaint",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { checkpointOrdinal, isFinal, emittedCount });
            return Assert.IsType<bool>(result);
        }

        [Fact]
        public void ShouldEmitIncrementalParseRepaint_FirstCheckpointAlwaysEmits()
        {
            Assert.True(InvokeShouldEmit(checkpointOrdinal: 1, isFinal: false, emittedCount: 0));
        }

        [Fact]
        public void ShouldEmitIncrementalParseRepaint_UsesConfiguredStrideForIntermediateCheckpoints()
        {
            Assert.True(InvokeShouldEmit(checkpointOrdinal: 2, isFinal: false, emittedCount: 1));
            Assert.False(InvokeShouldEmit(checkpointOrdinal: 3, isFinal: false, emittedCount: 1));
        }

        [Fact]
        public void ShouldEmitIncrementalParseRepaint_FinalCheckpointEmitsBeforeCapAndStopsAtCap()
        {
            Assert.True(InvokeShouldEmit(checkpointOrdinal: 5, isFinal: true, emittedCount: 7));
            Assert.False(InvokeShouldEmit(checkpointOrdinal: 5, isFinal: true, emittedCount: 8));
        }
    }
}
