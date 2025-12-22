using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// Integration tests for Phase D-4: DOM Mutation Batching.
    /// Verifies that JS mutations are deferred and batched, applied only before Layout.
    /// </summary>
    [Collection("Engine Tests")]
    public class DomMutationBatchingTests
    {
        public DomMutationBatchingTests()
        {
            // Reset state
            DomMutationQueue.Instance.Clear();
            EngineContext.Current.BeginPhase(EnginePhase.Idle);
        }

        [Fact]
        public void Mutations_AreDeferred_UntilFlush()
        {
            // Arrange
            var el = new Element("DIV");
            el.Attributes["id"] = "initial"; // Setup bypassing queue (Idle phase allows internal?)
            
            // Actually Element.SetAttribute now has Guard!
            // So setting it directly in Idle is allowed if guard permits Idle?
            // Element.guard: AssertNotInPhase(Measure, Layout, Paint). Idle is SAFE.
            
            // Act: Enqueue mutation (Simulating JS)
            EngineContext.Current.BeginPhase(EnginePhase.JSExecution);
            
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style,
                el,
                "id",
                "initial",
                "new_id"
            ));

            // Assert: State is NOT changed yet
            Assert.Equal("initial", el.Attributes["id"]);
            Assert.True(DomMutationQueue.Instance.HasPendingMutations);

            // Act: Flush (Simulating Renderer)
            EngineContext.Current.BeginPhase(EnginePhase.Idle); // Flush happens in Idle/Pre-Measure
            
            DomMutationQueue.Instance.ApplyPendingMutations(mutation =>
            {
                if (mutation.Type == MutationType.AttributeChange && mutation.Target is Element e)
                {
                    e.SetAttribute(mutation.PropertyName, mutation.NewValue.ToString());
                }
            });

            // Assert: State IS changed now
            Assert.Equal("new_id", el.Attributes["id"]);
            Assert.False(DomMutationQueue.Instance.HasPendingMutations);
        }

        [Fact]
        public void MultipleMutations_BatchSingleInvalidation()
        {
            // Arrange
            var el = new Element("DIV");
            
            EngineContext.Current.BeginPhase(EnginePhase.JSExecution);
            
            // Queue 3 valid mutations
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(MutationType.AttributeChange, InvalidationKind.Style, el, "a", null, "1"));
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(MutationType.AttributeChange, InvalidationKind.Layout, el, "b", null, "2"));
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(MutationType.AttributeChange, InvalidationKind.Style, el, "c", null, "3"));

            // Act: Flush
            EngineContext.Current.BeginPhase(EnginePhase.Idle);
            
            var invalidation = DomMutationQueue.Instance.ApplyPendingMutations(mutation =>
            {
                var e = mutation.Target as Element;
                e.SetAttribute(mutation.PropertyName, mutation.NewValue.ToString());
            });

            // Assert
            // Should be Style | Layout (combined)
            Assert.Equal(InvalidationKind.Style | InvalidationKind.Layout, invalidation);
            
            // Verify all applied
            Assert.Equal("1", el.GetAttribute("a"));
            Assert.Equal("2", el.GetAttribute("b"));
            Assert.Equal("3", el.GetAttribute("c"));
        }
        
        [Fact]
        public void DirectMutation_DuringLayout_Throws()
        {
            // Verify the Guard on Element works
            var el = new Element("DIV");
            
            EngineContext.Current.BeginPhase(EnginePhase.Layout);
            
            Assert.Throws<InvalidOperationException>(() =>
            {
                el.SetAttribute("id", "fail");
            });
            
            // Also AppendChild
            Assert.Throws<InvalidOperationException>(() =>
            {
                el.AppendChild(new Element("SPAN"));
            });
            
            EngineContext.Current.BeginPhase(EnginePhase.Idle);
        }
    }
}
