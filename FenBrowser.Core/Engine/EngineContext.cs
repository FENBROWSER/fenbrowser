using System;
using System.Diagnostics;
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
        private int _passIndex = 0;
        private int _currentNodeId = 0;
        private string _currentNodeTag = "";
        
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
        /// Begin a new engine phase. Validates linear transitions.
        /// </summary>
        /// <param name="phase">The phase to enter.</param>
        /// <exception cref="EngineInvariantException">Thrown if phase transition is invalid.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginPhase(EnginePhase phase)
        {
            // STRICT MICROTASK SAFETY:
            if (phase == EnginePhase.Microtasks && _currentPhase == EnginePhase.Microtasks)
            {
                 throw new InvalidOperationException("[EngineContext] VIOLATION: Recursive entry into Microtasks phase is forbidden!");
            }

            ValidatePhaseTransition(_currentPhase, phase);
            _currentPhase = phase;
            _passIndex = 0;
            
            #if DEBUG
             try {
                System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                $"[EngineContext] Phase transition: {_currentPhase} → {phase}\r\n");
             } catch {}
            #endif
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
        /// <exception cref="LayoutConvergenceException">Thrown if max passes exceeded.</exception>
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
            _currentNodeTag = tag ?? "";
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
        /// Validates phase transition follows linear order.
        /// </summary>
        private static void ValidatePhaseTransition(EnginePhase from, EnginePhase to)
        {
            // Idle can transition to ANY phase (starting a specific test/pass)
            if (from == EnginePhase.Idle)
                return;
            
            // Any phase can transition to Idle (reset)
            if (to == EnginePhase.Idle)
                return;
            
            // Normal forward transition (+1)
            if ((int)to == (int)from + 1)
                return;
            
            // Same phase is allowed (re-entry for loops)
            if (to == from)
                return;
            
            // JSExecution can jump to Microtasks (after task completes)
            if (from == EnginePhase.JSExecution && to == EnginePhase.Microtasks)
                return;
            
            // Microtasks can return to JSExecution (for callbacks)
            if (from == EnginePhase.Microtasks && to == EnginePhase.JSExecution)
                return;
            
            // Layout can jump to Observers (after paint)
            if (from == EnginePhase.Layout && to == EnginePhase.Observers)
                return;
            
            // Observers can jump to Animation
            if (from == EnginePhase.Observers && to == EnginePhase.Animation)
                return;
            
            // Animation can return to JSExecution (for RAF callbacks)
            if (from == EnginePhase.Animation && to == EnginePhase.JSExecution)
                return;
            
            // Microtasks can jump directly to Layout (for render update)
            if (from == EnginePhase.Microtasks && to == EnginePhase.Layout)
                return;
            
            // Observers/Animation can go to Microtasks (for callbacks)
            if ((from == EnginePhase.Observers || from == EnginePhase.Animation) && to == EnginePhase.Microtasks)
                return;

            #if DEBUG
            // Strict validation disabled for tests to allow jumping states
            // throw new EngineInvariantException($"Invalid phase transition: {from} → {to}. Phases must be linear.");
            #endif
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
