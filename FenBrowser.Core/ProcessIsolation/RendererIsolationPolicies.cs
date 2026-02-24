using System;
using System.Collections.Generic;

namespace FenBrowser.Core.ProcessIsolation
{
    /// <summary>
    /// Defines how renderer assignment keys are derived for process isolation decisions.
    /// </summary>
    public static class OriginIsolationPolicy
    {
        public static bool TryGetAssignmentKey(string url, out string key)
        {
            key = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (IsSupportedNetworkScheme(uri))
            {
                var host = uri.Host?.ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(host))
                {
                    return false;
                }

                var port = uri.IsDefaultPort ? GetDefaultPort(uri.Scheme) : uri.Port;
                key = $"{uri.Scheme.ToLowerInvariant()}://{host}:{port}";
                return true;
            }

            if (uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                key = "file://local";
                return true;
            }

            if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
            {
                var aboutToken = GetAboutToken(url);
                key = $"about://{aboutToken}";
                return true;
            }

            if (uri.IsAbsoluteUri && !string.IsNullOrWhiteSpace(uri.Scheme))
            {
                key = $"opaque://{uri.Scheme.ToLowerInvariant()}";
                return true;
            }

            return false;
        }

        public static bool RequiresReassignment(string currentAssignmentKey, string requestedUrl)
        {
            if (!TryGetAssignmentKey(requestedUrl, out var requestedKey))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(currentAssignmentKey))
            {
                return false;
            }

            return !string.Equals(currentAssignmentKey, requestedKey, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedNetworkScheme(Uri uri)
        {
            if (uri == null)
            {
                return false;
            }

            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetDefaultPort(string scheme)
        {
            if (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return 443;
            }

            return 80;
        }

        private static string GetAboutToken(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return "blank";
            }

            const string prefix = "about:";
            if (!rawUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return "blank";
            }

            var token = rawUrl.Substring(prefix.Length);
            var marker = token.IndexOfAny(new[] { '?', '#' });
            if (marker >= 0)
            {
                token = token.Substring(0, marker);
            }

            token = token.Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(token) ? "blank" : token;
        }
    }

    /// <summary>
    /// Crash restart backoff policy for renderer child processes.
    /// </summary>
    public sealed class RendererRestartPolicy
    {
        public int MaxRestartAttempts { get; }
        public int BaseBackoffMs { get; }
        public int MaxBackoffMs { get; }
        public int StableSessionResetMs { get; }
        public int CrashWindowMs { get; }
        public int MaxCrashCountInWindow { get; }
        public int QuarantineMs { get; }

        public RendererRestartPolicy(
            int maxRestartAttempts = 3,
            int baseBackoffMs = 250,
            int maxBackoffMs = 5000,
            int stableSessionResetMs = 60000,
            int crashWindowMs = 60000,
            int maxCrashCountInWindow = 4,
            int quarantineMs = 30000)
        {
            MaxRestartAttempts = maxRestartAttempts < 0 ? 0 : maxRestartAttempts;
            BaseBackoffMs = baseBackoffMs < 0 ? 0 : baseBackoffMs;
            MaxBackoffMs = maxBackoffMs < BaseBackoffMs ? BaseBackoffMs : maxBackoffMs;
            StableSessionResetMs = stableSessionResetMs < 0 ? 0 : stableSessionResetMs;
            CrashWindowMs = crashWindowMs < 0 ? 0 : crashWindowMs;
            MaxCrashCountInWindow = maxCrashCountInWindow < 0 ? 0 : maxCrashCountInWindow;
            QuarantineMs = quarantineMs < 0 ? 0 : quarantineMs;
        }

        public bool CanRestart(int attemptsSoFar)
        {
            return attemptsSoFar < MaxRestartAttempts;
        }

        public int GetBackoffDelayMs(int attemptsSoFar)
        {
            if (BaseBackoffMs <= 0)
            {
                return 0;
            }

            if (attemptsSoFar <= 0)
            {
                return BaseBackoffMs;
            }

            var multiplier = Math.Pow(2, attemptsSoFar);
            var delay = (int)Math.Round(BaseBackoffMs * multiplier);
            if (delay < BaseBackoffMs)
            {
                delay = BaseBackoffMs;
            }

            return delay > MaxBackoffMs ? MaxBackoffMs : delay;
        }

        public bool ShouldResetAttempts(long sessionUptimeMs)
        {
            return StableSessionResetMs > 0 && sessionUptimeMs >= StableSessionResetMs;
        }

        public bool ShouldQuarantine(int crashCountInWindow)
        {
            if (QuarantineMs <= 0 || CrashWindowMs <= 0 || MaxCrashCountInWindow <= 0)
            {
                return false;
            }

            return crashCountInWindow > MaxCrashCountInWindow;
        }
    }

    public sealed class NavigationIsolationDecision
    {
        public bool HasValidAssignment { get; set; }
        public string PreviousAssignmentKey { get; set; }
        public string RequestedAssignmentKey { get; set; }
        public bool RequiresReassignment { get; set; }
    }

