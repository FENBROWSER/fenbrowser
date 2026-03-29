using FenBrowser.Core.Engine;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class EngineContractTests
    {
        [Fact]
        public void PipelineStage_MetadataAndTransitions_AreConsistent()
        {
            Assert.Equal(PipelineStage.Tokenizing, PipelineStage.Idle.GetNextStage());
            Assert.Equal(PipelineStage.Idle, PipelineStage.Tokenizing.GetPreviousStage());
            Assert.Equal("DocumentBytes", PipelineStage.Tokenizing.InputTypeName());
            Assert.Equal("TokenStream", PipelineStage.Tokenizing.OutputTypeName());
            Assert.True(PipelineStage.Tokenizing.IsValidTransition(PipelineStage.Parsing));
            Assert.False(PipelineStage.Tokenizing.IsValidTransition(PipelineStage.Layout));
            Assert.Null(PipelineStage.Presenting.GetNextStage());
        }

        [Fact]
        public void NavigationSubresourceTracker_Snapshot_IsDetachedFromLiveState()
        {
            var tracker = new NavigationSubresourceTracker();
            tracker.ResetNavigation(10);
            tracker.MarkLoadStarted(10);

            var snapshot = tracker.SnapshotPendingCounts();
            tracker.MarkLoadStarted(10);

            Assert.True(tracker.HasPendingLoads(10));
            Assert.Equal(1, snapshot[10]);
            Assert.Equal(2, tracker.GetPendingCount(10));
        }
    }
}
