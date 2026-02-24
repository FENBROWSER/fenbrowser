using System;

namespace FenBrowser.Core.Engine
{
    public enum NavigationLifecyclePhase
    {
        Idle = 0,
        Requested = 1,
        Fetching = 2,
        ResponseReceived = 3,
        Committing = 4,
        Interactive = 5,
        Complete = 6,
        Failed = 7,
        Cancelled = 8
    }

    public sealed class NavigationLifecycleTransition
    {
        public long NavigationId { get; set; }
        public NavigationLifecyclePhase PreviousPhase { get; set; }
        public NavigationLifecyclePhase Phase { get; set; }
        public string RequestedUrl { get; set; }
        public string EffectiveUrl { get; set; }
        public string ResponseStatus { get; set; }
        public string Detail { get; set; }
        public bool IsUserInput { get; set; }
        public bool IsRedirect { get; set; }
        public int RedirectCount { get; set; }
        public string CommitSource { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class NavigationLifecycleSnapshot
    {
        public long NavigationId { get; set; }
        public NavigationLifecyclePhase Phase { get; set; }
        public string RequestedUrl { get; set; }
        public string EffectiveUrl { get; set; }
        public string ResponseStatus { get; set; }
        public string Detail { get; set; }
        public bool IsUserInput { get; set; }
        public bool IsRedirect { get; set; }
        public int RedirectCount { get; set; }
        public string CommitSource { get; set; }
        public DateTime LastTransitionUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Deterministic navigation lifecycle tracker used by host/runtime.
    /// Enforces monotonic transitions and rejects stale navigation ids.
    /// </summary>
    public sealed class NavigationLifecycleTracker
    {
        private readonly object _sync = new();
        private long _nextNavigationId;
        private NavigationLifecycleSnapshot _snapshot = new()
        {
            NavigationId = 0,
            Phase = NavigationLifecyclePhase.Idle
        };

        public event Action<NavigationLifecycleTransition> Transitioned;

        public NavigationLifecycleSnapshot Snapshot => GetSnapshot();

        public NavigationLifecycleSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                return CloneSnapshot(_snapshot);
            }
        }

        public long BeginNavigation(string requestedUrl, bool isUserInput)
        {
            NavigationLifecycleTransition transition;
            lock (_sync)
            {
                _nextNavigationId++;
                var previous = _snapshot.Phase;
                _snapshot = new NavigationLifecycleSnapshot
                {
                    NavigationId = _nextNavigationId,
                    Phase = NavigationLifecyclePhase.Requested,
                    RequestedUrl = requestedUrl,
                    EffectiveUrl = requestedUrl,
                    IsUserInput = isUserInput,
                    LastTransitionUtc = DateTime.UtcNow
                };

                transition = CreateTransition(_snapshot, previous);
            }

            Transitioned?.Invoke(transition);
            return transition.NavigationId;
        }

        public bool MarkFetching(long navigationId, string effectiveUrl = null)
        {
            return TryTransition(
                navigationId,
                NavigationLifecyclePhase.Fetching,
                effectiveUrl: effectiveUrl);
        }

        public bool MarkResponseReceived(long navigationId, string responseStatus, string effectiveUrl = null, string detail = null)
        {
            return TryTransition(
                navigationId,
                NavigationLifecyclePhase.ResponseReceived,
                effectiveUrl: effectiveUrl,
                responseStatus: responseStatus,
                detail: detail);
        }

        public bool MarkResponseReceived(
            long navigationId,
            string responseStatus,
            string effectiveUrl,
            bool isRedirect,
            int redirectCount,
            string detail = null)
        {
            return TryTransition(
                navigationId,
                NavigationLifecyclePhase.ResponseReceived,
                effectiveUrl: effectiveUrl,
                responseStatus: responseStatus,
                detail: detail,
                isRedirect: isRedirect,
                redirectCount: redirectCount);
        }

        public bool MarkCommitting(long navigationId, string effectiveUrl = null, string commitSource = null)
        {
            return TryTransition(
                navigationId,
                NavigationLifecyclePhase.Committing,
                effectiveUrl: effectiveUrl,
                commitSource: commitSource);
        }

        public bool MarkInteractive(long navigationId, string detail = null)
        {
            return TryTransition(
                navigationId,
                NavigationLifecyclePhase.Interactive,
                detail: detail);
        }

        public bool MarkComplete(long navigationId, string detail = null)
        {
            return TryTransition(
                navigationId,
                NavigationLifecyclePhase.Complete,
                detail: detail);
        }

        public bool MarkFailed(long navigationId, string detail, string responseStatus = null)
        {
            return TryTransition(
                navigationId,
                NavigationLifecyclePhase.Failed,
                responseStatus: responseStatus,
                detail: detail);
        }

        public bool MarkCancelled(long navigationId, string detail = null)
        {
            return TryTransition(
                navigationId,
                NavigationLifecyclePhase.Cancelled,
                detail: detail);
        }

        private bool TryTransition(
            long navigationId,
            NavigationLifecyclePhase nextPhase,
            string effectiveUrl = null,
            string responseStatus = null,
            string detail = null,
            bool? isRedirect = null,
            int? redirectCount = null,
            string commitSource = null)
        {
            NavigationLifecycleTransition transition;
            lock (_sync)
            {
                if (_snapshot.NavigationId != navigationId)
                {
                    return false;
                }

                var previous = _snapshot.Phase;
                if (!IsTransitionAllowed(previous, nextPhase))
                {
                    return false;
                }

                _snapshot.Phase = nextPhase;
                if (!string.IsNullOrWhiteSpace(effectiveUrl))
                {
                    _snapshot.EffectiveUrl = effectiveUrl;
                }

                if (!string.IsNullOrWhiteSpace(responseStatus))
                {
                    _snapshot.ResponseStatus = responseStatus;
                }

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    _snapshot.Detail = detail;
                }

                if (isRedirect.HasValue)
                {
                    _snapshot.IsRedirect = isRedirect.Value;
                }

                if (redirectCount.HasValue)
                {
                    _snapshot.RedirectCount = Math.Max(0, redirectCount.Value);
                }

                if (!string.IsNullOrWhiteSpace(commitSource))
                {
                    _snapshot.CommitSource = commitSource;
                }

                _snapshot.LastTransitionUtc = DateTime.UtcNow;
                transition = CreateTransition(_snapshot, previous);
            }

            Transitioned?.Invoke(transition);
            return true;
        }