    public sealed class RendererExitDecision
    {
        public bool IsExpectedExit { get; set; }
        public bool ShouldRestart { get; set; }
        public int RestartDelayMs { get; set; }
        public int RestartAttempt { get; set; }
        public int RetryAfterMs { get; set; }
        public string ReplayUrl { get; set; }
        public bool ReplayIsUserInput { get; set; }
        public bool IsCrashLoopQuarantined { get; set; }
        public string Reason { get; set; } = "unknown";
    }

    /// <summary>
    /// Pure policy state machine for per-tab renderer assignment and restart decisions.
    /// Host coordinators should delegate policy decisions to this registry.
    /// </summary>
    public sealed class RendererTabIsolationRegistry
    {
        private readonly RendererRestartPolicy _restartPolicy;
        private readonly Dictionary<int, TabIsolationState> _tabs = new();

        public RendererTabIsolationRegistry(RendererRestartPolicy restartPolicy = null)
        {
            _restartPolicy = restartPolicy ?? new RendererRestartPolicy();
        }

        public void EnsureTab(int tabId)
        {
            if (tabId <= 0)
            {
                return;
            }

            if (!_tabs.ContainsKey(tabId))
            {
                _tabs[tabId] = new TabIsolationState();
            }
        }

        public NavigationIsolationDecision ApplyNavigation(int tabId, string url, bool isUserInput)
        {
            if (tabId <= 0)
            {
                return new NavigationIsolationDecision
                {
                    HasValidAssignment = false,
                    RequiresReassignment = false
                };
            }

            EnsureTab(tabId);
            var state = _tabs[tabId];
            state.LastNavigationUrl = url;
            state.LastNavigationIsUserInput = isUserInput;
            if (isUserInput && state.QuarantinedUntilUnixMs > 0)
            {
                state.QuarantinedUntilUnixMs = 0;
                state.UnexpectedCrashTimestamps.Clear();
            }

            var decision = new NavigationIsolationDecision
            {
                PreviousAssignmentKey = state.AssignmentKey,
                RequestedAssignmentKey = state.AssignmentKey,
                HasValidAssignment = false,
                RequiresReassignment = false
            };

            if (!OriginIsolationPolicy.TryGetAssignmentKey(url, out var requestedKey))
            {
                return decision;
            }

            decision.HasValidAssignment = true;
            decision.RequestedAssignmentKey = requestedKey;

            if (string.IsNullOrWhiteSpace(state.AssignmentKey))
            {
                state.AssignmentKey = requestedKey;
                return decision;
            }

            if (!string.Equals(state.AssignmentKey, requestedKey, StringComparison.OrdinalIgnoreCase))
            {
                decision.RequiresReassignment = true;
                decision.PreviousAssignmentKey = state.AssignmentKey;
                state.AssignmentKey = requestedKey;
            }

            return decision;
        }

