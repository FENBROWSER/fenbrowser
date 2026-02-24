using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineInterleavedParsePolicyTests
    {
        private static int InvokeResolveBatchSize(bool enabled, int htmlLength)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "ResolveInterleavedTokenBatchSize",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { enabled, htmlLength });
            return Assert.IsType<int>(result);
        }

        [Fact]
        public void ResolveInterleavedTokenBatchSize_RespectsFeatureGateAndThreshold()
        {
            Assert.Equal(0, InvokeResolveBatchSize(enabled: false, htmlLength: 40000));
            Assert.Equal(0, InvokeResolveBatchSize(enabled: true, htmlLength: 8191));
            Assert.Equal(128, InvokeResolveBatchSize(enabled: true, htmlLength: 8192));
        }

        [Fact]
        public void ResolveInterleavedTokenBatchSize_UsesTieredBatchPolicyForLargeDocuments()
        {
            Assert.Equal(128, InvokeResolveBatchSize(enabled: true, htmlLength: 50000));
            Assert.Equal(256, InvokeResolveBatchSize(enabled: true, htmlLength: 131072));
            Assert.Equal(512, InvokeResolveBatchSize(enabled: true, htmlLength: 524288));
        }
    }
}
