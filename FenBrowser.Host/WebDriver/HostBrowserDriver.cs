using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Host.ProcessIsolation;
using FenBrowser.Host.Tabs;
using FenBrowser.WebDriver.Commands;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.Host.WebDriver
{
    public class HostBrowserDriver : IBrowserDriver
    {
        private const int ElementLookupTimeoutMs = 0;
        private static readonly TimeSpan ElementLookupPollInterval = TimeSpan.FromMilliseconds(50);
        private TabManager _tabs => TabManager.Instance;

        public HostBrowserDriver()
        {
            HostDialogCoordinator.Install();
        }

        public async Task NavigateAsync(string url)
        {
            await RunOnMainThread(async () =>
            {
                if (_tabs.ActiveTab == null)
                {
                    _tabs.CreateTab();
                }

                var activeTab = _tabs.ActiveTab ?? throw new InvalidOperationException("Current browsing context is no longer open");
                await activeTab.NavigateProgrammaticAsync(url);
            });
        }

        private BrowserTab GetActiveTabOrThrow()
        {
            return _tabs.ActiveTab ?? throw new InvalidOperationException("Current browsing context is no longer open");
        }

        private BrowserHost GetActiveHostOrThrow(bool requireValidContext = true)
        {
            var host = GetActiveTabOrThrow().Browser?.Host;
            if (host == null)
            {
                throw new InvalidOperationException("Current browsing context is no longer open");
            }

            if (requireValidContext && !host.HasValidCurrentBrowsingContext())
            {
                throw new InvalidOperationException("Current browsing context is no longer open");
            }

            return host;
        }

        public async Task<string> GetCurrentUrlAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var activeTab = GetActiveTabOrThrow();
                // With an out-of-process renderer the navigation commits inside
                // the renderer child; the committed URL reaches the UI through
                // metadata sync (BrowserIntegration.CurrentUrl), while the local
                // engine's current URI stays on the previous document.
                if (ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
                {
                    var tabUrl = activeTab.Url;
                    if (!string.IsNullOrWhiteSpace(tabUrl))
                    {
                        return tabUrl;
                    }
                }

                var host = GetActiveHostOrThrow();
                var currentUrl = await host.GetCurrentUrlAsync().ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(currentUrl) ? "about:blank" : currentUrl;
            });
        }

        public async Task<string> GetTitleAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var activeTab = GetActiveTabOrThrow();
                var host = GetActiveHostOrThrow(requireValidContext: false);
                var domTitle = await host.GetTitleAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(domTitle))
                {
                    return domTitle;
                }

                return activeTab?.Title ?? string.Empty;
            });
        }

        public async Task<string> GetWindowHandleAsync()
        {
            return await RunOnMainThread(() =>
            {
                return GetActiveTabOrThrow().Id.ToString();
            });
        }

        public async Task<IReadOnlyList<string>> GetWindowHandlesAsync()
        {
            return await RunOnMainThread(() =>
            {
                var handles = _tabs.Tabs.Select(tab => tab.Id.ToString()).ToList();
                return (IReadOnlyList<string>)handles;
            });
        }

        public async Task CloseWindowAsync()
        {
            await RunOnMainThread(() =>
            {
                _ = GetActiveTabOrThrow();
                _tabs.CloseActiveTab();
            });
        }

        public async Task GoBackAsync()
        {
            await RunOnMainThread(async () =>
            {
                if (_tabs.ActiveTab?.Browser != null)
                {
                    await _tabs.ActiveTab.Browser.GoBackAsync();
                }
            });
        }

        public async Task GoForwardAsync()
        {
            await RunOnMainThread(async () =>
            {
                if (_tabs.ActiveTab?.Browser != null)
                {
                    await _tabs.ActiveTab.Browser.GoForwardAsync();
                }
            });
        }

        public async Task RefreshAsync()
        {
            await RunOnMainThread(async () =>
            {
                if (_tabs.ActiveTab != null)
                {
                    await _tabs.ActiveTab.NavigateProgrammaticAsync(_tabs.ActiveTab.Url);
                }
            });
        }

        // WD-004: pageLoadStrategy eager/normal waits on the engine's explicit
        // navigation lifecycle (Interactive = DOMContentLoaded, Complete = load)
        // instead of inferring completion from a changed URL. Tracked navigations
        // carry a driver-issued identifier so lifecycle events belonging to other
        // navigations (a later user or script navigation) cannot complete a wait.
        private long _nextTrackedNavigationId;
        private readonly ConcurrentDictionary<long, RendererNavigationRecord> _pendingRendererNavigations = new();
        private readonly ConcurrentDictionary<long, (long TrackerId, FenBrowser.Core.Engine.NavigationLifecyclePhase InitialPhase)> _pendingLocalNavigations = new();

        /// <summary>
        /// Retains the latest lifecycle phase of a tracked renderer navigation.
        /// The record is created and its coordinator handler attached BEFORE the
        /// navigation is dispatched, so Interactive/Complete/Failed/Cancelled
        /// transitions that arrive while the command is still URL-commit polling
        /// are retained instead of being lost to a late subscription.
        /// </summary>
        private sealed class RendererNavigationRecord
        {
            private readonly object _sync = new();
            private TaskCompletionSource<bool> _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int TabId { get; init; }
            public string CorrelationId { get; init; }
            public Action<int, RendererNavigationLifecyclePayload> Handler { get; set; }

            private FenBrowser.Core.Engine.NavigationLifecyclePhase Phase { get; set; }
                = FenBrowser.Core.Engine.NavigationLifecyclePhase.Idle;

            public void Update(FenBrowser.Core.Engine.NavigationLifecyclePhase phase)
            {
                TaskCompletionSource<bool> releasedSignal = null;
                lock (_sync)
                {
                    if (ShouldReplace(Phase, phase))
                    {
                        Phase = phase;
                        releasedSignal = _signal;
                        _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }

                releasedSignal?.TrySetResult(true);
            }

            public FenBrowser.Core.Engine.NavigationLifecyclePhase Snapshot
            {
                get
                {
                    lock (_sync)
                    {
                        return Phase;
                    }
                }
            }

            public Task WaitChangeAsync()
            {
                lock (_sync)
                {
                    return _signal.Task;
                }
            }

            private static bool ShouldReplace(
                FenBrowser.Core.Engine.NavigationLifecyclePhase current,
                FenBrowser.Core.Engine.NavigationLifecyclePhase next)
            {
                if (current == next)
                {
                    return false;
                }

                if (next == FenBrowser.Core.Engine.NavigationLifecyclePhase.Failed ||
                    next == FenBrowser.Core.Engine.NavigationLifecyclePhase.Cancelled)
                {
                    return true;
                }

                if (current == FenBrowser.Core.Engine.NavigationLifecyclePhase.Failed ||
                    current == FenBrowser.Core.Engine.NavigationLifecyclePhase.Cancelled)
                {
                    return false;
                }

                return next > current;
            }
        }

        public async Task<long> NavigateTrackedAsync(string url)
        {
            var tab = _tabs.ActiveTab ?? throw new InvalidOperationException("Current browsing context is no longer open");

            if (ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
            {
                var coordinator = ProcessIsolation.ProcessIsolationRuntime.Current;
                var correlationId = Guid.NewGuid().ToString("N");
                var trackedId = NextTrackedNavigationId();
                var record = new RendererNavigationRecord
                {
                    TabId = tab.Id,
                    CorrelationId = correlationId
                };
                record.Handler = (reportedTabId, payload) =>
                {
                    if (reportedTabId != record.TabId || payload == null)
                    {
                        return;
                    }

                    if (!string.Equals(payload.NavigationCorrelationId, record.CorrelationId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (Enum.TryParse<FenBrowser.Core.Engine.NavigationLifecyclePhase>(
                            payload.Phase, ignoreCase: true, out var phase))
                    {
                        record.Update(phase);
                    }
                };

                _pendingRendererNavigations[trackedId] = record;
                // Subscribe before dispatching: transitions emitted while the
                // command is still waiting for URL commit must be retained.
                coordinator.NavigationLifecycleReceived += record.Handler;
                try
                {
                    await RunOnMainThread(() => tab.NavigateProgrammaticAsync(url, navigationCorrelationId: correlationId)).ConfigureAwait(false);
                }
                catch
                {
                    coordinator.NavigationLifecycleReceived -= record.Handler;
                    _pendingRendererNavigations.TryRemove(trackedId, out _);
                    throw;
                }

                return trackedId;
            }

            // In-process: the awaited navigation drives the local lifecycle
            // tracker to a terminal phase for the navigation it began, so the
            // snapshot at completion identifies both the id and the outcome.
            var tracked = await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                await tab.NavigateProgrammaticAsync(url);
                var snapshot = host.NavigationLifecycleState;
                return (snapshot.NavigationId, snapshot.Phase);
            }).ConfigureAwait(false);

            var localId = NextTrackedNavigationId();
            _pendingLocalNavigations[localId] = (tracked.NavigationId, tracked.Phase);
            return localId;
        }

        private long NextTrackedNavigationId()
        {
            var id = Interlocked.Increment(ref _nextTrackedNavigationId);
            PruneStaleNavigations(id);
            return id;
        }

        private void PruneStaleNavigations(long currentId)
        {
            foreach (var key in _pendingRendererNavigations.Keys)
            {
                if (key < currentId - 32)
                {
                    _pendingRendererNavigations.TryRemove(key, out _);
                }
            }

            foreach (var key in _pendingLocalNavigations.Keys)
            {
                if (key < currentId - 32)
                {
                    _pendingLocalNavigations.TryRemove(key, out _);
                }
            }
        }

        public async Task<WdReadinessWaitStatus> WaitForDocumentReadinessAsync(WdDocumentReadinessStage stage, int timeoutMs, long navigationId)
        {
            // Out-of-process renderer: navigation (and its lifecycle) runs in the
            // renderer child, so waiting on the local BrowserHost tracker would
            // never complete. Consume the retained lifecycle record for this
            // navigation — events that arrived before the wait began are already
            // in the record — and keep evaluating until the deadline.
            if (ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
            {
                if (navigationId != 0 && _pendingRendererNavigations.TryRemove(navigationId, out var record))
                {
                    try
                    {
                        return await AwaitRendererRecordReadiness(record, stage, timeoutMs).ConfigureAwait(false);
                    }
                    finally
                    {
                        DetachRendererRecord(record);
                    }
                }

                // Untracked: legacy ephemeral subscription, any correlation.
                var tabId = _tabs.ActiveTab?.Id ?? -1;
                return await WaitForRendererReadinessAsync(stage, timeoutMs, tabId, null).ConfigureAwait(false);
            }

            if (navigationId != 0 && _pendingLocalNavigations.TryRemove(navigationId, out var local))
            {
                return await WaitForLocalTrackedReadinessAsync(stage, timeoutMs, local.TrackerId, local.InitialPhase).ConfigureAwait(false);
            }

            // Untracked (id 0): legacy semantics on the local tracker.
            return await WaitForLocalUntrackedReadinessAsync(stage, timeoutMs).ConfigureAwait(false);
        }

        private static async Task<WdReadinessWaitStatus> AwaitRendererRecordReadiness(
            RendererNavigationRecord record,
            WdDocumentReadinessStage stage,
            int timeoutMs)
        {
            var targetPhase = TargetLifecyclePhase(stage);
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            while (true)
            {
                // The record already holds every transition forwarded since the
                // navigation was dispatched, so a Complete that fired during URL
                // commit polling resolves immediately here.
                if (TryClassifyPhase(record.Snapshot.ToString(), targetPhase, out var status))
                {
                    return status;
                }

                var remainingMs = (int)Math.Min(deadline - Environment.TickCount64, (long)int.MaxValue);
                if (remainingMs <= 0)
                {
                    return WdReadinessWaitStatus.TimedOut;
                }

                var signal = record.WaitChangeAsync();
                var finished = await Task.WhenAny(signal, Task.Delay(remainingMs)).ConfigureAwait(false);
                if (finished != signal)
                {
                    return WdReadinessWaitStatus.TimedOut;
                }
            }
        }

        private void DetachRendererRecord(RendererNavigationRecord record)
        {
            var coordinator = ProcessIsolation.ProcessIsolationRuntime.Current;
            if (coordinator != null && record.Handler != null)
            {
                coordinator.NavigationLifecycleReceived -= record.Handler;
            }
        }

        private async Task<WdReadinessWaitStatus> WaitForRendererReadinessAsync(
            WdDocumentReadinessStage stage,
            int timeoutMs,
            int tabId,
            string requiredCorrelationId)
        {
            var coordinator = ProcessIsolation.ProcessIsolationRuntime.Current;
            if (coordinator == null || timeoutMs <= 0)
            {
                return WdReadinessWaitStatus.TimedOut;
            }

            var targetPhase = TargetLifecyclePhase(stage);
            var completion = new TaskCompletionSource<WdReadinessWaitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnLifecycle(int reportedTabId, RendererNavigationLifecyclePayload payload)
            {
                if (reportedTabId != tabId || payload?.Phase == null)
                {
                    return;
                }

                // A later user/script navigation carries a different correlation
                // (or none); its transitions must never satisfy this wait.
                if (!string.IsNullOrEmpty(requiredCorrelationId) &&
                    !string.Equals(payload.NavigationCorrelationId, requiredCorrelationId, StringComparison.Ordinal))
                {
                    return;
                }

                if (TryClassifyPhase(payload.Phase, targetPhase, out var status))
                {
                    completion.TrySetResult(status);
                }
            }

            coordinator.NavigationLifecycleReceived += OnLifecycle;
            try
            {
                var finished = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (finished != completion.Task)
                {
                    return WdReadinessWaitStatus.TimedOut;
                }

                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                coordinator.NavigationLifecycleReceived -= OnLifecycle;
            }
        }

        private async Task<WdReadinessWaitStatus> WaitForLocalTrackedReadinessAsync(
            WdDocumentReadinessStage stage,
            int timeoutMs,
            long trackerId,
            FenBrowser.Core.Engine.NavigationLifecyclePhase initialPhase)
        {
            var targetPhase = TargetLifecyclePhase(stage);
            if (TryClassifyPhase(initialPhase.ToString(), targetPhase, out var initialStatus))
            {
                return initialStatus;
            }

            var wait = await RunOnMainThread(() => BeginReadinessWait(stage, trackerId)).ConfigureAwait(false);
            return await AwaitReadinessWait(wait, timeoutMs).ConfigureAwait(false);
        }

        private async Task<WdReadinessWaitStatus> WaitForLocalUntrackedReadinessAsync(WdDocumentReadinessStage stage, int timeoutMs)
        {
            var wait = await RunOnMainThread(() => BeginReadinessWait(stage, trackerIdFilter: null)).ConfigureAwait(false);
            return await AwaitReadinessWait(wait, timeoutMs).ConfigureAwait(false);
        }

        private static FenBrowser.Core.Engine.NavigationLifecyclePhase TargetLifecyclePhase(WdDocumentReadinessStage stage)
        {
            return stage == WdDocumentReadinessStage.Interactive
                ? FenBrowser.Core.Engine.NavigationLifecyclePhase.Interactive
                : FenBrowser.Core.Engine.NavigationLifecyclePhase.Complete;
        }

        private static bool TryClassifyPhase(
            string phaseName,
            FenBrowser.Core.Engine.NavigationLifecyclePhase targetPhase,
            out WdReadinessWaitStatus status)
        {
            status = WdReadinessWaitStatus.TimedOut;
            if (string.IsNullOrEmpty(phaseName) ||
                !Enum.TryParse<FenBrowser.Core.Engine.NavigationLifecyclePhase>(phaseName, ignoreCase: true, out var phase))
            {
                return false;
            }

            if (phase == FenBrowser.Core.Engine.NavigationLifecyclePhase.Failed ||
                phase == FenBrowser.Core.Engine.NavigationLifecyclePhase.Cancelled)
            {
                status = WdReadinessWaitStatus.NavigationAborted;
                return true;
            }

            if (phase >= targetPhase && phase <= FenBrowser.Core.Engine.NavigationLifecyclePhase.Complete)
            {
                status = WdReadinessWaitStatus.Reached;
                return true;
            }

            return false;
        }

        private sealed class ReadinessWait
        {
            public TaskCompletionSource<WdReadinessWaitStatus> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public BrowserHost Host { get; init; }
            public EventHandler<FenBrowser.Core.Engine.NavigationLifecycleTransition> Handler { get; set; }

            public void Unsubscribe() => Host.NavigationLifecycleChanged -= Handler;
        }

        private async Task<WdReadinessWaitStatus> AwaitReadinessWait(ReadinessWait wait, int timeoutMs)
        {
            try
            {
                if (wait.Completion.Task.IsCompleted)
                {
                    return await wait.Completion.Task.ConfigureAwait(false);
                }

                if (timeoutMs <= 0)
                {
                    return WdReadinessWaitStatus.TimedOut;
                }

                var finished = await Task.WhenAny(wait.Completion.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (finished != wait.Completion.Task)
                {
                    return WdReadinessWaitStatus.TimedOut;
                }

                return await wait.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                await RunOnMainThread(() => wait.Unsubscribe()).ConfigureAwait(false);
            }
        }

        private ReadinessWait BeginReadinessWait(WdDocumentReadinessStage stage, long? trackerIdFilter)
        {
            var host = GetActiveHostOrThrow();
            var targetPhase = TargetLifecyclePhase(stage);

            var wait = new ReadinessWait
            {
                Host = host
            };
            wait.Handler = (_, transition) =>
            {
                // Correlation: only transitions of the tracked navigation complete
                // the wait; other navigations are ignored.
                if (trackerIdFilter.HasValue && transition.NavigationId != trackerIdFilter.Value)
                {
                    return;
                }

                if (TryClassifyPhase(transition.Phase.ToString(), targetPhase, out var status))
                {
                    wait.Completion.TrySetResult(status);
                }
            };

            // The tracked variant deliberately skips the snapshot check: the
            // tracker snapshot may already describe a LATER navigation. The
            // tracked wait's outcome is anchored to the phase captured when its
            // navigation completed.
            if (!trackerIdFilter.HasValue)
            {
                var snapshot = host.NavigationLifecycleState;
                if (TryClassifyPhase(snapshot.Phase.ToString(), targetPhase, out var status))
                {
                    wait.Completion.TrySetResult(status);
                    return wait;
                }
            }

            host.NavigationLifecycleChanged += wait.Handler;
            return wait;
        }

        private ReadinessWait BeginReadinessWait(WdDocumentReadinessStage stage)
        {
            var host = GetActiveHostOrThrow();
            var targetPhase = stage == WdDocumentReadinessStage.Interactive
                ? FenBrowser.Core.Engine.NavigationLifecyclePhase.Interactive
                : FenBrowser.Core.Engine.NavigationLifecyclePhase.Complete;

            var wait = new ReadinessWait { Host = host };
            var snapshot = host.NavigationLifecycleState;
            if (snapshot.Phase == FenBrowser.Core.Engine.NavigationLifecyclePhase.Failed ||
                snapshot.Phase == FenBrowser.Core.Engine.NavigationLifecyclePhase.Cancelled)
            {
                wait.Completion.TrySetResult(WdReadinessWaitStatus.NavigationAborted);
            }
            else if (snapshot.Phase >= targetPhase &&
                     snapshot.Phase <= FenBrowser.Core.Engine.NavigationLifecyclePhase.Complete)
            {
                wait.Completion.TrySetResult(WdReadinessWaitStatus.Reached);
            }
            else
            {
                wait.Handler = (_, transition) =>
                {
                    if (transition.Phase == FenBrowser.Core.Engine.NavigationLifecyclePhase.Failed ||
                        transition.Phase == FenBrowser.Core.Engine.NavigationLifecyclePhase.Cancelled)
                    {
                        wait.Completion.TrySetResult(WdReadinessWaitStatus.NavigationAborted);
                    }
                    else if (transition.Phase >= targetPhase &&
                             transition.Phase <= FenBrowser.Core.Engine.NavigationLifecyclePhase.Complete)
                    {
                        wait.Completion.TrySetResult(WdReadinessWaitStatus.Reached);
                    }
                };
                host.NavigationLifecycleChanged += wait.Handler;
            }

            return wait;
        }

        public async Task<object> FindElementAsync(string strategy, string selector, object parentElement = null)
        {
            var parentId = parentElement as string;
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(ElementLookupTimeoutMs);

            while (true)
            {
                var (id, isLoading) = await RunOnMainThread(async () =>
                {
                    var activeTab = _tabs.ActiveTab;
                    var host = activeTab?.Browser?.Host;
                    if (host == null)
                    {
                        throw new InvalidOperationException("Current browsing context is no longer open");
                    }

                    var elementId = await host.FindElementAsync(strategy, selector, parentId);
                    return (Id: elementId, IsLoading: activeTab.IsLoading);
                });

                if (!string.IsNullOrEmpty(id))
                {
                    return id;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    return null;
                }

                // During navigation and immediate post-load settling, polling avoids flaky no-such-element races.
                await Task.Delay(isLoading ? ElementLookupPollInterval : TimeSpan.FromMilliseconds(25));
            }
        }

        public async Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null)
        {
            var parentId = parentElement as string;
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(ElementLookupTimeoutMs);

            while (true)
            {
                var (ids, isLoading) = await RunOnMainThread(async () =>
                {
                    var activeTab = _tabs.ActiveTab;
                    var host = activeTab?.Browser?.Host;
                    if (host == null)
                    {
                        throw new InvalidOperationException("Current browsing context is no longer open");
                    }

                    var elementIds = await host.FindElementsAsync(strategy, selector, parentId);
                    return (Ids: elementIds ?? Array.Empty<string>(), IsLoading: activeTab.IsLoading);
                });

                if (ids.Length > 0)
                {
                    return ids.Select(id => (object)id).ToArray();
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    return Array.Empty<object>();
                }

                await Task.Delay(isLoading ? ElementLookupPollInterval : TimeSpan.FromMilliseconds(25));
            }
        }

        public async Task<object> GetActiveElementAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return null;
                var id = await host.GetActiveElementAsync();
                return (object)id;
            });
        }

        public async Task<object> GetShadowRootAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(ElementLookupTimeoutMs);
            while (true)
            {
                var (shadowId, isLoading) = await RunOnMainThread(async () =>
                {
                    var activeTab = _tabs.ActiveTab;
                    var host = activeTab?.Browser?.Host;
                    if (host == null)
                    {
                        throw new InvalidOperationException("Current browsing context is no longer open");
                    }

                    var resolvedShadowId = await host.GetShadowRootAsync(id);
                    return (ShadowId: resolvedShadowId, IsLoading: activeTab.IsLoading);
                });

                if (!string.IsNullOrWhiteSpace(shadowId))
                {
                    return shadowId;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    return null;
                }

                await Task.Delay(isLoading ? ElementLookupPollInterval : TimeSpan.FromMilliseconds(25));
            }
        }

        public async Task<bool> IsElementSelectedAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.IsElementSelectedAsync(id);
            });
        }

        public async Task<object> GetElementPropertyAsync(object element, string name)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementPropertyAsync(id, name);
            });
        }

        public async Task<string> GetElementCssValueAsync(object element, string propertyName)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementCssValueAsync(id, propertyName);
            });
        }

        public async Task<string> GetElementTextAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementTextAsync(id);
            });
        }

        public async Task<string> GetElementTagNameAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementTagNameAsync(id);
            });
        }

        public async Task<WdElementRect> GetElementRectAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                var rect = await host.GetElementRectAsync(id);
                return new WdElementRect { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };
            });
        }

        public async Task<bool> IsElementEnabledAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.IsElementEnabledAsync(id);
            });
        }

        public async Task<string> GetElementComputedRoleAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementComputedRoleAsync(id);
            });
        }

        public async Task<string> GetElementComputedLabelAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementComputedLabelAsync(id);
            });
        }

        public async Task ClickElementAsync(object element)
        {
            await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                if (element is not string id)
                {
                    throw new InvalidOperationException("Element reference is invalid for current browsing context");
                }

                await host.ClickElementAsync(id);
            });
        }

        public async Task ClearElementAsync(object element)
        {
            await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                if (element is not string id)
                {
                    throw new InvalidOperationException("Element reference is invalid for current browsing context");
                }

                await host.ClearElementAsync(id);
            });
        }

        public async Task SendKeysAsync(object element, string text, bool strictFileInteractability = false)
        {
            await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                if (element is not string id)
                {
                    throw new InvalidOperationException("Element reference is invalid for current browsing context");
                }

                await host.SendKeysToElementAsync(id, text ?? string.Empty, strictFileInteractability);
            });
        }

        public async Task<string> GetElementAttributeAsync(object element, string name)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementAttributeAsync(id, name);
            });
        }

        public async Task<string> GetPageSourceAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return "<html></html>";
                return await host.GetPageSourceAsync();
            });
        }

        public async Task<object> ExecuteScriptAsync(string script, object[] args)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null)
                {
                    throw new InvalidOperationException("Current browsing context is no longer open");
                }
                return await host.ExecuteScriptAsync(script, args);
            });
        }

        public async Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeout)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null)
                {
                    throw new InvalidOperationException("Current browsing context is no longer open");
                }
                return await host.ExecuteAsyncScriptAsync(script, args, timeout);
            });
        }

        public async Task<string> TakeScreenshotAsync()
        {
            const int maxAttempts = 10;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await RunOnMainThread(() =>
                {
                    var activeTab = _tabs.ActiveTab;
                    if (activeTab == null)
                    {
                        return;
                    }

                    var bounds = ChromeManager.Instance.GetWebContentBounds();
                    FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.OnFrameRequested(activeTab, bounds.Width, bounds.Height);
                    activeTab.Browser.RequestRepaint();
                });

                // Allow the render thread to process the requested frame before capture.
                await Task.Delay(70).ConfigureAwait(false);

                var capture = await RunOnMainThread(() =>
                {
                    var bitmap = WindowManager.Instance.CaptureScreenshot();
                    if (bitmap == null)
                    {
                        return (Base64: string.Empty, IsSolid: true, Width: 0, Height: 0);
                    }

                    try
                    {
                        var screenshotBitmap = TryCropToWebViewport(bitmap);
                        try
                        {
                            var isSolid = IsSolidColor(screenshotBitmap);
                            using var image = SkiaSharp.SKImage.FromBitmap(screenshotBitmap);
                            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                            return (
                                Base64: Convert.ToBase64String(data.ToArray()),
                                IsSolid: isSolid,
                                Width: screenshotBitmap.Width,
                                Height: screenshotBitmap.Height);
                        }
                        finally
                        {
                            if (!ReferenceEquals(screenshotBitmap, bitmap))
                            {
                                screenshotBitmap.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        bitmap.Dispose();
                    }
                });

                if (!capture.IsSolid)
                {
                    return capture.Base64;
                }

                if (attempt == maxAttempts - 1)
                {
                    return capture.Base64;
                }

                // Solid captures can occur while waiting for asynchronous paint submission.
                // Retry with a short progressive backoff rather than synthesizing pixels.
                await Task.Delay(60 + (attempt * 20)).ConfigureAwait(false);
            }

            return string.Empty;
        }

        private static bool IsSolidColor(SkiaSharp.SKBitmap bitmap)
        {
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return true;
            }

            var first = bitmap.GetPixel(0, 0);
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y) != first)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static SkiaSharp.SKBitmap TryCropToWebViewport(SkiaSharp.SKBitmap sourceBitmap)
        {
            var contentBounds = ChromeManager.Instance.GetWebContentBounds();
            if (contentBounds.Width <= 1 || contentBounds.Height <= 1)
            {
                return sourceBitmap;
            }

            var dpiScale = Math.Max(WindowManager.Instance.DpiScale, 1f);
            var left = (int)Math.Floor(contentBounds.Left * dpiScale);
            var top = (int)Math.Floor(contentBounds.Top * dpiScale);
            var right = (int)Math.Ceiling(contentBounds.Right * dpiScale);
            var bottom = (int)Math.Ceiling(contentBounds.Bottom * dpiScale);

            left = Math.Max(0, Math.Min(left, sourceBitmap.Width));
            top = Math.Max(0, Math.Min(top, sourceBitmap.Height));
            right = Math.Max(left, Math.Min(right, sourceBitmap.Width));
            bottom = Math.Max(top, Math.Min(bottom, sourceBitmap.Height));

            var width = right - left;
            var height = bottom - top;
            if (width <= 1 || height <= 1)
            {
                return sourceBitmap;
            }

            var cropRect = new SkiaSharp.SKRectI(left, top, right, bottom);
            var cropped = new SkiaSharp.SKBitmap(width, height, sourceBitmap.ColorType, sourceBitmap.AlphaType);
            if (!sourceBitmap.ExtractSubset(cropped, cropRect))
            {
                cropped.Dispose();
                return sourceBitmap;
            }

            return cropped;
        }

        public async Task<string> TakeElementScreenshotAsync(object element)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return "";
                return await host.CaptureElementScreenshotAsync(id);
            });
        }

        public async Task<string> PrintPageAsync(WdPrintOptions options)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return "";
                var page = options?.Page ?? new WdPrintPageOptions();
                var landscape = string.Equals(options?.Orientation, "landscape", StringComparison.OrdinalIgnoreCase);
                var scale = options?.Scale ?? 1.0;
                return await host.PrintToPdfAsync(page.Width, page.Height, landscape, scale);
            });
        }

        public (int x, int y, int width, int height) GetWindowRect()
        {
            var rect = RunOnMainThread(() =>
            {
                var host = GetActiveHostOrThrow();
                return host.GetWindowRect();
            }).GetAwaiter().GetResult();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public void SetWindowRect(int? x, int? y, int? width, int? height)
        {
            RunOnMainThread(() =>
            {
                var host = GetActiveHostOrThrow();
                host.SetWindowRect(x, y, width, height);
            }).GetAwaiter().GetResult();
        }

        public (int x, int y, int width, int height) MaximizeWindow()
        {
            var rect = RunOnMainThread(() =>
            {
                var host = GetActiveHostOrThrow();
                return host.MaximizeWindow();
            }).GetAwaiter().GetResult();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public (int x, int y, int width, int height) MinimizeWindow()
        {
            var rect = RunOnMainThread(() =>
            {
                var host = GetActiveHostOrThrow();
                return host.MinimizeWindow();
            }).GetAwaiter().GetResult();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public (int x, int y, int width, int height) FullscreenWindow()
        {
            var rect = RunOnMainThread(() =>
            {
                var host = GetActiveHostOrThrow();
                return host.FullscreenWindow();
            }).GetAwaiter().GetResult();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public async Task<string> NewWindowAsync(string typeHint)
        {
            return await RunOnMainThread(async () =>
            {
                var beforeActiveTabId = _tabs.ActiveTab?.Id;
                var beforeCount = _tabs.Tabs.Count;
                _tabs.CreateTab();

                // Fallback guard if tab creation was ignored by host state.
                if (_tabs.Tabs.Count <= beforeCount || _tabs.ActiveTab?.Id == beforeActiveTabId)
                {
                    _tabs.CreateTab();
                }

                var activeTab = _tabs.ActiveTab;
                if (activeTab != null && activeTab.Id != beforeActiveTabId)
                {
                    await InitializeNewTopLevelContextAsync(activeTab).ConfigureAwait(false);
                    return activeTab.Id.ToString();
                }

                // No new context: never hand back an existing tab (or a made-up id) as new.
                throw new InvalidOperationException("New Window did not create a tab.");
            });
        }

        internal static Task InitializeNewTopLevelContextAsync(BrowserTab tab)
        {
            if (tab == null)
            {
                throw new ArgumentNullException(nameof(tab));
            }

            // WebDriver New Window creates a top-level browsing context whose
            // initial active document is a fully loaded about:blank document.
            return tab.NavigateProgrammaticAsync("about:blank");
        }

        public async Task SwitchToWindowAsync(string windowHandle)
        {
            var switched = await RunOnMainThread(() =>
            {
                // Correlate WebDriver window handle to tab ID for physical tab switching
                if (int.TryParse(windowHandle, out var tabId))
                {
                    var tabs = _tabs.Tabs;
                    var didSwitch = false;
                    for (int i = 0; i < tabs.Count; i++)
                    {
                        if (tabs[i].Id == tabId)
                        {
                            if (i == _tabs.ActiveIndex)
                            {
                                return false;
                            }

                            _tabs.SwitchToTab(i);
                            didSwitch = true;
                            break;
                        }
                    }
                    if (!didSwitch)
                    {
                        throw new InvalidOperationException($"No such window handle in tab manager: {windowHandle}");
                    }
                    return true;
                }

                throw new InvalidOperationException($"Invalid window handle format: {windowHandle}");
            });

            if (!switched)
            {
                return;
            }

            await RunOnMainThread(async () =>
            {
                // Switching top-level browsing context resets frame focus to top-level.
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host != null)
                {
                    await host.SwitchToFrameAsync(null);
                }
            });
        }

        public async Task SwitchToFrameAsync(object frameReference)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host != null)
                {
                    await host.SwitchToFrameAsync(frameReference);
                }
            });
        }

        public async Task SwitchToParentFrameAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host != null)
                {
                    await host.SwitchToParentFrameAsync();
                }
            });
        }

        public async Task<IReadOnlyList<WdCookie>> GetAllCookiesAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return (IReadOnlyList<WdCookie>)Array.Empty<WdCookie>();
                var cookies = await host.GetAllCookiesAsync();
                return cookies.Select(ToWdCookie).ToList();
            });
        }

        public async Task<WdCookie> GetNamedCookieAsync(string name)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return null;
                var cookie = await host.GetCookieAsync(name);
                return cookie == null ? null : ToWdCookie(cookie);
            });
        }

        public async Task AddCookieAsync(WdCookie cookie)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || cookie == null) return;
                await host.AddCookieAsync(ToEngineCookie(cookie));
            });
        }

        public async Task DeleteCookieAsync(string name)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                await host.DeleteCookieAsync(name);
            });
        }

        public async Task DeleteAllCookiesAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                await host.DeleteAllCookiesAsync();
            });
        }

        public async Task PerformActionsAsync(IReadOnlyList<WdActionSequence> actions)
        {
            await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                var mapped = (actions ?? Array.Empty<WdActionSequence>()).Select(ToEngineActionChain).ToList();
                await host.PerformActionsAsync(mapped);
            });
        }

        public async Task ReleaseActionsAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                await host.ReleaseActionsAsync();
            });
        }

        public async Task<bool> HasAlertAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return false;
                return await host.HasAlertAsync();
            });
        }

        public async Task DismissAlertAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                await host.DismissAlertAsync();
            });
        }

        public async Task AcceptAlertAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                await host.AcceptAlertAsync();
            });
        }

        public async Task<string> GetAlertTextAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return string.Empty;
                return await host.GetAlertTextAsync();
            });
        }

        public async Task SendAlertTextAsync(string text)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                await host.SendAlertTextAsync(text ?? string.Empty);
            });
        }

        public void SetUnhandledPromptBehavior(string behavior)
        {
            RunOnMainThread(() =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                host?.SetUnhandledPromptBehavior(behavior);
            }).GetAwaiter().GetResult();
        }

        public bool HasValidCurrentBrowsingContext()
        {
            var host = _tabs.ActiveTab?.Browser?.Host;
            return host != null && host.HasValidCurrentBrowsingContext();
        }

        private static WdCookie ToWdCookie(FenBrowser.FenEngine.Rendering.WebDriverCookie cookie)
        {
            return new WdCookie
            {
                Name = cookie?.Name ?? string.Empty,
                Value = cookie?.Value ?? string.Empty,
                Path = cookie?.Path ?? "/",
                Domain = cookie?.Domain ?? string.Empty,
                Secure = cookie?.Secure ?? false,
                HttpOnly = cookie?.HttpOnly ?? false,
                Expiry = cookie?.Expiry,
                SameSite = cookie?.SameSite
            };
        }

        private static FenBrowser.FenEngine.Rendering.WebDriverCookie ToEngineCookie(WdCookie cookie)
        {
            return new FenBrowser.FenEngine.Rendering.WebDriverCookie
            {
                Name = cookie?.Name ?? string.Empty,
                Value = cookie?.Value ?? string.Empty,
                Path = cookie?.Path ?? "/",
                Domain = cookie?.Domain ?? string.Empty,
                Secure = cookie?.Secure ?? false,
                HttpOnly = cookie?.HttpOnly ?? false,
                Expiry = cookie?.Expiry,
                SameSite = cookie?.SameSite
            };
        }

        private static ActionChain ToEngineActionChain(WdActionSequence sequence)
        {
            var chain = new ActionChain
            {
                Type = sequence?.Type ?? string.Empty,
                Id = sequence?.Id ?? string.Empty
            };

            if (sequence?.Actions != null)
            {
                foreach (var action in sequence.Actions)
                {
                    chain.Actions.Add(new InputAction
                    {
                        Type = action?.Type ?? string.Empty,
                        Duration = action?.Duration ?? 0,
                        X = action?.X ?? 0,
                        Y = action?.Y ?? 0,
                        Button = action?.Button ?? 0,
                        Value = action?.Value ?? string.Empty,
                        Origin = action?.Origin ?? string.Empty
                    });
                }
            }

            return chain;
        }

        private Task<T> RunOnMainThread<T>(Func<T> func) => WindowManager.Instance.RunOnMainThread(func);
        private Task RunOnMainThread(Action action) => WindowManager.Instance.RunOnMainThread(action);
        private async Task RunOnMainThread(Func<Task> func)
        {
            var task = await WindowManager.Instance.RunOnMainThread(func).ConfigureAwait(false);
            if (task != null)
            {
                await task.ConfigureAwait(false);
            }
        }

        private async Task<T> RunOnMainThread<T>(Func<Task<T>> func)
        {
            var task = await WindowManager.Instance.RunOnMainThread(func).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
    }
}