        public void MarkSessionStarted(int tabId, int pid)
        {
            if (!_tabs.TryGetValue(tabId, out var state))
            {
                return;
            }

            state.ActivePid = pid;
            state.ExpectedExitPid = 0;
            state.SessionStartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public void MarkExpectedExit(int tabId, int pid)
        {
            if (!_tabs.TryGetValue(tabId, out var state))
            {
                return;
            }

            state.ExpectedExitPid = pid;
        }

        public RendererExitDecision HandleSessionExit(int tabId, int pid, bool isShuttingDown)
        {
            var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (isShuttingDown)
            {
                return new RendererExitDecision
                {
                    IsExpectedExit = true,
                    ShouldRestart = false,
                    Reason = "host-shutdown"
                };
            }

            if (!_tabs.TryGetValue(tabId, out var state))
            {
                return new RendererExitDecision
                {
                    IsExpectedExit = true,
                    ShouldRestart = false,
                    Reason = "tab-missing"
                };
            }

            if (state.IsClosed)
            {
                return new RendererExitDecision
                {
                    IsExpectedExit = true,
                    ShouldRestart = false,
                    Reason = "tab-closed"
                };
            }

            if (state.ExpectedExitPid == pid)
            {
                if (state.ActivePid == pid)
                {
                    state.ActivePid = 0;
                }

                state.ExpectedExitPid = 0;
                return new RendererExitDecision
                {
                    IsExpectedExit = true,
                    ShouldRestart = false,
                    Reason = "expected-exit"
                };
            }

            if (state.ActivePid != pid)
            {
                return new RendererExitDecision
                {
                    IsExpectedExit = true,
                    ShouldRestart = false,
                    Reason = "stale-exit"
                };
            }

            if (state.SessionStartedAtUnixMs > 0)
            {
                var uptimeMs = nowUnixMs - state.SessionStartedAtUnixMs;
                if (_restartPolicy.ShouldResetAttempts(uptimeMs))
                {
                    state.RestartAttempts = 0;
                }
            }

            PruneCrashWindow(state, nowUnixMs);
            state.UnexpectedCrashTimestamps.Enqueue(nowUnixMs);
            if (_restartPolicy.ShouldQuarantine(state.UnexpectedCrashTimestamps.Count))
            {
                state.ActivePid = 0;
                state.QuarantinedUntilUnixMs = nowUnixMs + _restartPolicy.QuarantineMs;
                return new RendererExitDecision
                {
                    IsExpectedExit = false,
                    ShouldRestart = false,
                    IsCrashLoopQuarantined = true,
                    RetryAfterMs = _restartPolicy.QuarantineMs,
                    Reason = "quarantined-crash-loop",
                    ReplayUrl = state.LastNavigationUrl,
                    ReplayIsUserInput = state.LastNavigationIsUserInput
                };
            }

            if (!_restartPolicy.CanRestart(state.RestartAttempts))
            {
                state.ActivePid = 0;
                return new RendererExitDecision
                {
                    IsExpectedExit = false,
                    ShouldRestart = false,
                    Reason = "restart-budget-exhausted",
                    ReplayUrl = state.LastNavigationUrl,
                    ReplayIsUserInput = state.LastNavigationIsUserInput
                };
            }

            var delayMs = _restartPolicy.GetBackoffDelayMs(state.RestartAttempts);
            state.RestartAttempts++;
            state.ActivePid = 0;

            return new RendererExitDecision
            {
                IsExpectedExit = false,
                ShouldRestart = true,
                RestartDelayMs = delayMs,
                RestartAttempt = state.RestartAttempts,
                ReplayUrl = state.LastNavigationUrl,
                ReplayIsUserInput = state.LastNavigationIsUserInput,
                Reason = "unexpected-exit-restart"
            };
        }

        public void CloseTab(int tabId)
        {
            if (_tabs.TryGetValue(tabId, out var state))
            {
                state.IsClosed = true;
            }
        }

        public bool CanStartSession(int tabId, out int retryAfterMs, out string reason)
        {
            retryAfterMs = 0;
            reason = "allowed";

            if (tabId <= 0)
            {
                reason = "invalid-tab";
                return false;
            }

            EnsureTab(tabId);
            if (!_tabs.TryGetValue(tabId, out var state))
            {
                reason = "tab-missing";
                return false;
            }

            if (state.IsClosed)
            {
                reason = "tab-closed";
                return false;
            }

            if (state.QuarantinedUntilUnixMs <= 0)
            {
                return true;
            }

            var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowUnixMs >= state.QuarantinedUntilUnixMs)
            {
                state.QuarantinedUntilUnixMs = 0;
                state.UnexpectedCrashTimestamps.Clear();
                return true;
            }

            var remaining = state.QuarantinedUntilUnixMs - nowUnixMs;
            retryAfterMs = remaining <= 0 ? 0 : (int)Math.Min(int.MaxValue, remaining);
            reason = "quarantined-crash-loop";
            return false;
        }

        public bool TryGetSnapshot(int tabId, out TabIsolationSnapshot snapshot)
        {
            snapshot = null;
            if (!_tabs.TryGetValue(tabId, out var state))
            {
                return false;
            }

            snapshot = new TabIsolationSnapshot
            {
                AssignmentKey = state.AssignmentKey,
                ActivePid = state.ActivePid,
                ExpectedExitPid = state.ExpectedExitPid,
                RestartAttempts = state.RestartAttempts,
                LastNavigationUrl = state.LastNavigationUrl,
                LastNavigationIsUserInput = state.LastNavigationIsUserInput,
                IsClosed = state.IsClosed,
                SessionStartedAtUnixMs = state.SessionStartedAtUnixMs,
                QuarantinedUntilUnixMs = state.QuarantinedUntilUnixMs,
                CrashCountInWindow = state.UnexpectedCrashTimestamps.Count
            };
            return true;
        }

        private void PruneCrashWindow(TabIsolationState state, long nowUnixMs)
        {
            if (state == null)
            {
                return;
            }

            if (_restartPolicy.CrashWindowMs <= 0)
            {
                state.UnexpectedCrashTimestamps.Clear();
                return;
            }

            while (state.UnexpectedCrashTimestamps.Count > 0 &&
                   nowUnixMs - state.UnexpectedCrashTimestamps.Peek() > _restartPolicy.CrashWindowMs)
            {
                state.UnexpectedCrashTimestamps.Dequeue();
            }
        }

        private sealed class TabIsolationState
        {
            public string AssignmentKey { get; set; }
            public int ActivePid { get; set; }
            public int ExpectedExitPid { get; set; }
            public int RestartAttempts { get; set; }
            public string LastNavigationUrl { get; set; }
            public bool LastNavigationIsUserInput { get; set; }
            public bool IsClosed { get; set; }
            public long SessionStartedAtUnixMs { get; set; }
            public long QuarantinedUntilUnixMs { get; set; }
            public Queue<long> UnexpectedCrashTimestamps { get; } = new();
        }

        public sealed class TabIsolationSnapshot
        {
            public string AssignmentKey { get; set; }
            public int ActivePid { get; set; }
            public int ExpectedExitPid { get; set; }
            public int RestartAttempts { get; set; }
            public string LastNavigationUrl { get; set; }
            public bool LastNavigationIsUserInput { get; set; }
            public bool IsClosed { get; set; }
            public long SessionStartedAtUnixMs { get; set; }
            public long QuarantinedUntilUnixMs { get; set; }
            public int CrashCountInWindow { get; set; }
        }
    }
}
