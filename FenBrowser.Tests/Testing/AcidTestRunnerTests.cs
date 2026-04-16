using System.Threading.Tasks;
using FenBrowser.FenEngine.Testing;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Testing
{
    public class AcidTestRunnerTests
    {
        [Fact]
        public async Task RunAcid2Async_UsesHttpTopUrl()
        {
            var runner = new AcidTestRunner();
            string capturedUrl = null;

            var result = await runner.RunAcid2Async(url =>
            {
                capturedUrl = url;
                return Task.FromResult(new SKBitmap(128, 128));
            });

            Assert.Equal("http://acid2.acidtests.org/#top", capturedUrl);
            Assert.Equal("http://acid2.acidtests.org/#top", result.Url);
        }
    }
}
