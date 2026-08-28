using System.Collections.Generic;
using FenBrowser.Core.Engine;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Binds renderer-side engine navigation ids to the correlation id of the
    /// Navigate envelope that caused them, so forwarded lifecycle transitions
    /// carry the correlation of THEIR navigation.
    /// A navigation started inside the child (script location change, input
    /// activation) is observed with no pending correlation and therefore forwards
    /// an empty one, and a navigation's binding is pruned once it reaches a
    /// terminal phase so stale correlations cannot be reused by later
    /// transitions of the same engine navigation id.
    /// </summary>
    public sealed class RendererNavigationCorrelationBinder
    {
        private const int TerminalRetentionLimit = 64;

        private readonly object _sync = new();
        private readonly Dictionary<long, string> _correlationByNavigationId = new();
        private readonly Queue<long> _terminalNavigationIds = new();
        private string _pendingCorrelation;

        /// <summary>
        /// Marks the correlation id of the Navigate envelope the child is about
        /// to process: the next engine Requested transition binds to it.
        /// </summary>
        public void BeginNavigation(string correlationId)
        {
            lock (_sync)
            {
                _pendingCorrelation = correlationId ?? string.Empty;
            }
        }

        /// <summary>
        /// Clears the pending correlation after the envelope's navigation call
        /// completed, so navigations initiated by the child itself (script,
        /// input) do not inherit it.
        /// </summary>
        public void EndNavigation()
        {
            lock (_sync)
            {
                _pendingCorrelation = null;
            }
        }

        /// <summary>
        /// Observes an engine lifecycle transition: binds the navigation id to
        /// the pending correlation on first sight and consumes the binding on a
        /// terminal phase. Returns the correlation the transition must carry —
        /// empty for a navigation whose terminal phase was already forwarded,
        /// so a stale replay cannot resurrect a consumed correlation.
        /// </summary>
        public string Observe(long navigationId, NavigationLifecyclePhase phase)
        {
            lock (_sync)
            {
                if (!_correlationByNavigationId.TryGetValue(navigationId, out var correlation))
                {
                    correlation = _pendingCorrelation ?? string.Empty;
                    _correlationByNavigationId[navigationId] = correlation;
                }

                var isTerminal = phase == NavigationLifecyclePhase.Complete ||
                                 phase == NavigationLifecyclePhase.Failed ||
                                 phase == NavigationLifecyclePhase.Cancelled;
                if (isTerminal && correlation.Length > 0)
                {
                    _correlationByNavigationId[navigationId] = string.Empty;
                    _terminalNavigationIds.Enqueue(navigationId);
                    while (_terminalNavigationIds.Count > TerminalRetentionLimit)
                    {
                        _correlationByNavigationId.Remove(_terminalNavigationIds.Dequeue());
                    }
                }

                return correlation;
            }
        }
    }
}
