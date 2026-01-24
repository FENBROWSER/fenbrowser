using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Tracks the remaining time for the current frame/pass.
    /// Provides a mechanism to check if a deadline has been exceeded.
    /// </summary>
    public sealed class RenderDeadline
    {
        private readonly DateTime _deadline;
        private readonly string _phase;
        
        public RenderDeadline(double budgetMs, string phase = "Rendering")
        {
            _deadline = DateTime.Now.AddMilliseconds(budgetMs);
            _phase = phase;
        }

        public bool IsExceeded => DateTime.Now >= _deadline;

        /// <summary>
        /// Throws a DeadlineExceededException if the time budget is exhausted.
        /// </summary>
        public void Check()
        {
            if (IsExceeded)
            {
                FenLogger.Warn($"[RenderDeadline] Budget exceeded for phase: {_phase}", LogCategory.Performance);
                throw new DeadlineExceededException(_phase);
            }
        }
        
        public TimeSpan Remaining => _deadline - DateTime.Now;
    }

    /// <summary>
    /// Exception thrown when a render deadline is exceeded.
    /// </summary>
    public class DeadlineExceededException : Exception
    {
        public string Phase { get; }
        public DeadlineExceededException(string phase) : base($"Deadline exceeded for {phase}") { Phase = phase; }
    }
}
