using System;
using System.Diagnostics;
using FenBrowser.Core.Engine; // Use Core definition and Context

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Manages engine phase transitions and enforces phase isolation.
    /// Acts as a facade over EngineContext to enforce JS-specific invariants.
    /// Proxy traps and observer callbacks must NOT execute during Measure, Layout, or Paint.
    /// </summary>
    public static class EnginePhaseManager
    {
        /// <summary>
        /// Current engine phase (delegates to EngineContext).
        /// </summary>
        public static EnginePhase CurrentPhase => EngineContext.Current.CurrentPhase;

        /// <summary>
        /// Enter a new engine phase (delegates to EngineContext).
        /// </summary>
        public static void EnterPhase(EnginePhase phase)
        {
            EngineContext.Current.BeginPhase(phase);
        }

        /// <summary>
        /// Attempt to return to Idle phase (e.g. after task execution).
        /// </summary>
        public static void TryEnterIdle()
        {
            EngineContext.Current.BeginPhase(EnginePhase.Idle);
        }

        /// <summary>
        /// Assert that the current phase is NOT one of the forbidden phases.
        /// In Debug builds, this will throw an exception if violated.
        /// In Release builds, it logs a warning but continues.
        /// </summary>
        /// <param name="forbidden">Phases that are forbidden for the current operation.</param>
        [Conditional("DEBUG")]
        public static void AssertNotInPhase(params EnginePhase[] forbidden)
        {
            var current = CurrentPhase;
            foreach (var phase in forbidden)
            {
                if (current == phase)
                {
                    var message = $"[EnginePhaseManager] VIOLATION: Operation attempted during {phase} phase. " +
                                  "JavaScript execution is only permitted during JSExecution phase.";
                    
                    // In debug builds, crash immediately
                    Debug.Fail(message);
                    throw new InvalidOperationException(message);
                }
            }
        }

        /// <summary>
        /// Check if we're currently in the JS Execution Window (safe for JS operations).
        /// </summary>
        public static bool IsInJSExecutionWindow => CurrentPhase == EnginePhase.JSExecution;

        /// <summary>
        /// Check if we're in a layout-sensitive phase (Measure, Layout, or Paint).
        /// </summary>
        public static bool IsInLayoutPhase => CurrentPhase == EnginePhase.Measure ||
                                               CurrentPhase == EnginePhase.Layout ||
                                               CurrentPhase == EnginePhase.Paint;
    }
}
