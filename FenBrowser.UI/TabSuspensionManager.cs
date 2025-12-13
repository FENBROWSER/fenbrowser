using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.UI
{
    /// <summary>
    /// Tab state for suspension and restoration
    /// </summary>
    public class TabState
    {
        public int TabId { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public double ScrollY { get; set; }
        public DateTime SuspendedAt { get; set; }
        public bool WasSuspended { get; set; }
    }

    /// <summary>
    /// Event args for tab suspension events
    /// </summary>
    public class TabSuspensionEventArgs : EventArgs
    {
        public int TabId { get; set; }
        public TabState State { get; set; }
    }

    /// <summary>
    /// Manages automatic tab suspension for memory management.
    /// Suspends inactive tabs after a configurable timeout to free memory.
    /// </summary>
    public sealed class TabSuspensionManager : IDisposable
    {
        private static readonly Lazy<TabSuspensionManager> _instance = 
            new Lazy<TabSuspensionManager>(() => new TabSuspensionManager());
        
        public static TabSuspensionManager Instance => _instance.Value;

        // Tab activity tracking
        private readonly ConcurrentDictionary<int, DateTime> _lastActivity = 
            new ConcurrentDictionary<int, DateTime>();
        
        // Suspended tab states
        private readonly ConcurrentDictionary<int, TabState> _suspendedTabs = 
            new ConcurrentDictionary<int, TabState>();
        
        // Active tab (never suspend)
        private int _activeTabId = -1;
        private readonly object _activeTabLock = new object();

        // Suspension check timer
        private Timer _suspensionTimer;
        private bool _disposed;

        // Events
        public event EventHandler<TabSuspensionEventArgs> TabSuspended;
        public event EventHandler<TabSuspensionEventArgs> TabResumed;
        public event EventHandler<int> TabSuspensionRequested;

        private TabSuspensionManager()
        {
            // Check for tabs to suspend every minute
            _suspensionTimer = new Timer(CheckForSuspension, null, 
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        // ========== Public API ==========

        /// <summary>
        /// Track activity on a tab (call on navigation, interaction, etc.)
        /// </summary>
        public void TrackActivity(int tabId)
        {
            _lastActivity[tabId] = DateTime.UtcNow;
            
            // If this tab was suspended, mark for resume
            if (_suspendedTabs.ContainsKey(tabId))
            {
                ResumeTab(tabId);
            }
        }

        /// <summary>
        /// Set the currently active tab (never suspend active tab)
        /// </summary>
        public void SetActiveTab(int tabId)
        {
            lock (_activeTabLock)
            {
                _activeTabId = tabId;
            }
            TrackActivity(tabId);
        }

        /// <summary>
        /// Check if a tab is suspended
        /// </summary>
        public bool IsSuspended(int tabId)
        {
            return _suspendedTabs.ContainsKey(tabId);
        }

        /// <summary>
        /// Get the suspended state of a tab
        /// </summary>
        public TabState GetSuspendedState(int tabId)
        {
            _suspendedTabs.TryGetValue(tabId, out var state);
            return state;
        }

        /// <summary>
        /// Manually suspend a tab
        /// </summary>
        public void SuspendTab(int tabId, string url = null, string title = null, double scrollY = 0)
        {
            if (tabId == _activeTabId)
            {
                FenLogger.Debug($"[TabSuspension] Cannot suspend active tab {tabId}", LogCategory.General);
                return;
            }

            var state = new TabState
            {
                TabId = tabId,
                Url = url ?? "",
                Title = title ?? "",
                ScrollY = scrollY,
                SuspendedAt = DateTime.UtcNow,
                WasSuspended = true
            };

            _suspendedTabs[tabId] = state;
            
            // Notify cache manager
            CacheManager.Instance.SuspendTabPartition(tabId);
            
            // Raise event for UI update
            TabSuspended?.Invoke(this, new TabSuspensionEventArgs { TabId = tabId, State = state });
            
            FenLogger.Info($"[TabSuspension] Suspended tab {tabId}: {title}", LogCategory.General);
        }

        /// <summary>
        /// Resume a suspended tab
        /// </summary>
        public void ResumeTab(int tabId)
        {
            if (_suspendedTabs.TryRemove(tabId, out var state))
            {
                // Notify cache manager
                CacheManager.Instance.ResumeTabPartition(tabId);
                
                // Raise event for UI reload
                TabResumed?.Invoke(this, new TabSuspensionEventArgs { TabId = tabId, State = state });
                
                FenLogger.Info($"[TabSuspension] Resumed tab {tabId}: {state.Title}", LogCategory.General);
            }
        }

        /// <summary>
        /// Remove a tab from tracking (call when tab is closed)
        /// </summary>
        public void RemoveTab(int tabId)
        {
            _lastActivity.TryRemove(tabId, out _);
            _suspendedTabs.TryRemove(tabId, out _);
            CacheManager.Instance.DestroyTabPartition(tabId);
        }

        /// <summary>
        /// Get suspension statistics
        /// </summary>
        public (int active, int suspended, int total) GetStats()
        {
            return (
                _lastActivity.Count - _suspendedTabs.Count,
                _suspendedTabs.Count,
                _lastActivity.Count
            );
        }

        /// <summary>
        /// Force check for tabs to suspend
        /// </summary>
        public void ForceCheck()
        {
            CheckForSuspension(null);
        }

        // ========== Private Methods ==========

        private void CheckForSuspension(object state)
        {
            if (_disposed) return;
            
            var config = NetworkConfiguration.Instance;
            if (!config.EnableTabSuspension) return;

            var threshold = TimeSpan.FromMinutes(config.TabSuspensionMinutes);
            var now = DateTime.UtcNow;
            var activeTab = _activeTabId;

            // Get tabs that are inactive beyond threshold
            var candidates = _lastActivity
                .Where(kvp => 
                    kvp.Key != activeTab && 
                    !_suspendedTabs.ContainsKey(kvp.Key) &&
                    (now - kvp.Value) > threshold)
                .OrderBy(kvp => kvp.Value) // Oldest first
                .ToList();

            // Check memory pressure for aggressive suspension
            var cacheStats = CacheManager.Instance.GetStatistics();
            var isMemoryPressure = cacheStats.TotalMemoryBytes > config.AggressiveSuspensionThreshold;

            // Keep minimum active tabs
            var activeCount = _lastActivity.Count - _suspendedTabs.Count;
            var canSuspend = activeCount - candidates.Count >= config.MinActiveTabs;

            if (candidates.Any() && canSuspend)
            {
                // Suspend oldest inactive tabs
                var toSuspend = isMemoryPressure ? candidates : candidates.Take(1);
                
                foreach (var candidate in toSuspend)
                {
                    // Request suspension through event (UI will provide URL/title)
                    TabSuspensionRequested?.Invoke(this, candidate.Key);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _suspensionTimer?.Dispose();
            _lastActivity.Clear();
            _suspendedTabs.Clear();
        }
    }
}
