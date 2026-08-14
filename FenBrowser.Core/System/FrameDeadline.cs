using System;
using System.Diagnostics;

namespace FenBrowser.Core.Deadlines
{
    /// <summary>
    /// Represents a strict time budget for a frame or task.
    /// Used for cooperative multitasking to maintain 60fps.
    /// </summary>
    public class FrameDeadline
    {
        private readonly long _startedTimestamp;
        private readonly TimeSpan _budget;
        private readonly string _contextName;
        private readonly double _budgetMs;

        /// <summary>
        /// Creates a deadline that expires in the specified milliseconds from now.
        /// </summary>
        public FrameDeadline(double budgetMs, string contextName = "Unknown")
        {
            if (double.IsNaN(budgetMs) ||
                double.IsInfinity(budgetMs) ||
                budgetMs < 0 ||
                budgetMs > TimeSpan.MaxValue.TotalMilliseconds)
            {
                throw new ArgumentOutOfRangeException(nameof(budgetMs));
            }

            _budgetMs = budgetMs;
            _budget = TimeSpan.FromMilliseconds(budgetMs);
            _startedTimestamp = Stopwatch.GetTimestamp();
            _contextName = string.IsNullOrWhiteSpace(contextName) ? "Unknown" : contextName.Trim();
        }

        public double BudgetMs => _budgetMs;

        public string ContextName => _contextName;

        public bool IsExpired => Stopwatch.GetElapsedTime(_startedTimestamp) >= _budget;

        public TimeSpan Remaining
        {
            get
            {
                var remaining = _budget - Stopwatch.GetElapsedTime(_startedTimestamp);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Checks if the deadline has expired and throws if it has.
        /// </summary>
        public void Check()
        {
            if (IsExpired)
            {
                throw new DeadlineExceededException(_contextName);
            }
        }

        public bool TryCheck()
        {
            return !IsExpired;
        }

        public override string ToString()
        {
            return $"{ContextName} ({BudgetMs:0.###}ms, remaining={Remaining.TotalMilliseconds:0.###}ms)";
        }
    }

    public class DeadlineExceededException : Exception
    {
        public string Phase { get; }
        public DeadlineExceededException(string phase) : base($"Deadline exceeded during {phase}")
        {
            Phase = phase;
        }
    }
}
