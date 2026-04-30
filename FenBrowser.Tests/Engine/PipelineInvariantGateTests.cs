using FenBrowser.Core.Engine;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class PipelineInvariantGateTests
    {
        [Fact]
        public void BeginStage_SkippingRequiredIntermediateStage_Throws()
        {
            PipelineContext.Reset();
            var context = PipelineContext.Current;

            using (context.BeginScopedFrame())
            {
                using (context.BeginScopedStage(PipelineStage.Tokenizing))
                {
                }

                var ex = Assert.Throws<PipelineStageException>(() => context.BeginStage(PipelineStage.Styling));
                Assert.Contains("strict sequential order", ex.Message);
            }
        }

        [Fact]
        public void BeginStage_ParsingToTokenizingInterleave_IsAllowed()
        {
            PipelineContext.Reset();
            var context = PipelineContext.Current;

            using (context.BeginScopedFrame())
            {
                using (context.BeginScopedStage(PipelineStage.Tokenizing))
                {
                }

                using (context.BeginScopedStage(PipelineStage.Parsing))
                {
                }

                using (context.BeginScopedStage(PipelineStage.Tokenizing))
                {
                }
            }
        }

        [Fact]
        public void GetStyleSnapshot_BeforeStyleCompletes_Throws()
        {
            PipelineContext.Reset();
            var context = PipelineContext.Current;

            using (context.BeginScopedFrame())
            {
                using (context.BeginScopedStage(PipelineStage.Tokenizing))
                {
                    var ex = Assert.Throws<PipelineStageException>(() => context.GetStyleSnapshot());
                    Assert.Contains("Cannot read Styling snapshot", ex.Message);
                }
            }
        }

        [Fact]
        public void DirtyFlags_StyleInvalidation_PropagatesForwardOnly()
        {
            var flags = new DirtyFlags();

            flags.InvalidateStyle();

            Assert.True(flags.IsStyleDirty);
            Assert.True(flags.IsLayoutDirty);
            Assert.True(flags.IsPaintDirty);
            Assert.True(flags.IsRasterDirty);

            flags.ClearStyleDirty();

            Assert.False(flags.IsStyleDirty);
            Assert.True(flags.IsLayoutDirty);
            Assert.True(flags.IsPaintDirty);
            Assert.True(flags.IsRasterDirty);
        }

        [Fact]
        public void DirtyFlags_LayoutInvalidation_DoesNotDirtyStyle()
        {
            var flags = new DirtyFlags();

            flags.InvalidateLayout();

            Assert.False(flags.IsStyleDirty);
            Assert.True(flags.IsLayoutDirty);
            Assert.True(flags.IsPaintDirty);
            Assert.True(flags.IsRasterDirty);
        }
    }
}
