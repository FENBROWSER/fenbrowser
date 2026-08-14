// SpecRef: FenBrowser Renderer Process Slot Pooling Lifecycle
// CapabilityId: PROCESS-ISOLATION-SLOT-01
// Determinism: strict
using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Platform;
using FenBrowser.Core.Security.Sandbox;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Represents a renderer process slot. A slot may be created as a fresh warm
    /// standby and assigned once, or created already bound to a concrete assignment.
    /// It is never reset/reused after web content has been assigned to it.
    /// </summary>
    internal sealed class RendererProcessSlot : IDisposable
    {
        private static int _nextPoolTabId;

        private readonly object _syncRoot = new();
        private bool _isDisposed;
        private bool _hasAssignment;
        private string _currentAssignmentKey;
        private DateTime _activatedAt;
        private long _frameCount;
        private bool _isActive;

        // Telemetry
        private readonly Stopwatch _lifetimeStopwatch;
        private long _totalFramesRendered;
        private long _totalActivations;
#pragma warning disable CS0649
        private long _totalBytesSent;
        private long _totalBytesReceived;
#pragma warning restore CS0649

        internal RendererChildSession Session { get; private set; }
        public Process Process { get; private set; }
        public ISandbox Sandbox { get; private set; }
        public string AssignmentKey => _currentAssignmentKey;
        public bool IsActive => _isActive && !_isDisposed;
        public DateTime ActivatedAt => _activatedAt;
        public long ProcessId => Process?.Id ?? -1;
        public TimeSpan Lifetime => _lifetimeStopwatch?.Elapsed ?? TimeSpan.Zero;
        public long FrameCount => Interlocked.Read(ref _frameCount);
        public long TotalFramesRendered => Interlocked.Read(ref _totalFramesRendered);
        public long TotalActivations => Interlocked.Read(ref _totalActivations);
        public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
        public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

        internal RendererProcessSlot(
            RendererChildSession session,
            Process process,
            ISandbox sandbox,
            string initialAssignmentKey)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Process = process ?? throw new ArgumentNullException(nameof(process));

            _lifetimeStopwatch = Stopwatch.StartNew();
            _currentAssignmentKey = string.IsNullOrWhiteSpace(initialAssignmentKey)
                ? "warm-pool"
                : initialAssignmentKey;
            _hasAssignment = !string.Equals(_currentAssignmentKey, "warm-pool", StringComparison.Ordinal);
            if (_hasAssignment)
            {
                _activatedAt = DateTime.UtcNow;
                _totalActivations = 1;
            }

            Sandbox = sandbox;
            Session.FrameReceived += OnFrameReceived;

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                $"[RendererProcessSlot] Created slot for process {Process.Id} assignment={_currentAssignmentKey}");
        }

        internal static async Task<RendererProcessSlot> CreateAsync(
            string assignmentKey,
            IOsSandboxFactory sandboxFactory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(assignmentKey))
                throw new ArgumentException("Assignment key required", nameof(assignmentKey));
            if (sandboxFactory == null)
                throw new ArgumentNullException(nameof(sandboxFactory));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            var tabId = GenerateTabId();
            var pipeName = $"fen_renderer_pool_{Environment.ProcessId}_{tabId}_{Guid.NewGuid():N}";
            var authToken = CreateAuthToken();
            var session = new RendererChildSession(tabId, pipeName, authToken);
            ISandbox sandbox = null;
            Process process = null;

            try
            {
                sandbox = CreateSandbox(sandboxFactory, assignmentKey);
                process = StartRendererChildWithSandbox(
                    tabId, pipeName, authToken, assignmentKey, sandbox);

                if (process == null)
                    throw new RendererProcessSlotException("Failed to start renderer process");

                session.AttachProcess(process);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);

                var ready = await session.WaitForReadyAsync(timeout, cts.Token).ConfigureAwait(false);
                if (!ready)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new RendererProcessSlotException("Renderer process startup timeout");
                }

                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                    $"[RendererProcessSlot] Process {process.Id} ready for assignment {assignmentKey}");

                return new RendererProcessSlot(session, process, sandbox, assignmentKey);
            }
            catch (OperationCanceledException)
            {
                // A partially started child must not outlive the failed acquisition.
                TryKillProcess(process, $"startup-cancelled assignment={assignmentKey}");
                sandbox?.Dispose();
                session.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                // RendererChildSession.Dispose closes IPC but intentionally does not
                // own process lifetime, so kill the partially launched child here.
                TryKillProcess(process, $"startup-failed assignment={assignmentKey}");
                sandbox?.Dispose();
                session.Dispose();

                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessSlot] Creation failed for assignment {assignmentKey}: {ex.Message}");

                throw new RendererProcessSlotException($"Failed to create process slot: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Assign a never-used warm renderer exactly once. There is intentionally no
        /// reset path here: a second assignment requires a new process.
        /// </summary>
        internal Task ActivateAsync(string assignmentKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(assignmentKey))
                throw new ArgumentException("Assignment key required", nameof(assignmentKey));
            cancellationToken.ThrowIfCancellationRequested();

            lock (_syncRoot)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(RendererProcessSlot));
                if (_isActive)
                    throw new InvalidOperationException("Slot already active");
                if (_hasAssignment)
                {
                    throw new InvalidOperationException(
                        "Renderer process slots are single-assignment until a real reset IPC protocol exists.");
                }

                _hasAssignment = true;
                _isActive = true;
                _activatedAt = DateTime.UtcNow;
                _currentAssignmentKey = assignmentKey;
                Interlocked.Exchange(ref _frameCount, 0);
                Interlocked.Increment(ref _totalActivations);
            }

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                $"[RendererProcessSlot] Assigned fresh process {Process.Id} to {assignmentKey}");

            return Task.CompletedTask;
        }

        public bool IsHealthy()
        {
            if (_isDisposed) return false;

            try
            {
                return Process != null && !Process.HasExited;
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessSlot] Health check failed for process {Process?.Id}: {ex.Message}");
                return false;
            }
        }

        private void OnFrameReceived(int tabId, RendererFrameReadyPayload payload)
        {
            Interlocked.Increment(ref _frameCount);
            Interlocked.Increment(ref _totalFramesRendered);
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                _isActive = false;
            }

            var processId = ProcessId;
            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                $"[RendererProcessSlot] Disposing process {processId} " +
                $"lifetime={_lifetimeStopwatch.Elapsed.TotalSeconds:F1}s " +
                $"activations={Interlocked.Read(ref _totalActivations)} frames={Interlocked.Read(ref _totalFramesRendered)} " +
                $"bytesSent={Interlocked.Read(ref _totalBytesSent)} bytesRecv={Interlocked.Read(ref _totalBytesReceived)}");

            try
            {
                if (Session != null)
                {
                    Session.FrameReceived -= OnFrameReceived;
                    Session.Dispose();
                }

                if (Process != null && !Process.HasExited)
                {
                    try
                    {
                        Process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                            $"[RendererProcessSlot] Failed to kill process {processId}: {ex.Message}");
                    }
                }

                Process?.Dispose();
                Sandbox?.Dispose();
                _lifetimeStopwatch.Stop();
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessSlot] Dispose error for process {processId}: {ex.Message}");
            }
        }

        private static int GenerateTabId()
        {
            var id = Interlocked.Increment(ref _nextPoolTabId);
            if (id <= 0)
                throw new InvalidOperationException("Renderer pool tab ID space exhausted.");
            return -id;
        }

        private static string CreateAuthToken()
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }

        private static ISandbox CreateSandbox(IOsSandboxFactory sandboxFactory, string assignmentKey)
        {
            var allowUnsandboxedFallback = ProcessIsolationEnvPolicy.IsUnsandboxedFallbackEnabled("FEN_RENDERER_ALLOW_UNSANDBOXED");

            if (!SandboxLaunchPolicy.TryAcquire(
                $"renderer pool slot assignment={assignmentKey}",
                sandboxFactory,
                OsSandboxProfile.RendererMinimal,
                allowUnsandboxedFallback,
                "FEN_RENDERER_ALLOW_UNSANDBOXED",
                out var sandbox))
            {
                return null;
            }

            return sandbox;
        }

        private static Process StartRendererChildWithSandbox(
            int tabId,
            string pipeName,
            string authToken,
            string assignmentKey,
            ISandbox sandbox)
        {
            var allowUnsandboxedFallback = ProcessIsolationEnvPolicy.IsUnsandboxedFallbackEnabled("FEN_RENDERER_ALLOW_UNSANDBOXED");
            var parentPid = Environment.ProcessId;
            var exePath = HostExecutablePathResolver.Resolve();

            if (string.IsNullOrWhiteSpace(exePath))
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                    "[RendererProcessSlot] Could not resolve host executable path for pooled child launch.");
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
            RendererChildEnvironment.ResetToSafeBase(startInfo);
            startInfo.Environment["FEN_RENDERER_CHILD"] = "1";
            startInfo.Environment["FEN_RENDERER_TAB_ID"] = tabId.ToString();
            startInfo.Environment["FEN_RENDERER_PARENT_PID"] = parentPid.ToString();
            startInfo.Environment["FEN_RENDERER_PIPE_NAME"] = pipeName;
            startInfo.Environment["FEN_RENDERER_AUTH_TOKEN"] = authToken;
            startInfo.Environment["FEN_RENDERER_SANDBOX_PROFILE"] = "renderer_minimal";
            startInfo.Environment["FEN_RENDERER_CAPABILITIES"] = "navigate,input,frame";
            startInfo.Environment["FEN_RENDERER_ASSIGNMENT_KEY"] = assignmentKey ?? string.Empty;

            sandbox?.ApplyToProcessStartInfo(startInfo);
            Process process = null;

            if (sandbox != null && sandbox.RequiresCustomSpawn)
            {
                try
                {
                    process = sandbox.SpawnProcess(startInfo);
                }
                catch (Exception ex)
                {
                    EngineLog.Write(
                        LogSubsystem.ProcessIsolation,
                        allowUnsandboxedFallback ? LogSeverity.Warn : LogSeverity.Error,
                        $"[RendererProcessSlot] Sandbox.SpawnProcess failed for assignment {assignmentKey}: {ex.Message}" +
                        (allowUnsandboxedFallback ? " (retrying with job-only fallback)" : string.Empty));
                    if (!allowUnsandboxedFallback)
                        return null;

                    process = Process.Start(startInfo);
                    if (process != null && sandbox != null)
                    {
                        try
                        {
                            sandbox.AttachToProcess(process);
                        }
                        catch (Exception attachEx)
                        {
                            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                                $"[RendererProcessSlot] Job-only sandbox fallback attach failed for pid={process.Id}: {attachEx.Message}");
                        }
                    }
                }
            }
            else
            {
                if (sandbox == null && !allowUnsandboxedFallback)
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                        "[RendererProcessSlot] Refusing pooled renderer launch because no sandbox is active. Set FEN_RENDERER_ALLOW_UNSANDBOXED=1 to override.");
                    return null;
                }

                process = Process.Start(startInfo);
                if (process != null && sandbox != null)
                {
                    try
                    {
                        sandbox.AttachToProcess(process);
                    }
                    catch (Exception ex)
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                            $"[RendererProcessSlot] Sandbox.AttachToProcess failed for pid={process.Id}: {ex.Message}");
                        if (!allowUnsandboxedFallback)
                        {
                            TryKillProcess(process, $"pool-sandbox-attach-failed assignment={assignmentKey}");
                            return null;
                        }
                    }
                }
            }

            return process;
        }

        private static void TryKillProcess(Process process, string reason)
        {
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessSlot] Failed to kill process ({reason}): {ex.Message}");
            }
        }
    }

    internal sealed class RendererProcessSlotException : Exception
    {
        public RendererProcessSlotException(string message) : base(message) { }
        public RendererProcessSlotException(string message, Exception inner) : base(message, inner) { }
    }
}
