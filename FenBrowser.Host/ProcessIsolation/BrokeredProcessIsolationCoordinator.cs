using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Platform;
using FenBrowser.Core.ProcessIsolation;
using FenBrowser.Core.Security.Sandbox;
using FenBrowser.Host.Tabs;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Brokered process model: host keeps UI/process brokering and spawns a per-tab renderer child process.
    /// Child process wiring is intentionally minimal but establishes a concrete OS process boundary.
    /// </summary>
    public sealed class BrokeredProcessIsolationCoordinator : IProcessIsolationCoordinator
    {
        private readonly ConcurrentDictionary<int, TabProcessState> _tabStates = new();
        private readonly int _parentPid = Environment.ProcessId;
        private readonly RendererRestartPolicy _restartPolicy;
        private readonly RendererTabIsolationRegistry _isolationRegistry;
        private readonly string _assignmentPolicy = "origin-strict";
        private readonly TimeSpan _rendererReadyTimeout;
        private volatile bool _isShuttingDown;

        public string Mode => "brokered";
        public bool UsesOutOfProcessRenderer => true;

        public event Action<int, RendererFrameReadyPayload> FrameReceived;
        public event Action<int, string> RendererCrashed;

        public BrokeredProcessIsolationCoordinator()
        {
            _restartPolicy = new RendererRestartPolicy(
                maxRestartAttempts: ParseIntEnv("FEN_RENDERER_MAX_RESTARTS", 3),
                baseBackoffMs: ParseIntEnv("FEN_RENDERER_RESTART_BACKOFF_MS", 250),
                maxBackoffMs: ParseIntEnv("FEN_RENDERER_RESTART_MAX_BACKOFF_MS", 5000),
                stableSessionResetMs: ParseIntEnv("FEN_RENDERER_STABLE_RESET_MS", 60000),
                crashWindowMs: ParseIntEnv("FEN_RENDERER_CRASH_WINDOW_MS", 60000),
                maxCrashCountInWindow: ParseIntEnv("FEN_RENDERER_MAX_CRASHES_IN_WINDOW", 4),
                quarantineMs: ParseIntEnv("FEN_RENDERER_CRASH_QUARANTINE_MS", 30000));
            _isolationRegistry = new RendererTabIsolationRegistry(_restartPolicy);
            _rendererReadyTimeout = TimeSpan.FromMilliseconds(ParseIntEnv("FEN_RENDERER_READY_TIMEOUT_MS", 5000));
        }

        public void Initialize()
        {
            FenLogger.Info(
                $"[ProcessIsolation] Mode=brokered (per-tab renderer child process enabled, assignment={_assignmentPolicy}, maxRestarts={_restartPolicy.MaxRestartAttempts}, stableResetMs={_restartPolicy.StableSessionResetMs}, crashWindowMs={_restartPolicy.CrashWindowMs}, crashWindowLimit={_restartPolicy.MaxCrashCountInWindow}, quarantineMs={_restartPolicy.QuarantineMs})",
                LogCategory.General);
        }

        public void OnTabCreated(BrowserTab tab)
        {
            if (tab == null)
                return;

            var state = new TabProcessState(tab);
            _tabStates[tab.Id] = state;
            _isolationRegistry.EnsureTab(tab.Id);

            if (!TryStartSession(state, restartAttempt: 0, restartReason: "tab-created"))
            {
                FenLogger.Warn($"[ProcessIsolation] Initial renderer spawn failed for tab {tab.Id}; will retry on next navigation.", LogCategory.General);
                return;
            }
        }

        public void OnTabActivated(BrowserTab tab)
        {
            if (tab == null)
                return;

            if (_tabStates.TryGetValue(tab.Id, out var state))
            {
                var session = state.Session;
                var process = session?.ChildProcess;
                if (process != null && !process.HasExited)
                {
                    FenLogger.Debug(
                        $"[ProcessIsolation] Activated tab {tab.Id} backed by renderer pid={process.Id}, assignment={state.AssignmentKey ?? "<none>"}",
                        LogCategory.General);
                }
                session?.SendTabActivated();
            }
        }

        public void OnNavigationRequested(BrowserTab tab, string url, bool isUserInput)
        {
            if (tab == null || string.IsNullOrWhiteSpace(url))
                return;

            if (!_tabStates.TryGetValue(tab.Id, out var state))
            {
                return;
            }

            var navDecision = _isolationRegistry.ApplyNavigation(tab.Id, url, isUserInput);
            if (navDecision.HasValidAssignment)
            {
                if (navDecision.RequiresReassignment)
                {
                    FenLogger.Info(
                        $"[ProcessIsolation] Reassigning renderer process for tab {tab.Id}: {navDecision.PreviousAssignmentKey} -> {navDecision.RequestedAssignmentKey}",
                        LogCategory.General);
                    RecycleSessionForAssignmentChange(state, navDecision.RequestedAssignmentKey);
                }

                state.AssignmentKey = navDecision.RequestedAssignmentKey;
            }

            var session = state.Session;
            if (session == null)
            {
                if (!TryStartSession(state, restartAttempt: 0, restartReason: "navigation"))
                {
                    return;
                }
                session = state.Session;
            }

            session?.SendNavigate(url, isUserInput);
        }

        private void RecycleSessionForAssignmentChange(TabProcessState state, string newAssignment)
        {
            if (state == null)
            {
                return;
            }

            var oldSession = state.Session;
            state.AssignmentKey = newAssignment;
            if (oldSession != null)
            {
                MarkExpectedExit(state, oldSession);
                oldSession.SendShutdown();
                StopSession(oldSession, $"tab {state.TabId} assignment change");
            }

            state.Session = null;
            state.ActivePid = 0;

            _ = TryStartSession(state, restartAttempt: 0, restartReason: "assignment-change");
        }

        public void OnInputEvent(BrowserTab tab, RendererInputEvent inputEvent)
        {
            if (tab == null || inputEvent == null)
                return;

            if (_tabStates.TryGetValue(tab.Id, out var state))
            {
                state.Session?.SendInput(inputEvent);
            }
        }

        public void OnFrameRequested(BrowserTab tab, float viewportWidth, float viewportHeight)
        {
            if (tab == null)
                return;

            if (_tabStates.TryGetValue(tab.Id, out var state))
            {
                state.Session?.SendFrameRequest(viewportWidth, viewportHeight);
            }
        }

        public void OnTabClosed(BrowserTab tab)
        {
            if (tab == null)
                return;

            if (_tabStates.TryRemove(tab.Id, out var state))
            {
                state.IsClosed = true;
                _isolationRegistry.CloseTab(tab.Id);
                var session = state.Session;
                if (session != null)
                {
                    MarkExpectedExit(state, session);
                    session.SendTabClosed();
                    session.SendShutdown();
                    StopSession(session, $"tab {tab.Id} closed");
                }

                // Dispose the OS sandbox — this also kills remaining processes via KILL_ON_JOB_CLOSE.
                state.Sandbox?.Dispose();
                state.Sandbox = null;
            }
        }

        public void Shutdown()
        {
            _isShuttingDown = true;

            foreach (var kvp in _tabStates)
            {
                var state = kvp.Value;
                state.IsClosed = true;
                _isolationRegistry.CloseTab(state.TabId);
                if (state.Session != null)
                {
                    MarkExpectedExit(state, state.Session);
                    state.Session.SendShutdown();
                    StopSession(state.Session, "host shutdown");
                }

                state.Sandbox?.Dispose();
                state.Sandbox = null;
            }

            _tabStates.Clear();
        }

        private bool TryStartSession(TabProcessState state, int restartAttempt, string restartReason)
        {
            if (state == null || state.IsClosed)
            {
                return false;
            }

            if (!_isolationRegistry.CanStartSession(state.TabId, out var retryAfterMs, out var denyReason))
            {
                var retrySuffix = retryAfterMs > 0 ? $" retryAfterMs={retryAfterMs}" : string.Empty;
                FenLogger.Warn(
                    $"[ProcessIsolation] Start denied for tab {state.TabId} (reason={denyReason}{retrySuffix})",
                    LogCategory.General);
                return false;
            }

            var pipeName = $"fen_renderer_{_parentPid}_{state.TabId}_{Guid.NewGuid():N}";
            var token = CreateAuthToken();
            var session = new RendererChildSession(state.TabId, pipeName, token);
            session.FrameReceived += (tabId, payload) => FrameReceived?.Invoke(tabId, payload);

            var process = StartRendererChildWithSandbox(state.TabId, pipeName, token, state.AssignmentKey, out var sandbox);
            if (process == null)
            {
                sandbox?.Dispose();
                session.Dispose();
                return false;
            }

            // Dispose the previous sandbox if one exists (e.g. after a crash restart).
            state.Sandbox?.Dispose();
            state.Sandbox = sandbox;

            process.EnableRaisingEvents = true;
            var startedPid = process.Id;
            process.Exited += (_, __) => HandleChildProcessExit(state.TabId, startedPid);

            session.AttachProcess(process);
            var ready = session.WaitForReadyAsync(_rendererReadyTimeout).GetAwaiter().GetResult();
            if (!ready)
            {
                FenLogger.Error(
                    $"[ProcessIsolation] Renderer child failed startup contract for tab {state.TabId} (pid={startedPid}, readyTimeoutMs={(int)_rendererReadyTimeout.TotalMilliseconds}).",
                    LogCategory.General);
                StopSession(session, $"tab {state.TabId} startup contract failure");
                sandbox?.Dispose();
                return false;
            }

            state.Session = session;
            state.ActivePid = startedPid;
            _isolationRegistry.MarkSessionStarted(state.TabId, startedPid);

            FenLogger.Info(
                $"[ProcessIsolation] Renderer child started for tab {state.TabId} (pid={startedPid}, pipe={pipeName}, assignment={state.AssignmentKey ?? "<none>"}, reason={restartReason})",
                LogCategory.General);
            return true;
        }

        private void HandleChildProcessExit(int tabId, int exitedPid)
        {
            if (_isShuttingDown)
            {
                return;
            }

            if (!_tabStates.TryGetValue(tabId, out var state))
            {
                return;
            }

            var exitDecision = _isolationRegistry.HandleSessionExit(tabId, exitedPid, _isShuttingDown);
            if (exitDecision.IsExpectedExit)
            {
                return;
            }

            if (!exitDecision.ShouldRestart)
            {
                var retryAfterSuffix = exitDecision.RetryAfterMs > 0 ? $", retryAfterMs={exitDecision.RetryAfterMs}" : string.Empty;
                FenLogger.Warn(
                    $"[ProcessIsolation] Renderer crash restart unavailable for tab {tabId} (pid={exitedPid}, reason={exitDecision.Reason}{retryAfterSuffix}).",
                    LogCategory.General);
                    
                // Fire crash event to UI
                RendererCrashed?.Invoke(tabId, exitDecision.Reason);
                return;
            }

            FenLogger.Warn(
                $"[ProcessIsolation] Renderer child exited unexpectedly for tab {tabId} (pid={exitedPid}). Restart attempt {exitDecision.RestartAttempt}/{_restartPolicy.MaxRestartAttempts} in {exitDecision.RestartDelayMs}ms.",
                LogCategory.General);

            _ = Task.Run(async () =>
            {
                if (exitDecision.RestartDelayMs > 0)
                {
                    await Task.Delay(exitDecision.RestartDelayMs).ConfigureAwait(false);
                }

                if (_isShuttingDown || state.IsClosed)
                {
                    return;
                }

                // Another coordinator path already recovered this tab; do not clobber active session.
                if (state.ActivePid != 0)
                {
                    return;
                }

                if (state.Session != null)
                {
                    var existingPid = state.Session.ChildProcess?.Id ?? 0;
                    if (existingPid != 0 && existingPid != exitedPid)
                    {
                        return;
                    }

                    StopSession(state.Session, $"tab {tabId} crash cleanup");
                }

                state.Session = null;
                state.ActivePid = 0;

                if (!TryStartSession(state, exitDecision.RestartAttempt, "crash-restart"))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(exitDecision.ReplayUrl))
                {
                    state.Session?.SendNavigate(exitDecision.ReplayUrl, exitDecision.ReplayIsUserInput);
                }
            });
        }

        private Process StartRendererChild(int tabId, string pipeName, string authToken, string assignmentKey)
        {
            return StartRendererChildWithSandbox(tabId, pipeName, authToken, assignmentKey, out _);
        }

        private Process StartRendererChildWithSandbox(int tabId, string pipeName, string authToken, string assignmentKey, out ISandbox sandbox)
        {
            sandbox = null;
            try
            {
                var allowUnsandboxedFallback = string.Equals(
                    Environment.GetEnvironmentVariable("FEN_RENDERER_ALLOW_UNSANDBOXED"),
                    "1",
                    StringComparison.OrdinalIgnoreCase);
                using var launchScope = FenLogger.BeginScope(
                    component: "RendererProcessLauncher",
                    data: new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["tabId"] = tabId,
                        ["assignmentKey"] = assignmentKey ?? string.Empty,
                        ["allowUnsandboxedFallback"] = allowUnsandboxedFallback
                    });
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (string.IsNullOrWhiteSpace(exePath))
                {
                    FenLogger.Warn("[ProcessIsolation] Could not resolve host executable path for brokered child launch.", LogCategory.ProcessIsolation);
                    return null;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--renderer-child --tab-id={tabId}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.Environment["FEN_RENDERER_CHILD"] = "1";
                startInfo.Environment["FEN_RENDERER_TAB_ID"] = tabId.ToString();
                startInfo.Environment["FEN_RENDERER_PARENT_PID"] = _parentPid.ToString();
                startInfo.Environment["FEN_RENDERER_PIPE_NAME"] = pipeName;
                startInfo.Environment["FEN_RENDERER_AUTH_TOKEN"] = authToken;
                startInfo.Environment["FEN_RENDERER_SANDBOX_PROFILE"] = "renderer_minimal";
                startInfo.Environment["FEN_RENDERER_CAPABILITIES"] = "navigate,input,frame";
                startInfo.Environment["FEN_RENDERER_ASSIGNMENT_KEY"] = assignmentKey ?? string.Empty;

                // Obtain the OS sandbox for the renderer profile.
                var sandboxFactory = PlatformLayerFactory.GetInstance().CreateSandboxFactory();
                if (!SandboxLaunchPolicy.TryAcquire(
                    $"renderer tab {tabId}",
                    sandboxFactory,
                    OsSandboxProfile.RendererMinimal,
                    allowUnsandboxedFallback,
                    "FEN_RENDERER_ALLOW_UNSANDBOXED",
                    out var rendererSandbox))
                {
                    return null;
                }

                rendererSandbox?.ApplyToProcessStartInfo(startInfo);

                Process process;

                if (rendererSandbox != null && rendererSandbox.RequiresCustomSpawn)
                {
                    // AppContainer (and any future sandbox requiring CreateProcessW) path.
                    try
                    {
                        process = rendererSandbox.SpawnProcess(startInfo);
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Error(
                            $"[ProcessIsolation] Sandbox.SpawnProcess failed for tab {tabId}: {ex.Message}",
                            LogCategory.ProcessIsolation);
                        rendererSandbox.Dispose();
                        rendererSandbox = null;
                        if (!allowUnsandboxedFallback)
                        {
                            FenLogger.Error(
                                $"[ProcessIsolation] Refusing unsandboxed renderer retry for tab {tabId}. Set FEN_RENDERER_ALLOW_UNSANDBOXED=1 to override.",
                                LogCategory.ProcessIsolation);
                            return null;
                        }
                        process = Process.Start(startInfo);
                    }
                }
                else
                {
                    // Standard .NET process start (Job Object sandbox or NullSandbox).
                    if (rendererSandbox == null && !allowUnsandboxedFallback)
                    {
                        FenLogger.Error(
                            $"[ProcessIsolation] Refusing renderer launch for tab {tabId} because no sandbox is active. Set FEN_RENDERER_ALLOW_UNSANDBOXED=1 to override.",
                            LogCategory.ProcessIsolation);
                        return null;
                    }

                    process = Process.Start(startInfo);

                    if (process != null && rendererSandbox != null)
                    {
                        try
                        {
                            rendererSandbox.AttachToProcess(process);
                        }
                        catch (Exception ex)
                        {
                            FenLogger.Warn(
                                $"[ProcessIsolation] Sandbox.AttachToProcess failed for tab {tabId} pid={process.Id}: {ex.Message}",
                                LogCategory.ProcessIsolation);
                            if (!allowUnsandboxedFallback)
                            {
                                try { process.Kill(entireProcessTree: true); } catch { }
                                rendererSandbox.Dispose();
                                return null;
                            }
                        }
                    }
                }

                if (process != null && rendererSandbox != null)
                {
                    FenLogger.Info(
                        $"[ProcessIsolation] Renderer child sandboxed via '{rendererSandbox.ProfileName}' for tab {tabId}.",
                        LogCategory.ProcessIsolation);
                    sandbox = rendererSandbox;
                }
                else
                {
                    rendererSandbox?.Dispose();
                }

                return process;
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[ProcessIsolation] Failed to start renderer child for tab {tabId}: {ex.Message}", LogCategory.ProcessIsolation);
                return null;
            }
        }

        private static void StopSession(RendererChildSession session, string reason)
        {
            if (session == null)
                return;

            var process = session.ChildProcess;
            if (process == null)
            {
                session.Dispose();
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[ProcessIsolation] Failed to stop renderer child ({reason}): {ex.Message}", LogCategory.General);
            }
            finally
            {
                try { process.Dispose(); } catch { }
                session.Dispose();
            }
        }

        private void MarkExpectedExit(TabProcessState state, RendererChildSession session)
        {
            if (state == null || session == null)
            {
                return;
            }

            var process = session.ChildProcess;
            if (process != null)
            {
                state.ActivePid = process.Id;
                _isolationRegistry.MarkExpectedExit(state.TabId, process.Id);
            }
        }

        private static int ParseIntEnv(string key, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (int.TryParse(raw, out var parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static string CreateAuthToken()
        {
            Span<byte> bytes = stackalloc byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private sealed class TabProcessState
        {
            public int TabId { get; }
            public RendererChildSession Session { get; set; }
            public int ActivePid { get; set; }
            public bool IsClosed { get; set; }
            public string AssignmentKey { get; set; }

            /// <summary>
            /// The OS-level sandbox applied to the renderer child process.
            /// Disposed when the tab is closed or the renderer crashes and is not restarted.
            /// </summary>
            public ISandbox Sandbox { get; set; }

            public TabProcessState(BrowserTab tab)
            {
                TabId = tab.Id;
            }
        }
    }
}
