using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Debug-only invariant assertions for engine correctness.
    /// All methods are compiled out in Release builds via [Conditional("DEBUG")].
    /// Violations fail fast in Debug mode with current engine context attached.
    /// </summary>
    public static class EngineInvariants
    {
        /// <summary>
        /// Assert that Core state is not mutated during Layout or Paint phases.
        /// Call this before any mutation that should be forbidden in these phases.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertCoreImmutable(
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var phase = EngineContext.Current.CurrentPhase;
            if (phase == EnginePhase.Layout || phase == EnginePhase.Paint)
            {
                FailFast($"Core state mutation forbidden during {phase} phase", caller, file, line);
            }
        }

        /// <summary>
        /// Assert that layout is side-effect free.
        /// Call this to verify no external state was modified during layout.
        /// </summary>
        [Conditional("DEBUG")]
        public static void AssertLayoutSideEffectFree(
            bool condition,
            string what,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (!condition)
            {
                FailFast($"Layout side-effect detected: {what}", caller, file, line);
            }
        }

        /// <summary>
        /// Assert that phase transitions match the engine's explicit transition matrix.
        /// </summary>
        [Conditional("DEBUG")]
        public static void AssertLinearPhaseTransition(
            EnginePhase from,
            EnginePhase to,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (!EngineContext.IsValidTransition(from, to))
            {
                FailFast($"Invalid phase transition: {from} -> {to}", caller, file, line);
            }
        }

        /// <summary>
        /// Assert that layout convergence is within iteration limit.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertConvergenceLimit(
            int currentPass,
            int maxPasses,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (currentPass > maxPasses)
            {
                throw new LayoutConvergenceException(currentPass, maxPasses);
            }
        }

        /// <summary>
        /// Assert that Host state is not mutated by Engine.
        /// </summary>
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AssertNoHostMutation(
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var phase = EngineContext.Current.CurrentPhase;
            if (phase != EnginePhase.Idle)
            {
                FailFast($"Host mutation forbidden during {phase} phase", caller, file, line);
            }
        }

        /// <summary>
        /// Assert that UI is not directly mutating Engine state.
        /// </summary>
        [Conditional("DEBUG")]
        public static void AssertNoUIToEngineMutation(
            string operation,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            FailFast($"UI -> Engine mutation forbidden: {operation}. Use Host as intermediary.", caller, file, line);
        }

        /// <summary>
        /// Assert a general condition with descriptive message.
        /// </summary>
        [Conditional("DEBUG")]
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

        private static void FailFast(string message, string caller, string file, int line)
        {
            var context = EngineContext.Current;
            var fullMessage = $"""
                ENGINE INVARIANT VIOLATION
                ===========================
                Message: {message}
                Phase: {context.CurrentPhase}
                Pass: {context.PassIndex}
                Node: {context.CurrentNodeTag}#{context.CurrentNodeId}
                Location: {file}:{line} in {caller}
                ===========================
                """;

            throw new EngineInvariantException(fullMessage);
        }
    }
}
