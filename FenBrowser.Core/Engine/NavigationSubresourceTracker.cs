using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Tracks navigation-scoped subresource fetch activity (e.g. CSS/image requests)
    /// so completion decisions can be bound to the active navigation id.
    /// </summary>
    public sealed class NavigationSubresourceTracker
    {
        private readonly object _sync = new();
        private readonly Dictionary<long, int> _pendingByNavigation = new();

        public event Action<long, int> PendingCountChanged;

        public void ResetNavigation(long navigationId)
        {
            if (navigationId <= 0)
            {
                return;
            }

            lock (_sync)
            {
                _pendingByNavigation[navigationId] = 0;
            }

            PendingCountChanged?.Invoke(navigationId, 0);
        }

        public void AbandonNavigation(long navigationId)
        {
            if (navigationId <= 0)
            {
                return;
            }

            bool removed;
            lock (_sync)
            {
                removed = _pendingByNavigation.Remove(navigationId);
            }

            if (removed)
            {
                PendingCountChanged?.Invoke(navigationId, 0);
            }
        }

        public void MarkLoadStarted(long navigationId)
        {
            if (navigationId <= 0)
            {
                return;
            }

            int count;
            lock (_sync)
            {
                if (!_pendingByNavigation.TryGetValue(navigationId, out count))
                {
                    count = 0;
                }

                count++;
                _pendingByNavigation[navigationId] = count;
            }

            PendingCountChanged?.Invoke(navigationId, count);
        }

        public void MarkLoadCompleted(long navigationId)
        {
            if (navigationId <= 0)
            {
                return;
            }

            int count;
            lock (_sync)
            {
                if (!_pendingByNavigation.TryGetValue(navigationId, out count))
                {
                    return;
                }

                count = Math.Max(0, count - 1);
                if (count == 0)
                {
                    _pendingByNavigation[navigationId] = 0;
                }
                else
                {
                    _pendingByNavigation[navigationId] = count;
                }
            }

            PendingCountChanged?.Invoke(navigationId, count);
        }

        public int GetPendingCount(long navigationId)
        {
            if (navigationId <= 0)
            {
                return 0;
            }

            lock (_sync)
            {
                return _pendingByNavigation.TryGetValue(navigationId, out var count)
                    ? Math.Max(0, count)
                    : 0;
            }
        }

        public bool HasPendingLoads(long navigationId)
        {
            return GetPendingCount(navigationId) > 0;
        }

        public IReadOnlyDictionary<long, int> SnapshotPendingCounts()
        {
            lock (_sync)
            {
                return new Dictionary<long, int>(_pendingByNavigation);
            }
        }
    }
}

