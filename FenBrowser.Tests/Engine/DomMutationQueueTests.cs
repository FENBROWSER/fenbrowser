using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Engine; // Phase enum
using Xunit;
using System.Collections.Generic;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// Tests for DomMutationQueue per Phase D-4 spec.
    /// Required tests:
    /// 1. JS mutation during JSExecution
    /// 2. No layout reentrancy
    /// 3. Multiple mutations batch correctly
    /// 4. Explicit invalidation respected
    /// </summary>
    [Collection("Engine Tests")]
    public class DomMutationQueueTests
    {
        public DomMutationQueueTests()
        {
            // Clear queue state and set phase for test isolation
            DomMutationQueue.Instance.Clear();
            EnginePhaseManager.EnterPhase(EnginePhase.JSExecution);
        }

        [Fact]
        public void EnqueueMutation_DuringJSExecution_Succeeds()
        {
            // Arrange
            var mutation = new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style,
                new object(),
                "class",
                "old",
                "new"
            );

            // Act - should succeed in JSExecution phase
            DomMutationQueue.Instance.EnqueueMutation(mutation);

            // Assert
            Assert.Equal(1, DomMutationQueue.Instance.PendingCount);
            Assert.True(DomMutationQueue.Instance.HasPendingMutations);
        }

        [Fact]
        public void MultipleMutations_BatchCorrectly()
        {
            // Arrange
            var mutations = new List<DomMutation>
            {
                new DomMutation(MutationType.AttributeChange, InvalidationKind.Style, new object()),
                new DomMutation(MutationType.TextChange, InvalidationKind.Layout, new object()),
                new DomMutation(MutationType.NodeInsert, InvalidationKind.Layout | InvalidationKind.Paint, new object())
            };

            // Act
            foreach (var mutation in mutations)
            {
                DomMutationQueue.Instance.EnqueueMutation(mutation);
            }

            // Assert - all batched
            Assert.Equal(3, DomMutationQueue.Instance.PendingCount);
        }

        [Fact]
        public void ApplyPendingMutations_AppliesInOrder()
        {
            // Arrange
            var appliedOrder = new List<string>();
            
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.AttributeChange, InvalidationKind.Style, "first"));
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.TextChange, InvalidationKind.Layout, "second"));
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.NodeInsert, InvalidationKind.Paint, "third"));

            // Act
            DomMutationQueue.Instance.ApplyPendingMutations(mutation =>
            {
                appliedOrder.Add(mutation.Target.ToString());
            });

            // Assert - applied in enqueue order
            Assert.Equal(3, appliedOrder.Count);
            Assert.Equal("first", appliedOrder[0]);
            Assert.Equal("second", appliedOrder[1]);
            Assert.Equal("third", appliedOrder[2]);
        }

        [Fact]
        public void ApplyPendingMutations_ReturnsAccumulatedInvalidation()
        {
            // Arrange
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.AttributeChange, InvalidationKind.Style, new object()));
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.TextChange, InvalidationKind.Layout, new object()));
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.NodeInsert, InvalidationKind.Paint, new object()));

            // Act
            var invalidation = DomMutationQueue.Instance.ApplyPendingMutations(_ => { });

            // Assert - all invalidation kinds accumulated
            Assert.True((invalidation & InvalidationKind.Style) != 0);
            Assert.True((invalidation & InvalidationKind.Layout) != 0);
            Assert.True((invalidation & InvalidationKind.Paint) != 0);
        }

        [Fact]
        public void ApplyPendingMutations_ClearsQueue()
        {
            // Arrange
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.AttributeChange, InvalidationKind.Style, new object()));
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.TextChange, InvalidationKind.Layout, new object()));

            // Act
            DomMutationQueue.Instance.ApplyPendingMutations(_ => { });

            // Assert - queue cleared
            Assert.Equal(0, DomMutationQueue.Instance.PendingCount);
            Assert.False(DomMutationQueue.Instance.HasPendingMutations);
        }

        [Fact]
        public void Clear_RemovesAllPendingMutations()
        {
            // Arrange
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.AttributeChange, InvalidationKind.Style, new object()));
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.TextChange, InvalidationKind.Layout, new object()));

            // Act
            DomMutationQueue.Instance.Clear();

            // Assert
            Assert.Equal(0, DomMutationQueue.Instance.PendingCount);
            Assert.False(DomMutationQueue.Instance.HasPendingMutations);
            Assert.Equal(InvalidationKind.None, DomMutationQueue.Instance.PendingInvalidation);
        }

        [Fact]
        public void PendingInvalidation_AccumulatesCorrectly()
        {
            // Arrange & Act
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.AttributeChange, InvalidationKind.Style, new object()));
            
            var afterFirst = DomMutationQueue.Instance.PendingInvalidation;
            
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.NodeInsert, InvalidationKind.Layout, new object()));
            
            var afterSecond = DomMutationQueue.Instance.PendingInvalidation;

            // Assert - invalidation accumulates
            Assert.Equal(InvalidationKind.Style, afterFirst);
            Assert.True((afterSecond & InvalidationKind.Style) != 0);
            Assert.True((afterSecond & InvalidationKind.Layout) != 0);
        }

        [Fact]
        public void MutationError_DoesNotBreakQueue()
        {
            // Arrange
            var successfulCount = 0;
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.AttributeChange, InvalidationKind.Style, "first"));
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.TextChange, InvalidationKind.Layout, "error"));
            DomMutationQueue.Instance.EnqueueMutation(
                new DomMutation(MutationType.NodeInsert, InvalidationKind.Paint, "third"));

            // Act - second mutation throws, but third still applies
            DomMutationQueue.Instance.ApplyPendingMutations(mutation =>
            {
                if (mutation.Target.ToString() == "error")
                    throw new System.Exception("Simulated error");
                successfulCount++;
            });

            // Assert - error didn't break the queue
            Assert.Equal(2, successfulCount);
            Assert.Equal(0, DomMutationQueue.Instance.PendingCount);
        }
    }
}
