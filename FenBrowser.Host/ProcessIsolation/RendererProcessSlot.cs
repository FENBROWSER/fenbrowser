// SpecRef: FenBrowser Renderer Process Slot Pooling Lifecycle
// CapabilityId: PROCESS-ISOLATION-SLOT-01
// Determinism: strict
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Platform;
using FenBrowser.Core.Security.Sandbox;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Represents a pooled renderer process slot with lifecycle management, health tracking,
    /// and deterministic activation patterns. Thread-safe and disposable.
    /// </summary>
    internal sealed class RendererProcessSlot : IDisposable
    {
        private readonly object _syncRoot = new object();
        private bool _isDisposed;
        private string _currentAssignmentKey;
        private DateTime _activatedAt;
        private long _frameCount;
        private bool _isActive;
        
        // Telemetry
        private readonly Stopwatch _lifetimeStopwatch;
        private long _totalFramesRendered;
        private long _totalActivations;
        private long _totalBytesSent;
        private long _totalBytesReceived;

        // Underlying session and process
        internal RendererChildSession Session { get; private set; }
        public Process Process { get; private set; }
        public ISandbox Sandbox { get; private set; }
        public string AssignmentKey => _currentAssignmentKey;
        public bool IsActive => _isActive && !_isDisposed;
        public DateTime ActivatedAt => _activatedAt;
        public long ProcessId => Process?.Id ?? -1;
        public TimeSpan Lifetime => _lifetimeStopwatch?.Elapsed ?? TimeSpan.Zero;
        public long FrameCount => _frameCount;
        public long TotalFramesRendered => _totalFramesRendered;
        public long TotalActivations => _totalActivations;
        public long TotalBytesSent => _totalBytesSent;
        public long TotalBytesReceived => _totalBytesReceived;

        internal RendererProcessSlot(
            RendererChildSession session,
            Process process,
            ISandbox sandbox,
            string initialAssignmentKey)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Process = process ?? throw new ArgumentNullException(nameof(process));
            
            _lifetimeStopwatch = Stopwatch.StartNew();
            _currentAssignmentKey = initialAssignmentKey ?? "warm-pool";
            Sandbox = sandbox;
            
            // Subscribe to session events
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
            
            var tabId = GenerateTabId();
            var pipeName = $"fen_renderer_pool_{Environment.ProcessId}_{tabId}_{Guid.NewGuid():N}";
            var authToken = CreateAuthToken();
            
            // Create session
            var session = new RendererChildSession(tabId, pipeName, authToken);
            
            try
            {
                // Create sandbox and spawn process
                var sandbox = CreateSandbox(sandboxFactory, assignmentKey);
                var process = StartRendererChildWithSandbox(
                    tabId, pipeName, authToken, assignmentKey, sandbox);
                
                if (process == null)
                {
                    throw new RendererProcessSlotException("Failed to start renderer process");
                }
                
                // Attach process and wait for connection
                session.AttachProcess(process);
                
                // Wait for ready with timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                
                var ready = await session.WaitForReadyAsync(timeout, cancellationToken);
                if (!ready)
                {
                    throw new RendererProcessSlotException("Renderer process startup timeout");
                }
                
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                    $"[RendererProcessSlot] Process {process.Id} ready for assignment {assignmentKey}");
                
                return new RendererProcessSlot(session, process, sandbox, assignmentKey);
            }
            catch (Exception ex)
            {
                // Cleanup on failure
                session?.Dispose();
                
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessSlot] Creation failed for assignment {assignmentKey}: {ex.Message}");
                
                throw new RendererProcessSlotException($"Failed to create process slot: {ex.Message}", ex);
            }
        }

        internal async Task ActivateAsync(string assignmentKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(assignmentKey))
                throw new ArgumentException("Assignment key required", nameof(assignmentKey));
            
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(RendererProcessSlot));
            
            lock (_syncRoot)
            {
                if (_isActive)
                    throw new InvalidOperationException("Slot already active");
                
                _isActive = true;
                _activatedAt = DateTime.UtcNow;
                _currentAssignmentKey = assignmentKey;
                _frameCount = 0;
                Interlocked.Increment(ref _totalActivations);
            }
            
            try
            {
                // Reset state for new activation
                await ResetForAssignmentAsync(assignmentKey, cancellationToken);
                
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessSlot] Activated process {Process.Id} for assignment {assignmentKey} " +
                    $"(activation #{_totalActivations})");
            }
            catch (Exception ex)
            {
                lock (_syncRoot)
                {
                    _isActive = false;
                }
                
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessSlot] Activation failed for process {Process.Id}: {ex.Message}");
                
                throw new RendererProcessSlotException($"Failed to activate slot: {ex.Message}", ex);
            }
        }

        internal void ResetForReuse()
        {
            if (_isDisposed) return;
            
            lock (_syncRoot)
            {
                _isActive = false;
                _currentAssignmentKey = "warm-pool";
                _frameCount = 0;
                // Note: We intentionally do NOT clear telemetry counters here
                // to maintain lifetime statistics
            }
            
            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Trace,
                $"[RendererProcessSlot] Process {Process.Id} reset for pool reuse");
        }

        private async Task ResetForAssignmentAsync(string assignmentKey, CancellationToken cancellationToken = default)
        {
            // Send reset message to clear renderer state
            var payload = new RendererResetPayload
            {
                AssignmentKey = assignmentKey,
                ClearState = true,
                ClearCache = false, // Keep compiled CSS/JS caches if possible
                ResetMetrics = false
            };
            
            // This is a simplified reset - in production you'd use proper IPC
            // For now, we'll just log the reset
            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Trace,
                $"[RendererProcessSlot] Resetting process {Process.Id} for new assignment {assignmentKey}");
            
            await Task.CompletedTask; // TODO: Implement actual reset IPC
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
                    $"[RendererProcessSlot] Health check failed for process {Process.Id}: {ex.Message}");
                return false;
            }
        }

        private void OnFrameReceived(int tabId, RendererFrameReadyPayload payload)
        {
            // This is a callback from the session's frame received event
            Interlocked.Increment(ref _frameCount);
            Interlocked.Increment(ref _totalFramesRendered);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            
            lock (_syncRoot)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                _isActive = false;
            }
            
            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                $"[RendererProcessSlot] Disposing process {Process.Id} " +
                $"lifetime={_lifetimeStopwatch.Elapsed.TotalSeconds:F1}s " +
                $"activations={_totalActivations} frames={_totalFramesRendered} " +
                $"bytesSent={_totalBytesSent} bytesRecv={_totalBytesReceived}");
            
            try
            {
                Session?.Dispose();
                
                if (Process != null && !Process.HasExited)
                {
                    try
                    {
                        Process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                            $"[RendererProcessSlot] Failed to kill process {Process.Id}: {ex.Message}");
                    }
                }
                
                Process?.Dispose();
                Sandbox?.Dispose();
                _lifetimeStopwatch?.Stop();
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessSlot] Dispose error for process {Process.Id}: {ex.Message}");
            }
        }

        // Helper methods
        private static int GenerateTabId()
        {
            // Generate a temporary tab ID for pool processes
            // These IDs are negative to distinguish from real tabs
            return -Math.Abs(Environment.ProcessId + (int)Stopwatch.GetTimestamp());
        }
        
        private static string CreateAuthToken()
        {
            // Generate a secure auth token for IPC
            return Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        }
        
        private static ISandbox CreateSandbox(IOsSandboxFactory sandboxFactory, string assignmentKey)
        {
            // For production use, create sandbox with proper policies
            // Use a custom renderer profile with appropriate limits
            var profile = new OsSandboxProfile(
                kind: OsSandboxProfileKind.RendererMinimal,
                maxMemoryBytes: 512L * 1024 * 1024, // 512 MiB
                maxCpuPercent: 80,
                denyDesktopAccess: true,
                denyWindowEnumeration: true,
                capabilities: OsSandboxCapabilities.RendererMinimal
            );
            
            // For now, use a stub implementation. In production, the sandboxFactory
            // would need to know about the assignmentKey for proper isolation.
            // The assignmentKey is tracked at the pool level, not passed to sandbox
            return sandboxFactory.Create(profile);
        }
        
        private static Process StartRendererChildWithSandbox(
            int tabId,
            string pipeName,
            string authToken,
            string assignmentKey,
            ISandbox sandbox)
        {
            // This should be replaced with actual process spawning logic
            // For now, we'll return null to indicate this is a stub
            
            // In production, this would:
            // 1. Create process start info with sandboxed environment
            // 2. Set command line arguments for renderer
            // 3. Apply sandbox policies
            // 4. Start the process
            
            throw new NotImplementedException(
                "Process spawning not yet implemented. Integrate with existing BrokeredProcessIsolationCoordinator logic.");
        }
    }

    internal sealed class RendererProcessSlotException : Exception
    {
        public RendererProcessSlotException(string message) : base(message) { }
        public RendererProcessSlotException(string message, Exception inner) : base(message, inner) { }
    }

    // Payload classes for IPC communication
    internal sealed class RendererResetPayload
    {
        public string AssignmentKey { get; set; }
        public bool ClearState { get; set; }
        public bool ClearCache { get; set; }
        public bool ResetMetrics { get; set; }
    }
}


