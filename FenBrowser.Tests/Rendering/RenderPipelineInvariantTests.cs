using System;
using System.Threading;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class RenderPipelineInvariantTests
    {
        public RenderPipelineInvariantTests()
        {
            RenderPipeline.StrictInvariants = true;
            RenderPipeline.Reset();
        }

        [Fact]
        public void ValidFrameSequence_TransitionsIdleToPresentToIdle()
        {
            long before = RenderPipeline.FrameSequence;

            RenderPipeline.EnterLayout();
            RenderPipeline.EndLayout();
            RenderPipeline.EnterPaint();
            RenderPipeline.EndPaint();
            RenderPipeline.EnterPresent();
            RenderPipeline.EndFrame();

            Assert.Equal(RenderPhase.Idle, RenderPipeline.CurrentPhase);
            Assert.True(RenderPipeline.FrameSequence > before);
        }

        [Fact]
        public void EnterPaint_WithoutLayoutFrozen_ThrowsInvariantException()
        {
            RenderPipeline.EnterLayout();

            var ex = Record.Exception(() => RenderPipeline.EnterPaint());

            Assert.NotNull(ex);
            Assert.IsType<RenderPipelineInvariantException>(ex);
        }

        [Fact]
        public void EndFrame_WithoutPresent_ThrowsInvariantException()
        {
            RenderPipeline.EnterLayout();
            RenderPipeline.EndLayout();
            RenderPipeline.EnterPaint();
            RenderPipeline.EndPaint();

            var ex = Record.Exception(() => RenderPipeline.EndFrame());

            Assert.NotNull(ex);
            Assert.IsType<RenderPipelineInvariantException>(ex);
        }

        [Fact]
        public void AssertLayerSeparation_ThrowsOutsideCompositeAndPresent()
        {
            RenderPipeline.EnterLayout();

            var ex = Record.Exception(() => RenderPipeline.AssertLayerSeparation(isDebugOrOverlay: true));

            Assert.NotNull(ex);
            Assert.IsType<RenderPipelineInvariantException>(ex);
        }

        [Fact]
        public void EndFrame_TracksLastFrameDuration()
        {
            RenderPipeline.EnterLayout();
            Thread.Sleep(2);
            RenderPipeline.EndLayout();
            RenderPipeline.EnterPaint();
            Thread.Sleep(2);
            RenderPipeline.EndPaint();
            RenderPipeline.EnterPresent();
            RenderPipeline.EndFrame();

            Assert.True(RenderPipeline.LastFrameDuration > TimeSpan.Zero);
        }
    }
}
