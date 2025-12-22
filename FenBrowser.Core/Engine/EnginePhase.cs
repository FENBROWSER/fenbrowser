namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Defines render engine phases for invariant enforcement and diagnostics.
    /// Phases MUST transition linearly: Idle → Style → Measure → Layout → Paint → Idle
    /// </summary>
    public enum EnginePhase
    {
        /// <summary>No rendering active. Between frames.</summary>
        Idle = 0,
        
        /// <summary>CSS cascade and computed style resolution.</summary>
        Style = 1,
        
        /// <summary>Text measurement and intrinsic sizing.</summary>
        Measure = 2,
        
        /// <summary>Box model calculation and positioning. May run multiple passes.</summary>
        Layout = 3,
        
        /// <summary>Drawing to canvas. Final phase before Idle.</summary>
        Paint = 4,

        /// <summary>JavaScript execution window (Observer callbacks, events).</summary>
        JSExecution = 5
    }
}
