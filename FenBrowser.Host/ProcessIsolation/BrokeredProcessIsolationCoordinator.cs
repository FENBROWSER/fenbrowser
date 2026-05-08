// SpecRef: FenBrowser sandbox policy and brokered startup fail-closed contract
// CapabilityId: PROCESS-SANDBOX-FAILCLOSED-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
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
        private readonly string _assignmentPolicy;
        private readonly RendererAssignmentPolicyMode _assignmentPolicyMode;
        private readonly TimeSpan _rendererReadyTimeout;
        private readonly RendererProcessPool _rendererProcessPool;
        private volatile bool _isShuttingDown;

        public string Mode => "brokered";
        public bool UsesOutOfProcessRenderer => true;

        public event Action<int, RendererFrameReadyPayload> FrameReceived;
        public event Action<int, string> RendererCrashed;

        public BrokeredProcessIsolationCoordinator()
        {
            (_assignmentPolicyMode, _assignmentPolicy) = ParseAssignmentPolicy(
                Environment.GetEnvironmentVariable("FEN_RENDERER_ASSIGNMENT_POLICY"));
            _restartPolicy = new RendererRestartPolicy(
                maxRestartAttempts: ParseIntEnv("FEN_RENDERER_MAX_RESTARTS", 3),
                baseBackoffMs: ParseIntEnv("FEN_RENDERER_RESTART_BACKOFF_MS", 250),
                maxBackoffMs: ParseIntEnv("FEN_RENDERER_RESTART_MAX_BACKOFF_MS", 5000),
                stableSessionResetMs: ParseIntEnv("FEN_RENDERER_STABLE_RESET_MS", 60000),
                crashWindowMs: ParseIntEnv("FEN_RENDERER_CRASH_WINDOW_MS", 60000),
                maxCrashCountInWindow: ParseIntEnv("FEN_RENDERER_MAX_CRASHES_IN_WINDOW", 4),
                quarantineMs: ParseIntEnv("FEN_RENDERER_CRASH_QUARANTINE_MS", 30000));
            _isolationRegistry = new RendererTabIsolationRegistry(_restartPolicy, _assignmentPolicyMode);
            _rendererReadyTimeout = TimeSpan.FromMilliseconds(ParseIntEnv("FEN_RENDERER_READY_TIMEOUT_MS", 5000));
            if (ParseBoolEnv("FEN_RENDERER_PROCESS_POOL", fallback: true))
            {
                try
                {
                    var sandboxFactory = PlatformLayerFactory.GetInstance().CreateSandboxFactory();
                    _rendererProcessPool = new RendererProcessPool(BuildRendererPoolConfig(), sandboxFactory);
                }
                catch (Exception ex)
                {
                    _rendererProcessPool = null;
                    EngineLog.Write(
                        LogSubsystem.ProcessIsolation,
                        LogSeverity.Warn,
                        $"[ProcessIsolation] Renderer process pool unavailable; falling back to direct launch path. {ex.Message}");
                }
            }
        }

        public void Initialize()
        {
            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, 
                $"[ProcessIsolation] Mode=brokered (per-tab renderer child process enabled, assignment={_assignmentPolicy}, maxRestarts={_restartPolicy.MaxRestartAttempts}, stableResetMs={_restartPolicy.StableSessionResetMs}, crashWindowMs={_restartPolicy.CrashWindowMs}, crashWindowLimit={_restartPolicy.MaxCrashCountInWindow}, quarantineMs={_restartPolicy.QuarantineMs}, poolEnabled={_rendererProcessPool != null})");

            _rendererProcessPool?.Start();
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
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[ProcessIsolation] Initial renderer spawn failed for tab {tab.Id}; will retry on next navigation.");
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
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug, 
                        $"[ProcessIsolation] Activated tab {tab.Id} backed by renderer pid={process.Id}, assignment={state.AssignmentKey ?? "<none>"}");
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
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, 
                        $"[ProcessIsolation] Reassigning renderer process for tab {tab.Id}: {navDecision.PreviousAssignmentKey} -> {navDecision.RequestedAssignmentKey}");
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
                TearDownSession(state, oldSession, $"tab {state.TabId} assignment change");
            }

            state.Session = null;
            state.ActivePid = 0;
            state.PooledSlot = null;

            _ = TryStartSession(state, restartAttempt: 0, restartReason: "assignment-change");
        }

        public void OnInputEvent(BrowserTab tab, RendererInputEvent inputEvent)
        {
            if (tab == null || inputEvent == null || !inputEvent.IsMeaningful)
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
                    TearDownSession(state, session, $"tab {tab.Id} closed");
                }

                // Dispose the OS sandbox — this also kills remaining processes via KILL_ON_JOB_CLOSE.
                state.Sandbox?.Dispose();
                state.Sandbox = null;
                state.PooledSlot = null;
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
                    TearDownSession(state, state.Session, "host shutdown");
                }

                state.Sandbox?.Dispose();
                state.Sandbox = null;
                state.PooledSlot = null;
            }

            _tabStates.Clear();
            _rendererProcessPool?.Dispose();
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
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, 
                    $"[ProcessIsolation] Start denied for tab {state.TabId} (reason={denyReason}{retrySuffix})");
                return false;
            }

            var assignmentForLaunch = !string.IsNullOrWhiteSpace(state.AssignmentKey)
                ? state.AssignmentKey
                : $"bootstrap://tab/{state.TabId}";

            if (_rendererProcessPool != null)
            {
                try
                {
                    var pooledSlot = _rendererProcessPool.AcquireSlotAsync(assignmentForLaunch).GetAwaiter().GetResult();
                    if (pooledSlot?.Process != null && pooledSlot.Session != null)
                    {
                        var pooledSession = pooledSlot.Session;
                        var pooledProcess = pooledSlot.Process;

                        // Remap pooled session tab ids back to the owning host tab id.
                        pooledSession.FrameReceived += (_, payload) => FrameReceived?.Invoke(state.TabId, payload);

                        state.Sandbox?.Dispose();
                        state.Sandbox = null;
                        state.PooledSlot = pooledSlot;

                        pooledProcess.EnableRaisingEvents = true;
                        var pooledPid = pooledProcess.Id;
                        pooledProcess.Exited += (_, __) => HandleChildProcessExit(state.TabId, pooledPid);

                        state.Session = pooledSession;
                        state.ActivePid = pooledPid;
                        _isolationRegistry.MarkSessionStarted(state.TabId, pooledPid);

                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                            $"[ProcessIsolation] Renderer child acquired from pool for tab {state.TabId} (pid={pooledPid}, assignment={assignmentForLaunch}, reason={restartReason})");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                        $"[ProcessIsolation] Renderer pool acquire failed for tab {state.TabId} assignment={assignmentForLaunch}. Falling back to direct launch. {ex.Message}");
                }
            }

            var pipeName = $"fen_renderer_{_parentPid}_{state.TabId}_{Guid.NewGuid():N}";
            var token = CreateAuthToken();
            var session = new RendererChildSession(state.TabId, pipeName, token);
            session.FrameReceived += (_, payload) => FrameReceived?.Invoke(state.TabId, payload);

            var process = StartRendererChildWithSandbox(state.TabId, pipeName, token, assignmentForLaunch, out var sandbox);
            if (process == null)
            {
                sandbox?.Dispose();
                session.Dispose();
                return false;
            }

            // Dispose the previous sandbox if one exists (e.g. after a crash restart).
            state.Sandbox?.Dispose();
            state.Sandbox = sandbox;
            state.PooledSlot = null;

            process.EnableRaisingEvents = true;
            var startedPid = process.Id;
            process.Exited += (_, __) => HandleChildProcessExit(state.TabId, startedPid);

            session.AttachProcess(process);
            var ready = session.WaitForReadyAsync(_rendererReadyTimeout).GetAwaiter().GetResult();
            if (!ready)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error, 
                    $"[ProcessIsolation] Renderer child failed startup contract for tab {state.TabId} (pid={startedPid}, readyTimeoutMs={(int)_rendererReadyTimeout.TotalMilliseconds}).");
                StopSession(session, $"tab {state.TabId} startup contract failure");
                sandbox?.Dispose();
                return false;
            }

            state.Session = session;
            state.ActivePid = startedPid;
            _isolationRegistry.MarkSessionStarted(state.TabId, startedPid);

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, 
                $"[ProcessIsolation] Renderer child started for tab {state.TabId} (pid={startedPid}, pipe={pipeName}, assignment={assignmentForLaunch}, reason={restartReason})");
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
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, 
                    $"[ProcessIsolation] Renderer crash restart unavailable for tab {tabId} (pid={exitedPid}, reason={exitDecision.Reason}{retryAfterSuffix}).");
                    
                // Fire crash event to UI
                RendererCrashed?.Invoke(tabId, exitDecision.Reason);
                return;
            }

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, 
                $"[ProcessIsolation] Renderer child exited unexpectedly for tab {tabId} (pid={exitedPid}). Restart attempt {exitDecision.RestartAttempt}/{_restartPolicy.MaxRestartAttempts} in {exitDecision.RestartDelayMs}ms.");

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

                    TearDownSession(state, state.Session, $"tab {tabId} crash cleanup");
                }

                state.Session = null;
                state.ActivePid = 0;
                state.PooledSlot = null;

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

        private void TearDownSession(TabProcessState state, RendererChildSession session, string reason)
        {
            if (state?.PooledSlot != null)
            {
                try
                {
                    if (_rendererProcessPool != null)
                    {
                        _rendererProcessPool.RetireSlot(state.PooledSlot, reason);
                    }
                    else
                    {
                        state.PooledSlot.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                        $"[ProcessIsolation] Failed to retire pooled renderer for tab {state.TabId} ({reason}): {ex.Message}");
                    state.PooledSlot.Dispose();
                }
                finally
                {
                    state.PooledSlot = null;
                    state.Session = null;
                    state.ActivePid = 0;
                }

                return;
            }

            StopSession(session, reason);
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
                using var launchScope = EngineLog.BeginScope(
                    LogSubsystem.ProcessIsolation,
                    "RendererProcessLauncher",
                    fields: new System.Collections.Generic.Dictionary<string, object>
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
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, "[ProcessIsolation] Could not resolve host executable path for brokered child launch.");
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
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error, 
                            $"[ProcessIsolation] Sandbox.SpawnProcess failed for tab {tabId}: {ex.Message}");
                        rendererSandbox.Dispose();
                        rendererSandbox = null;
                        if (!allowUnsandboxedFallback)
                        {
                            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error, 
                                $"[ProcessIsolation] Refusing unsandboxed renderer retry for tab {tabId}. Set FEN_RENDERER_ALLOW_UNSANDBOXED=1 to override.");
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
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error, 
                            $"[ProcessIsolation] Refusing renderer launch for tab {tabId} because no sandbox is active. Set FEN_RENDERER_ALLOW_UNSANDBOXED=1 to override.");
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
                            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, 
                                $"[ProcessIsolation] Sandbox.AttachToProcess failed for tab {tabId} pid={process.Id}: {ex.Message}");
                            if (!allowUnsandboxedFallback)
                            {
                                TryKillProcess(process, $"sandbox-attach-failed tab={tabId}");
                                rendererSandbox.Dispose();
                                return null;
                            }
                        }
                    }
                }

                if (process != null && rendererSandbox != null)
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, 
                        $"[ProcessIsolation] Renderer child sandboxed via '{rendererSandbox.ProfileName}' for tab {tabId}.");
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
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error, $"[ProcessIsolation] Failed to start renderer child for tab {tabId}: {ex.Message}");
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
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[ProcessIsolation] Failed to stop renderer child ({reason}): {ex.Message}");
            }
            finally
            {
                TryDisposeProcess(process, reason);
                session.Dispose();
            }
        }

        private static void TryKillProcess(Process process, string reason)
        {
            if (process == null || process.HasExited)
            {
                return;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug, $"[ProcessIsolation] Failed to kill renderer child ({reason}): {ex.Message}");
            }
        }

        private static void TryDisposeProcess(Process process, string reason)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                process.Dispose();
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug, $"[ProcessIsolation] Failed to dispose renderer child ({reason}): {ex.Message}");
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

        private static ProcessIsolationConfig BuildRendererPoolConfig()
        {
            var maxPoolSize = Math.Max(1, ParseIntEnv("FEN_RENDERER_POOL_MAX_SIZE", 1));
            var targetWarmCount = Math.Max(0, ParseIntEnv("FEN_RENDERER_POOL_WARM_TARGET", 0));
            if (targetWarmCount > maxPoolSize)
            {
                targetWarmCount = maxPoolSize;
            }

            var maxConcurrentStartup = Math.Max(1, ParseIntEnv("FEN_RENDERER_POOL_MAX_CONCURRENT_STARTUP", 2));
            var startupTimeoutMs = Math.Max(1000, ParseIntEnv("FEN_RENDERER_POOL_STARTUP_TIMEOUT_MS", 5000));
            var lifetimeMs = Math.Max(60000, ParseIntEnv("FEN_RENDERER_POOL_LIFETIME_MS", 30 * 60 * 1000));
            var healthCheckMs = Math.Max(5000, ParseIntEnv("FEN_RENDERER_POOL_HEALTHCHECK_MS", 30000));
            var enablePreWarm = ParseBoolEnv("FEN_RENDERER_POOL_PREWARM", fallback: false);

            return new ProcessIsolationConfig(
                maxPoolSize: maxPoolSize,
                targetWarmCount: targetWarmCount,
                maxConcurrentStartup: maxConcurrentStartup,
                processStartupTimeout: TimeSpan.FromMilliseconds(startupTimeoutMs),
                processLifetimeMax: TimeSpan.FromMilliseconds(lifetimeMs),
                enablePreWarm: enablePreWarm,
                healthCheckInterval: TimeSpan.FromMilliseconds(healthCheckMs));
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

        private static bool ParseBoolEnv(string key, bool fallback)
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return fallback;
        }

        private static (RendererAssignmentPolicyMode mode, string label) ParseAssignmentPolicy(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return (RendererAssignmentPolicyMode.OriginStrict, "origin-strict");
            }

            var normalized = raw.Trim();
            if (string.Equals(normalized, "site-per-process-lite", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "site-per-process", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "site", StringComparison.OrdinalIgnoreCase))
            {
                return (RendererAssignmentPolicyMode.SitePerProcessLite, "site-per-process-lite");
            }

            if (string.Equals(normalized, "origin-strict", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "origin", StringComparison.OrdinalIgnoreCase))
            {
                return (RendererAssignmentPolicyMode.OriginStrict, "origin-strict");
            }

            EngineLog.Write(
                LogSubsystem.ProcessIsolation,
                LogSeverity.Warn,
                $"[ProcessIsolation] Unknown FEN_RENDERER_ASSIGNMENT_POLICY='{normalized}'. Falling back to origin-strict.");
            return (RendererAssignmentPolicyMode.OriginStrict, "origin-strict");
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
            public RendererProcessSlot PooledSlot { get; set; }

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


