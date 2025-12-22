using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core;
using FenBrowser.Core.Engine; // Phase enum

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Types of DOM mutations that can be batched.
    /// </summary>
    public enum MutationType
    {
        AttributeChange,
        TextChange,
        NodeInsert,
        NodeRemove
    }

    /// <summary>
    /// Indicates which subsystems need invalidation after mutation.
    /// </summary>
    [Flags]
    public enum InvalidationKind
    {
        None = 0,
        Style = 1,      // CSS recalc needed
        Layout = 2,     // Layout pass needed
        Paint = 4       // Paint only (no layout change)
    }

    /// <summary>
    /// Represents a single DOM mutation to be applied.
    /// </summary>
    public sealed class DomMutation
    {
        public MutationType Type { get; }
        public InvalidationKind Invalidation { get; }
        public object Target { get; }
        public string PropertyName { get; }
        public object OldValue { get; }
        public object NewValue { get; }

        public DomMutation(
            MutationType type,
            InvalidationKind invalidation,
            object target,
            string propertyName = null,
            object oldValue = null,
            object newValue = null)
        {
            Type = type;
            Invalidation = invalidation;
            Target = target;
            PropertyName = propertyName;
            OldValue = oldValue;
            NewValue = newValue;
        }
    }

    /// <summary>
    /// Batches DOM mutations from JavaScript execution.
    /// 
    /// CORE RULE: JS mutations NEVER apply immediately. Ever.
    /// 
    /// Mutation Pipeline:
    /// JSExecution
    ///    ↓
    /// ApplyPendingMutations (runs BEFORE Measure)
    ///    ↓
    /// Invalidate (explicit)
    ///    ↓
    /// Measure → Layout → Paint
    /// 
    /// JS cannot observe partial state.
    /// </summary>
    public class DomMutationQueue
    {
        private readonly List<DomMutation> _pendingMutations = new List<DomMutation>();
        private readonly object _lock = new object();

        private static DomMutationQueue _instance;
        public static DomMutationQueue Instance => _instance ??= new DomMutationQueue();

        /// <summary>
        /// Accumulated invalidation kinds from pending mutations.
        /// </summary>
        public InvalidationKind PendingInvalidation { get; private set; } = InvalidationKind.None;

        /// <summary>
        /// Number of pending mutations.
        /// </summary>
        public int PendingCount
        {
            get { lock (_lock) { return _pendingMutations.Count; } }
        }

        /// <summary>
        /// Enqueue a mutation to be applied before the next layout.
        /// Called during JSExecution phase.
        /// </summary>
        public void EnqueueMutation(DomMutation mutation)
        {
            if (mutation == null) return;

            // Phase assertion: must be in JSExecution to enqueue mutations
            EnginePhaseManager.AssertNotInPhase(
                EnginePhase.Measure,
                EnginePhase.Layout,
                EnginePhase.Paint);

            lock (_lock)
            {
                _pendingMutations.Add(mutation);
                PendingInvalidation |= mutation.Invalidation;
            }
        }

        /// <summary>
        /// Apply all pending mutations and return the accumulated invalidation.
        /// Called BEFORE Measure phase begins.
        /// </summary>
        /// <param name="applyAction">Action to apply each mutation to the DOM.</param>
        /// <returns>Accumulated invalidation kinds requiring re-layout/repaint.</returns>
        public InvalidationKind ApplyPendingMutations(Action<DomMutation> applyAction)
        {
            // Phase assertion: must NOT be in layout phases when applying
            EnginePhaseManager.AssertNotInPhase(
                EnginePhase.Measure,
                EnginePhase.Layout,
                EnginePhase.Paint);

            List<DomMutation> mutations;
            InvalidationKind invalidation;

            lock (_lock)
            {
                mutations = new List<DomMutation>(_pendingMutations);
                invalidation = PendingInvalidation;
                _pendingMutations.Clear();
                PendingInvalidation = InvalidationKind.None;
            }

            // Apply mutations in order (deterministic)
            foreach (var mutation in mutations)
            {
                try
                {
                    applyAction?.Invoke(mutation);
                }
                catch
                {
                    // Mutation errors should not break the queue
                }
            }

            return invalidation;
        }

        /// <summary>
        /// Clear all pending mutations (called on navigation).
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _pendingMutations.Clear();
                PendingInvalidation = InvalidationKind.None;
            }
        }

        /// <summary>
        /// Check if there are pending mutations.
        /// </summary>
        public bool HasPendingMutations
        {
            get { lock (_lock) { return _pendingMutations.Count > 0; } }
        }
    }
}
