using System;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Tracks current engine phase, pass index, and provides invariant guards.
    /// Thread-static singleton for render thread isolation.
    /// </summary>
    public sealed class EngineContext
    {
        [ThreadStatic]
        private static EngineContext _instance;

        /// <summary>
        /// Gets the thread-local engine context instance.
        /// </summary>
        public static EngineContext Current => _instance ??= new EngineContext();

        private EnginePhase _currentPhase = EnginePhase.Idle;
        private int _passIndex;
        private int _currentNodeId;
        private string _currentNodeTag = string.Empty;

        /// <summary>Maximum allowed layout passes before convergence failure.</summary>
        public const int MaxLayoutPasses = 10;

        /// <summary>Current engine phase.</summary>
        public EnginePhase CurrentPhase => _currentPhase;

        /// <summary>Current pass index (for layout convergence loops).</summary>
        public int PassIndex => _passIndex;

        /// <summary>Current node being processed (for diagnostics).</summary>
        public int CurrentNodeId => _currentNodeId;

        /// <summary>Current node tag (for diagnostics).</summary>
        public string CurrentNodeTag => _currentNodeTag;

        private EngineContext() { }

        /// <summary>
        /// Begin a new engine phase. Validates explicit transitions.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginPhase(EnginePhase phase)
        {
            if (phase == EnginePhase.Microtasks && _currentPhase == EnginePhase.Microtasks)
                throw new InvalidOperationException("[EngineContext] VIOLATION: Recursive entry into Microtasks phase is forbidden!");

            ValidatePhaseTransition(_currentPhase, phase);
            _currentPhase = phase;
            _passIndex = 0;
        }

        /// <summary>
        /// Temporarily switch phases and restore the previous phase on dispose.
        /// Intended for tightly bounded callbacks such as observer delivery.
        /// </summary>
        public EnginePhaseScope PushPhase(EnginePhase phase)
        {
            var previousPhase = _currentPhase;
            ValidateScopedTransition(previousPhase, phase);
            _currentPhase = phase;
            _passIndex = 0;
            return new EnginePhaseScope(this, previousPhase);
        }

        /// <summary>
        /// End current phase and return to Idle.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndPhase()
        {
            _currentPhase = EnginePhase.Idle;
            _passIndex = 0;
        }

        /// <summary>
        /// Reset context to initial state (for testing).
        /// </summary>
        public static void Reset()
        {
            _instance = new EngineContext();
        }

        /// <summary>
        /// Increment pass index (for layout convergence loops).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncrementPass()
        {
            _passIndex++;
            EngineInvariants.AssertConvergenceLimit(_passIndex, MaxLayoutPasses);
        }

        /// <summary>
        /// Set current node for diagnostics.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetCurrentNode(int nodeId, string tag)
        {
            _currentNodeId = nodeId;
            _currentNodeTag = tag ?? string.Empty;
        }

        /// <summary>
        /// Format diagnostic prefix for logging.
        /// </summary>
        public string DiagnosticPrefix => $"[{_currentPhase}][Pass={_passIndex}][Node={_currentNodeTag}#{_currentNodeId}]";

        /// <summary>
        /// Validates that the engine is NOT in any of the specified phases.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AssertNotInPhase(params EnginePhase[] forbiddenPhases)
        {
            var current = _currentPhase;
            for (int i = 0; i < forbiddenPhases.Length; i++)
            {
                if (current == forbiddenPhases[i])
                    throw new InvalidOperationException($"Operation forbidden in phase {current}. Current Pass={_passIndex}");
            }
        }

        /// <summary>
        /// Validates phase transition follows explicit engine ordering.
        /// </summary>
        private static void ValidatePhaseTransition(EnginePhase from, EnginePhase to)
        {
            if (IsValidTransition(from, to))
                return;

            throw new EngineInvariantException($"Invalid phase transition: {from} -> {to}. Phases must be explicit.");
        }

        private static void ValidateScopedTransition(EnginePhase from, EnginePhase to)
        {
            if (IsValidTransition(from, to, allowScopedTransitions: true))
                return;

            throw new EngineInvariantException($"Invalid scoped phase transition: {from} -> {to}. Scoped transitions must be explicit.");
        }

        internal void RestorePhase(EnginePhase phase)
        {
            _currentPhase = phase;
            _passIndex = 0;
        }

        internal static bool IsValidTransition(EnginePhase from, EnginePhase to, bool allowScopedTransitions = false)
        {
            if (from == EnginePhase.Idle || to == EnginePhase.Idle)
                return true;

            if ((int)to == (int)from + 1)
                return true;

            if (to == from)
                return true;

            if (from == EnginePhase.JSExecution && to == EnginePhase.Microtasks)
                return true;

            if (from == EnginePhase.Microtasks && to == EnginePhase.JSExecution)
                return true;

            if (from == EnginePhase.Layout && to == EnginePhase.Observers)
                return true;

            if (from == EnginePhase.Observers && to == EnginePhase.Animation)
                return true;

            if (from == EnginePhase.Animation && to == EnginePhase.JSExecution)
                return true;

            if (from == EnginePhase.Microtasks && to == EnginePhase.Layout)
                return true;

            if ((from == EnginePhase.Observers || from == EnginePhase.Animation) && to == EnginePhase.Microtasks)
                return true;

            if (allowScopedTransitions &&
                to == EnginePhase.JSExecution &&
                (from == EnginePhase.Measure || from == EnginePhase.Layout || from == EnginePhase.Paint ||
                 from == EnginePhase.Observers || from == EnginePhase.Animation))
            {
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Restores the previous engine phase when disposed.
    /// </summary>
    public sealed class EnginePhaseScope : IDisposable
    {
        private EngineContext _context;
        private readonly EnginePhase _previousPhase;

        internal EnginePhaseScope(EngineContext context, EnginePhase previousPhase)
        {
            _context = context;
            _previousPhase = previousPhase;
        }

        public void Dispose()
        {
            var context = _context;
            if (context == null)
                return;

            _context = null;
            context.RestorePhase(_previousPhase);
        }
    }

    /// <summary>
    /// Exception thrown when engine invariants are violated.
    /// </summary>
    public class EngineInvariantException : Exception
    {
        public EngineInvariantException(string message) : base(message) { }
        public EngineInvariantException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Exception thrown when layout fails to converge within iteration limit.
    /// </summary>
    public class LayoutConvergenceException : EngineInvariantException
    {
        public int PassCount { get; }
        public int MaxPasses { get; }

        public LayoutConvergenceException(int passCount, int maxPasses)
            : base($"Layout failed to converge after {passCount} passes (max: {maxPasses})")
        {
            PassCount = passCount;
            MaxPasses = maxPasses;
        }
    }
}