        private static bool IsTransitionAllowed(NavigationLifecyclePhase previous, NavigationLifecyclePhase next)
        {
            if (previous == next)
            {
                return false;
            }

            return previous switch
            {
                NavigationLifecyclePhase.Idle => next == NavigationLifecyclePhase.Requested,
                NavigationLifecyclePhase.Requested =>
                    next == NavigationLifecyclePhase.Fetching ||
                    next == NavigationLifecyclePhase.ResponseReceived ||
                    next == NavigationLifecyclePhase.Committing ||
                    next == NavigationLifecyclePhase.Failed ||
                    next == NavigationLifecyclePhase.Cancelled,
                NavigationLifecyclePhase.Fetching =>
                    next == NavigationLifecyclePhase.ResponseReceived ||
                    next == NavigationLifecyclePhase.Failed ||
                    next == NavigationLifecyclePhase.Cancelled,
                NavigationLifecyclePhase.ResponseReceived =>
                    next == NavigationLifecyclePhase.Committing ||
                    next == NavigationLifecyclePhase.Failed ||
                    next == NavigationLifecyclePhase.Cancelled,
                NavigationLifecyclePhase.Committing =>
                    next == NavigationLifecyclePhase.Interactive ||
                    next == NavigationLifecyclePhase.Complete ||
                    next == NavigationLifecyclePhase.Failed ||
                    next == NavigationLifecyclePhase.Cancelled,
                NavigationLifecyclePhase.Interactive =>
                    next == NavigationLifecyclePhase.Complete ||
                    next == NavigationLifecyclePhase.Failed ||
                    next == NavigationLifecyclePhase.Cancelled,
                NavigationLifecyclePhase.Complete => next == NavigationLifecyclePhase.Requested,
                NavigationLifecyclePhase.Failed => next == NavigationLifecyclePhase.Requested,
                NavigationLifecyclePhase.Cancelled => next == NavigationLifecyclePhase.Requested,
                _ => false
            };
        }

        private static NavigationLifecycleTransition CreateTransition(
            NavigationLifecycleSnapshot snapshot,
            NavigationLifecyclePhase previous)
        {
            return new NavigationLifecycleTransition
            {
                NavigationId = snapshot.NavigationId,
                PreviousPhase = previous,
                Phase = snapshot.Phase,
                RequestedUrl = snapshot.RequestedUrl,
                EffectiveUrl = snapshot.EffectiveUrl,
                ResponseStatus = snapshot.ResponseStatus,
                Detail = snapshot.Detail,
                IsUserInput = snapshot.IsUserInput,
                IsRedirect = snapshot.IsRedirect,
                RedirectCount = snapshot.RedirectCount,
                CommitSource = snapshot.CommitSource,
                TimestampUtc = snapshot.LastTransitionUtc
            };
        }

        private static NavigationLifecycleSnapshot CloneSnapshot(NavigationLifecycleSnapshot source)
        {
            return new NavigationLifecycleSnapshot
            {
                NavigationId = source.NavigationId,
                Phase = source.Phase,
                RequestedUrl = source.RequestedUrl,
                EffectiveUrl = source.EffectiveUrl,
                ResponseStatus = source.ResponseStatus,
                Detail = source.Detail,
                IsUserInput = source.IsUserInput,
                IsRedirect = source.IsRedirect,
                RedirectCount = source.RedirectCount,
                CommitSource = source.CommitSource,
                LastTransitionUtc = source.LastTransitionUtc
            };
        }
    }
}
