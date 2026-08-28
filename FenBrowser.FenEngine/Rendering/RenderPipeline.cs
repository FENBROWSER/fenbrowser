// SpecRef: FenBrowser pipeline stage authority contract (parse -> DOM -> style -> layout -> paint -> raster)
// CapabilityId: PIPELINE-STAGE-AUTHORITY-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Logging;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    public enum RenderPhase
    {
        Idle,
        Layout,
        LayoutFrozen,
        Paint,
        Composite,
        Present
    }

    public static class RenderPipeline
    {
        [ThreadStatic]
        private static RenderPipelineState t_state;

        private static readonly ConditionalWeakTable<object, RenderMilestoneState> s_documentMilestones = new();

        public static bool StrictInvariants { get; set; } = true;
        public static TimeSpan FrameBudget { get; set; } = TimeSpan.FromMilliseconds(16.67);

        public static RenderPhase CurrentPhase
        {
            get
            {
                var state = GetState();
                lock (state.SyncRoot)
                {
                    return state.CurrentPhase;
                }
            }
        }

        public static long FrameSequence
        {
            get
            {
                var state = GetState();
                lock (state.SyncRoot)
                {
                    return state.FrameSequence;
                }
            }
        }

        public static TimeSpan LastFrameDuration
        {
            get
            {
                var state = GetState();
                lock (state.SyncRoot)
                {
                    return state.LastFrameDuration;
                }
            }
        }

        public static bool LastFrameExceededBudget
        {
            get
            {
                var state = GetState();
                lock (state.SyncRoot)
                {
                    return state.LastFrameDuration > FrameBudget;
                }
            }
        }

        public static void Reset()
        {
            var state = GetState();
            lock (state.SyncRoot)
            {
                state.CurrentPhase = RenderPhase.Idle;
                state.FrameStartedTimestamp = 0;
                state.LastFrameDuration = TimeSpan.Zero;
                state.FirstLayoutLogged = false;
                state.FirstPaintLogged = false;
                state.OwnerThreadId = 0;
            }
        }

        public static void EnterLayout()
        {
            var state = GetState();
            lock (state.SyncRoot)
            {
                EnsureThreadAffinity(state, nameof(EnterLayout), acquireIfUnclaimed: true);
                RequirePhase(state, RenderPhase.Idle, nameof(EnterLayout));
                state.FrameSequence++;
                state.FrameStartedTimestamp = Stopwatch.GetTimestamp();
                state.CurrentPhase = RenderPhase.Layout;
            }
        }

        public static void EndLayout(object milestoneOwner = null)
        {
            var state = GetState();
            lock (state.SyncRoot)
            {
                EnsureThreadAffinity(state, nameof(EndLayout));
                RequirePhase(state, RenderPhase.Layout, nameof(EndLayout));
                state.CurrentPhase = RenderPhase.LayoutFrozen;
                LogFirstLayout(state, milestoneOwner);
            }
        }

        public static void EnterPaint()
        {
            var state = GetState();
            lock (state.SyncRoot)
            {
                EnsureThreadAffinity(state, nameof(EnterPaint));
                RequirePhase(state, RenderPhase.LayoutFrozen, nameof(EnterPaint));
                state.CurrentPhase = RenderPhase.Paint;
            }
        }

        public static void EndPaint(object milestoneOwner = null)
        {
            var state = GetState();
            lock (state.SyncRoot)
            {
                EnsureThreadAffinity(state, nameof(EndPaint));
                RequirePhase(state, RenderPhase.Paint, nameof(EndPaint));
                state.CurrentPhase = RenderPhase.Composite;
                LogFirstPaint(state, milestoneOwner);
            }
        }

        public static void EnterPresent()
        {
            var state = GetState();
            lock (state.SyncRoot)
            {
                EnsureThreadAffinity(state, nameof(EnterPresent));
                RequirePhase(state, RenderPhase.Composite, nameof(EnterPresent));
                state.CurrentPhase = RenderPhase.Present;
            }
        }

        public static void EndFrame()
        {
            var state = GetState();
            lock (state.SyncRoot)
            {
                EnsureThreadAffinity(state, nameof(EndFrame));
                RequirePhase(state, RenderPhase.Present, nameof(EndFrame));
                if (state.FrameStartedTimestamp != 0)
                {
                    state.LastFrameDuration = Stopwatch.GetElapsedTime(state.FrameStartedTimestamp);
                    EngineLogCompat.Debug(
                        $"[PIPELINE][SUMMARY] frame={state.FrameSequence} durationMs={state.LastFrameDuration.TotalMilliseconds:F2} phase={state.CurrentPhase}",
                        LogCategory.Rendering);
                    if (state.LastFrameDuration > FrameBudget)
                    {
                        EngineLogCompat.Warn($"[PIPELINE] Frame {state.FrameSequence} exceeded budget: {state.LastFrameDuration.TotalMilliseconds:F2}ms > {FrameBudget.TotalMilliseconds:F2}ms", LogCategory.Performance);
                    }
                }

                state.CurrentPhase = RenderPhase.Idle;
                state.FrameStartedTimestamp = 0;
                state.OwnerThreadId = 0;
            }
        }

        public static void AssertPhase(RenderPhase expected)
        {
            var state = GetState();
            lock (state.SyncRoot)
            {
                if (state.CurrentPhase != expected)
                {
                    HandleViolation(state, $"AssertPhase failed. Expected {expected}, actual {state.CurrentPhase}", null);
                }
            }
        }

        public static void AssertNotPhase(RenderPhase forbidden)
        {
            var state = GetState();
            lock (state.SyncRoot)
            {
                if (state.CurrentPhase == forbidden)
                {
                    HandleViolation(state, $"AssertNotPhase failed. Forbidden phase {forbidden} is active.", null);
                }
            }
        }

        public static void AssertLayerSeparation(bool isDebugOrOverlay)
        {
            var state = GetState();
            lock (state.SyncRoot)
            {
                if (isDebugOrOverlay && state.CurrentPhase != RenderPhase.Composite && state.CurrentPhase != RenderPhase.Present)
                {
                    HandleViolation(state, $"Debug/overlay drawing must happen in Composite/Present. Actual: {state.CurrentPhase}", null);
                }
            }
        }

        private static RenderPipelineState GetState() => t_state ??= new RenderPipelineState();

        private static void LogFirstLayout(RenderPipelineState state, object milestoneOwner)
        {
            if (milestoneOwner == null)
            {
                if (state.FirstLayoutLogged) return;
                state.FirstLayoutLogged = true;
                EngineLogCompat.Info("[DOC][INFO] First layout complete", LogCategory.Layout);
                return;
            }

            var milestones = s_documentMilestones.GetValue(milestoneOwner, static _ => new RenderMilestoneState());
            lock (milestones.SyncRoot)
            {
                if (milestones.FirstLayoutLogged) return;
                milestones.FirstLayoutLogged = true;
                EngineLogCompat.Info("[DOC][INFO] First layout complete", LogCategory.Layout);
            }
        }

        private static void LogFirstPaint(RenderPipelineState state, object milestoneOwner)
        {
            if (milestoneOwner == null)
            {
                if (state.FirstPaintLogged) return;
                state.FirstPaintLogged = true;
                EngineLogCompat.Info("[DOC][INFO] First paint submitted", LogCategory.Paint);
                return;
            }

            var milestones = s_documentMilestones.GetValue(milestoneOwner, static _ => new RenderMilestoneState());
            lock (milestones.SyncRoot)
            {
                if (milestones.FirstPaintLogged) return;
                milestones.FirstPaintLogged = true;
                EngineLogCompat.Info("[DOC][INFO] First paint submitted", LogCategory.Paint);
            }
        }

        private static void RequirePhase(RenderPipelineState state, RenderPhase expected, string operation)
        {
            if (state.CurrentPhase != expected)
            {
                HandleViolation(state, $"{operation} requires phase {expected}, actual {state.CurrentPhase}", expected);
            }
        }

        private static void EnsureThreadAffinity(RenderPipelineState state, string operation, bool acquireIfUnclaimed = false)
        {
            var currentThreadId = Environment.CurrentManagedThreadId;
            if (state.OwnerThreadId == 0)
            {
                if (acquireIfUnclaimed)
                {
                    state.OwnerThreadId = currentThreadId;
                    return;
                }

                HandleViolation(
                    state,
                    $"{operation} called on thread {currentThreadId} without an active frame owner.",
                    null);
                return;
            }

            if (state.OwnerThreadId == currentThreadId)
            {
                return;
            }

            HandleViolation(
                state,
                $"{operation} called on thread {currentThreadId}, but active frame is owned by thread {state.OwnerThreadId}",
                null);
        }

        private static void HandleViolation(RenderPipelineState state, string message, RenderPhase? recoverTo)
        {
            if (StrictInvariants)
            {
                throw new RenderPipelineInvariantException(message);
            }

            EngineLogCompat.Warn($"[PIPELINE RECOVERY] {message}", LogCategory.Rendering);
            if (recoverTo.HasValue)
            {
                state.CurrentPhase = recoverTo.Value;
            }
        }

        private sealed class RenderPipelineState
        {
            public object SyncRoot { get; } = new object();
            public RenderPhase CurrentPhase { get; set; } = RenderPhase.Idle;
            public long FrameSequence { get; set; }
            public long FrameStartedTimestamp { get; set; }
            public TimeSpan LastFrameDuration { get; set; }
            public bool FirstLayoutLogged { get; set; }
            public bool FirstPaintLogged { get; set; }
            public int OwnerThreadId { get; set; }
        }

        private sealed class RenderMilestoneState
        {
            public object SyncRoot { get; } = new object();
            public bool FirstLayoutLogged { get; set; }
            public bool FirstPaintLogged { get; set; }
        }
    }

    public sealed class RenderPipelineInvariantException : InvalidOperationException
    {
        public RenderPipelineInvariantException(string message) : base(message)
        {
        }
    }
}
