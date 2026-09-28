using System.Threading.Tasks;
using FenBrowser.FenEngine.Adapters;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    [Collection(SvgRendererBackendStateCollection.Name)]
    public sealed class SvgRendererWarmupTests
    {
        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("on", true)]
        [InlineData(" ON ", true)]
        [InlineData("off", false)]
        [InlineData(" Off ", false)]
        public void Flag_DefaultsOnAndOnlyOffDisables(string? value, bool enabled)
        {
            Assert.Equal(enabled, SvgRendererWarmup.IsEnabled(value));
        }

        [Fact]
        public void WarmupDocument_RendersAdmissibly()
        {
            // A refused warm-up document would silently stop warming anything.
            using var result = new FenSvgRenderer().Render(SvgRendererWarmup.Document);

            Assert.True(SvgRenderResult.IsAdmissible(result), result.ErrorMessage);
        }

        [Fact]
        public async Task Start_RunsOncePerProcess()
        {
            SvgRendererWarmup.ResetForTesting();
            try
            {
                Task first = SvgRendererWarmup.Start("on");
                Task second = SvgRendererWarmup.Start("on");

                Assert.Same(first, second);
                await first;
                Assert.True(first.IsCompletedSuccessfully);
            }
            finally
            {
                SvgRendererWarmup.ResetForTesting();
            }
        }

        [Fact]
        public void Start_WhenOff_DoesNotRun()
        {
            SvgRendererWarmup.ResetForTesting();
            try
            {
                Task task = SvgRendererWarmup.Start("off");

                Assert.Same(Task.CompletedTask, task);
                Assert.Same(Task.CompletedTask, SvgRendererWarmup.Completion);
            }
            finally
            {
                SvgRendererWarmup.ResetForTesting();
            }
        }
    }
}
