// =============================================================================
// PipelineContext.cs
// FenBrowser Rendering Pipeline Context
// 
// SPEC REFERENCE: Custom (no external spec - internal architecture)
// PURPOSE: Central state container for the rendering pipeline
// 
// DESIGN PRINCIPLES:
//   1. Single source of truth for pipeline state
//   2. Immutable snapshots for each stage output
//   3. Forward-only dirty flag propagation
//   4. Phase guard enforcement
// =============================================================================

using System;
using FenBrowser.Core.Dom.V2;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Central context for the rendering pipeline.
    /// Tracks current stage, dirty flags, and immutable snapshots from each stage.
    /// Thread-local singleton for render thread isolation.
    /// </summary>
    public sealed class PipelineContext
    {
        [ThreadStatic]
        private static PipelineContext _instance;

        /// <summary>
        /// Gets the thread-local pipeline context instance.
        /// </summary>
        public static PipelineContext Current => _instance ??= new PipelineContext();

        // Current pipeline stage
        private PipelineStage _currentStage = PipelineStage.Idle;

        // Dirty flags with forward propagation
        private readonly DirtyFlags _dirtyFlags = new DirtyFlags();

        // Frame counter
        private long _frameNumber = 0;

        // Timing information
        private DateTime _frameStartTime;
        private DateTime _currentStageStartTime;
        private readonly Dictionary<PipelineStage, TimeSpan> _stageTimes = new();

        // Viewport dimensions (needed for layout)
        private float _viewportWidth;
        private float _viewportHeight;

        // Immutable snapshots from each stage
        private PipelineSnapshot _styleSnapshot;
        private PipelineSnapshot _layoutSnapshot;
        private PipelineSnapshot _paintSnapshot;

        private PipelineContext() { }

        #region Properties

        /// <summary>
        /// Current pipeline stage.
        /// </summary>
        public PipelineStage CurrentStage => _currentStage;

        /// <summary>
        /// Dirty flags for incremental updates.
        /// </summary>
        public DirtyFlags DirtyFlags => _dirtyFlags;

        /// <summary>
        /// Current frame number (increments each render cycle).
        /// </summary>
        public long FrameNumber => Interlocked.Read(ref _frameNumber);

        /// <summary>
        /// Viewport width in pixels.
        /// </summary>
        public float ViewportWidth => _viewportWidth;

        /// <summary>
        /// Viewport height in pixels.
        /// </summary>
        public float ViewportHeight => _viewportHeight;

        /// <summary>
        /// Time when current frame started.
        /// </summary>
        public DateTime FrameStartTime => _frameStartTime;

        /// <summary>
        /// Duration of current frame so far.
        /// </summary>
        public TimeSpan FrameDuration => DateTime.UtcNow - _frameStartTime;

        #endregion

        #region Stage Transitions

        /// <summary>
        /// Begin a new pipeline stage.
        /// </summary>
        /// <exception cref="PipelineStageException">Thrown if transition is invalid.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginStage(PipelineStage stage)
        {
            ValidateStageTransition(_currentStage, stage);
            _currentStage = stage;
            _currentStageStartTime = DateTime.UtcNow;

#if TRACE_PIPELINE
            FenBrowser.Core.EngineLogCompat.Debug(
                $"[Pipeline] Stage: {stage.GetDescription()}", 
                Logging.LogCategory.Rendering);
#endif
        }

        /// <summary>
        /// End current stage and record timing.
        /// </summary>
        public void EndStage()
        {
            if (_currentStage != PipelineStage.Idle)
            {
                var stageStart = _currentStageStartTime == default ? _frameStartTime : _currentStageStartTime;
                _stageTimes[_currentStage] = DateTime.UtcNow - stageStart;
            }
            // Note: Don't reset to Idle here - let BeginStage handle transitions
        }

        /// <summary>
        /// Begin a new frame. Resets frame state and increments frame counter.
        /// </summary>
        public void BeginFrame()
        {
            Interlocked.Increment(ref _frameNumber);
            _frameStartTime = DateTime.UtcNow;
            _currentStageStartTime = _frameStartTime;
            _stageTimes.Clear();
            _currentStage = PipelineStage.Idle;
        }

        /// <summary>
        /// End the current frame.
        /// </summary>
        public void EndFrame()
        {
            _currentStage = PipelineStage.Idle;
        }

        /// <summary>
        /// Begin a new frame and return a scope that always ends the frame.
        /// </summary>
        public PipelineFrameScope BeginScopedFrame()
        {
            BeginFrame();
            return new PipelineFrameScope(this);
        }

        /// <summary>
        /// Begin a stage and return a scope that always ends the stage.
        /// </summary>
        public PipelineStageScope BeginScopedStage(PipelineStage stage)
        {
            BeginStage(stage);
            return new PipelineStageScope(this);
        }

        /// <summary>
        /// Validates that a stage transition is legal.
        /// </summary>
        private static void ValidateStageTransition(PipelineStage from, PipelineStage to)
        {
            // Allow Idle -> any stage
            if (from == PipelineStage.Idle)
                return;

            // Allow any stage -> Idle
            if (to == PipelineStage.Idle)
                return;

            // Allow forward transitions only (or same stage for re-entry)
            if (to >= from)
                return;

            // HTML spec requires tokenizer and tree builder to interleave:
            // the tree builder switches the tokenizer state for <script>, <style>,
            // <textarea>, etc.  Allow Parsing -> Tokenizing transitions.
            if (from == PipelineStage.Parsing && to == PipelineStage.Tokenizing)
                return;

            // Backward transitions are forbidden
            throw new PipelineStageException(
                $"Invalid pipeline stage transition: {from} -> {to}. " +
                $"Pipeline stages must proceed forward only.");
        }

        #endregion

        #region Viewport

        /// <summary>
        /// Set viewport dimensions. Marks layout dirty if changed.
        /// </summary>
        public void SetViewport(float width, float height)
        {
            if (Math.Abs(_viewportWidth - width) > 0.01f || 
                Math.Abs(_viewportHeight - height) > 0.01f)
            {
                _viewportWidth = width;
                _viewportHeight = height;
                _dirtyFlags.InvalidateLayout();
            }
        }

        #endregion

        #region Snapshots

        /// <summary>
        /// Store a snapshot from the style stage.
        /// </summary>
        public void SetStyleSnapshot(IDictionary<Node, CssComputed> styles)
        {
            _styleSnapshot = new PipelineSnapshot(
                PipelineStage.Styling,
                _frameNumber,
                _dirtyFlags.Generation,
                styles
            );
            _dirtyFlags.ClearStyleDirty();
        }

        /// <summary>
        /// Store a snapshot from the layout stage.
        /// </summary>
        public void SetLayoutSnapshot(object layoutTree)
        {
            _layoutSnapshot = new PipelineSnapshot(
                PipelineStage.Layout,
                _frameNumber,
                _dirtyFlags.Generation,
                layoutTree
            );
            _dirtyFlags.ClearLayoutDirty();
        }

        /// <summary>
        /// Store a snapshot from the paint stage.
        /// </summary>
        public void SetPaintSnapshot(object displayList)
        {
            _paintSnapshot = new PipelineSnapshot(
                PipelineStage.Painting,
                _frameNumber,
                _dirtyFlags.Generation,
                displayList
            );
            _dirtyFlags.ClearPaintDirty();
        }

        /// <summary>
        /// Get the style snapshot. Throws if accessed from wrong stage.
        /// </summary>
        public PipelineSnapshot GetStyleSnapshot()
        {
            AssertCanRead(PipelineStage.Styling);
            return _styleSnapshot;
        }

        /// <summary>
        /// Get the layout snapshot. Throws if accessed from wrong stage.
        /// </summary>
        public PipelineSnapshot GetLayoutSnapshot()
        {
            AssertCanRead(PipelineStage.Layout);
            return _layoutSnapshot;
        }

        /// <summary>
        /// Get the paint snapshot. Throws if accessed from wrong stage.
        /// </summary>
        public PipelineSnapshot GetPaintSnapshot()
        {
            AssertCanRead(PipelineStage.Painting);
            return _paintSnapshot;
        }

        /// <summary>
        /// Assert that current stage can read from the specified snapshot stage.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AssertCanRead(PipelineStage snapshotStage)
        {
            // Can only read from stages that have already completed
            if (_currentStage.IsBefore(snapshotStage) || _currentStage == snapshotStage)
            {
                throw new PipelineStageException(
                    $"Cannot read {snapshotStage} snapshot during {_currentStage} stage. " +
                    $"Snapshots can only be read from completed previous stages.");
            }
        }

        #endregion

        #region Phase Guard

        /// <summary>
        /// Assert that current stage is not reading "future" data.
        /// Call this when accessing data that should only be available after a certain stage.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AssertNotReadingFuture(PipelineStage dataAvailableAfter)
        {
            if (_currentStage.IsBefore(dataAvailableAfter))
            {
                throw new PipelineStageException(
                    $"Attempted to read data available after {dataAvailableAfter} during {_currentStage} stage. " +
                    $"This is a forward-dependency violation.");
            }
        }

        /// <summary>
        /// Assert that we are in one of the specified stages.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AssertInStage(params PipelineStage[] allowedStages)
        {
            foreach (var stage in allowedStages)
            {
                if (_currentStage == stage) return;
            }

            throw new PipelineStageException(
                $"Operation not allowed in {_currentStage} stage. " +
                $"Allowed stages: {string.Join(", ", allowedStages)}");
        }

        /// <summary>
        /// Assert that we are NOT in any of the specified stages.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AssertNotInStage(params PipelineStage[] forbiddenStages)
        {
            foreach (var stage in forbiddenStages)
            {
                if (_currentStage == stage)
                {
                    throw new PipelineStageException(
                        $"Operation forbidden in {_currentStage} stage.");
                }
            }
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// Get timing information for all stages in current frame.
        /// </summary>
        public IReadOnlyDictionary<PipelineStage, TimeSpan> GetStageTimes()
        {
            return _stageTimes;
        }

        /// <summary>
        /// Get diagnostic prefix for logging.
        /// </summary>
        public string DiagnosticPrefix => 
            $"[Frame={_frameNumber}][Stage={_currentStage}][{_dirtyFlags}]";

        /// <summary>
        /// Reset context for testing.
        /// </summary>
        public static void Reset()
        {
            _instance = new PipelineContext();
        }

        public override string ToString()
        {
            return $"PipelineContext: Frame={_frameNumber}, Stage={_currentStage}, " +
                   $"Viewport={_viewportWidth}x{_viewportHeight}, {_dirtyFlags}";
        }

        #endregion
    }

    /// <summary>
    /// Scope helper that guarantees EndFrame() even on exceptions.
    /// </summary>
    public sealed class PipelineFrameScope : IDisposable
    {
        private PipelineContext _context;

        public PipelineFrameScope(PipelineContext context)
        {
            _context = context;
        }

        public void Dispose()
        {
            var context = Interlocked.Exchange(ref _context, null);
            if (context != null)
            {
                context.EndFrame();
            }
        }
    }

    /// <summary>
    /// Scope helper that guarantees EndStage() even on exceptions.
    /// </summary>
    public sealed class PipelineStageScope : IDisposable
    {
        private PipelineContext _context;

        public PipelineStageScope(PipelineContext context)
        {
            _context = context;
        }

        public void Dispose()
        {
            var context = Interlocked.Exchange(ref _context, null);
            if (context != null)
            {
                context.EndStage();
            }
        }
    }

    /// <summary>
    /// Immutable snapshot of pipeline stage output.
    /// </summary>
    public sealed class PipelineSnapshot
    {
        /// <summary>
        /// Stage that produced this snapshot.
        /// </summary>
        public PipelineStage Stage { get; }

        /// <summary>
        /// Frame number when snapshot was created.
        /// </summary>
        public long FrameNumber { get; }

        /// <summary>
        /// Dirty flags generation when snapshot was created.
        /// </summary>
        public long Generation { get; }

        /// <summary>
        /// The snapshot data (type depends on stage).
        /// </summary>
        public object Data { get; }

        /// <summary>
        /// Timestamp when snapshot was created.
        /// </summary>
        public DateTime Timestamp { get; }

        public PipelineSnapshot(PipelineStage stage, long frameNumber, long generation, object data)
        {
            Stage = stage;
            FrameNumber = frameNumber;
            Generation = generation;
            Data = data;
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Get typed data from snapshot.
        /// </summary>
        public T GetData<T>()
        {
            if (Data is T typed)
                return typed;
                
            throw new InvalidCastException(
                $"Snapshot data is {Data?.GetType().Name ?? "null"}, not {typeof(T).Name}");
        }

        public override string ToString()
        {
            return $"Snapshot[{Stage}, Frame={FrameNumber}, Gen={Generation}]";
        }
    }

    /// <summary>
    /// Exception thrown when pipeline stage invariants are violated.
    /// </summary>
    public class PipelineStageException : EngineInvariantException
    {
        public PipelineStage? CurrentStage { get; }
        public PipelineStage? AttemptedStage { get; }

        public PipelineStageException(string message) : base(message)
        {
            CurrentStage = PipelineContext.Current?.CurrentStage;
        }

        public PipelineStageException(string message, PipelineStage current, PipelineStage attempted) 
            : base(message)
        {
            CurrentStage = current;
            AttemptedStage = attempted;
        }
    }
}
