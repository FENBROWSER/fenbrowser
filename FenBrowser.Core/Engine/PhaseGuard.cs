// =============================================================================
// PhaseGuard.cs
// FenBrowser Pipeline Phase Guard
// 
// SPEC REFERENCE: Custom (no external spec - internal architecture)
// PURPOSE: Debug-mode guard that prevents reading "future" state
// 
// INVARIANT: No stage may read output from a stage that hasn't run yet.
// INVARIANT: DOM mutations forbidden during Layout, Paint, Raster stages.
// INVARIANT: Style changes forbidden during Layout, Paint, Raster stages.
// =============================================================================

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Static guard class for enforcing pipeline invariants.
    /// All assertions are compiled out in Release builds for performance.
    /// In Debug builds, violations cause immediate exceptions with full context.
    /// </summary>
    public static class PhaseGuard
    {
        #region Stage Guards

        /// <summary>
        /// Assert that DOM mutation is allowed in current stage.
        /// DOM mutations are forbidden during Layout, Paint, and Raster stages.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertDOMMutationAllowed(
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var stage = PipelineContext.Current.CurrentStage;
            if (stage == PipelineStage.Layout || 
                stage == PipelineStage.Painting || 
                stage == PipelineStage.Rasterizing)
            {
                FailFast($"DOM mutation forbidden during {stage} stage", caller, file, line);
            }
        }

        /// <summary>
        /// Assert that style mutation is allowed in current stage.
        /// Style mutations are forbidden during Layout, Paint, and Raster stages.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertStyleMutationAllowed(
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var stage = PipelineContext.Current.CurrentStage;
            if (stage == PipelineStage.Layout || 
                stage == PipelineStage.Painting || 
                stage == PipelineStage.Rasterizing)
            {
                FailFast($"Style mutation forbidden during {stage} stage", caller, file, line);
            }
        }

        /// <summary>
        /// Assert that layout data access is allowed in current stage.
        /// Layout data can only be read after Layout stage completes.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertLayoutDataAvailable(
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var stage = PipelineContext.Current.CurrentStage;
            if (stage.IsBefore(PipelineStage.Layout))
            {
                FailFast($"Layout data not yet available in {stage} stage", caller, file, line);
            }
        }

        /// <summary>
        /// Assert that paint data access is allowed in current stage.
        /// Paint data (display list) can only be read after Paint stage completes.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertPaintDataAvailable(
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var stage = PipelineContext.Current.CurrentStage;
            if (stage.IsBefore(PipelineStage.Painting))
            {
                FailFast($"Paint data not yet available in {stage} stage", caller, file, line);
            }
        }

        #endregion

        #region Forward Dependency Guards

        /// <summary>
        /// Assert that we are not reading data from a future stage.
        /// This is a critical invariant - forward dependencies break the pipeline.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertNoForwardDependency(
            PipelineStage dataFromStage,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var current = PipelineContext.Current.CurrentStage;
            if (current.IsBefore(dataFromStage))
            {
                FailFast(
                    $"Forward dependency detected: {current} stage is reading data from {dataFromStage} stage. " +
                    $"This breaks the pipeline invariant.", 
                    caller, file, line);
            }
        }

        /// <summary>
        /// Assert that we are in a specific stage.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertInStage(
            PipelineStage expected,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var current = PipelineContext.Current.CurrentStage;
            if (current != expected)
            {
                FailFast($"Expected stage {expected}, but current stage is {current}", caller, file, line);
            }
        }

        /// <summary>
        /// Assert that we are in one of the specified stages.
        /// </summary>
        [Conditional("DEBUG")]
        public static void AssertInStages(
            PipelineStage[] allowed,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var current = PipelineContext.Current.CurrentStage;
            foreach (var stage in allowed)
            {
                if (current == stage) return;
            }

            FailFast(
                $"Stage {current} not in allowed stages: {string.Join(", ", allowed)}", 
                caller, file, line);
        }

        /// <summary>
        /// Assert that we are NOT in the specified stage.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertNotInStage(
            PipelineStage forbidden,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var current = PipelineContext.Current.CurrentStage;
            if (current == forbidden)
            {
                FailFast($"Operation forbidden in {forbidden} stage", caller, file, line);
            }
        }

        #endregion

        #region Dirty Flag Guards

        /// <summary>
        /// Assert that style computation is not needed (style is clean).
        /// Use before reading style data to ensure it's up to date.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertStyleClean(
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (PipelineContext.Current.DirtyFlags.IsStyleDirty)
            {
                FailFast("Attempted to read stale style data (style is dirty)", caller, file, line);
            }
        }

        /// <summary>
        /// Assert that layout computation is not needed (layout is clean).
        /// Use before reading layout data to ensure it's up to date.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertLayoutClean(
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (PipelineContext.Current.DirtyFlags.IsLayoutDirty)
            {
                FailFast("Attempted to read stale layout data (layout is dirty)", caller, file, line);
            }
        }

        /// <summary>
        /// Assert that paint computation is not needed (paint is clean).
        /// Use before reading display list to ensure it's up to date.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertPaintClean(
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (PipelineContext.Current.DirtyFlags.IsPaintDirty)
            {
                FailFast("Attempted to read stale paint data (paint is dirty)", caller, file, line);
            }
        }

        #endregion

        #region General Assertions

        /// <summary>
        /// Assert a general condition.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Assert(
            bool condition,
            string message,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (!condition)
            {
                FailFast(message, caller, file, line);
            }
        }

        /// <summary>
        /// Assert that a value is not null.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertNotNull(
            object value,
            string name,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (value == null)
            {
                FailFast($"{name} is null", caller, file, line);
            }
        }

        #endregion

        #region Failure Handling

        /// <summary>
        /// Immediately fail with full diagnostic context.
        /// </summary>
        private static void FailFast(string message, string caller, string file, int line)
        {
            var pipelineContext = PipelineContext.Current;
            var engineContext = EngineContext.Current;

            var fullMessage = $"""
                PIPELINE INVARIANT VIOLATION
                ====================================
                Message: {message}
                
                Pipeline State:
                  Stage: {pipelineContext.CurrentStage}
                  Frame: {pipelineContext.FrameNumber}
                  Dirty: {pipelineContext.DirtyFlags}
                  Viewport: {pipelineContext.ViewportWidth}x{pipelineContext.ViewportHeight}
                
                Engine State:
                  Phase: {engineContext.CurrentPhase}
                  Pass: {engineContext.PassIndex}
                  Node: {engineContext.CurrentNodeTag}#{engineContext.CurrentNodeId}
                
                Location: {file}:{line}
                Caller: {caller}
                ====================================
                """;

            // Log to file
            try
            {
                System.IO.File.AppendAllText(
                    @"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt",
                    $"\r\n{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\r\n{fullMessage}\r\n");
            }
            catch { /* Ignore logging failures */ }

            // Throw with full context
            throw new PipelineStageException(fullMessage);
        }

        #endregion
    }
}
