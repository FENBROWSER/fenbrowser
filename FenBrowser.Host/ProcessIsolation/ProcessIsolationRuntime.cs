using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Global runtime access point for process-isolation routing from host UI code paths.
    /// </summary>
    public static class ProcessIsolationRuntime
    {
        private static Network.NetworkChildProcessHost _networkHost;
        private static Targets.TargetChildProcessHost _gpuHost;
        private static Targets.TargetChildProcessHost _utilityHost;

        /// <summary>
        /// Broker-side coordinator that routes network I/O through the sandboxed
        /// Network child process. Available whenever the network child is running;
        /// null in explicit in-process mode or after shutdown.
        /// </summary>
        public static Network.NetworkProcessCoordinator NetworkCoordinator { get; private set; }

        public static IProcessIsolationCoordinator Current { get; private set; }
        public static event Action<IProcessIsolationCoordinator> CoordinatorChanged;

        public static void SetCoordinator(IProcessIsolationCoordinator coordinator)
        {
            ShutdownAuxiliaryTargets();
            Current = coordinator;

            var shouldStartAuxiliaryTargets =
                coordinator?.UsesOutOfProcessRenderer == true &&
                IsAuxiliaryTargetAutoStartEnabled();

            // Once OOP isolation is selected, direct browser-process sockets must
            // not become an accidental fallback while the network child starts.
            // A successful authenticated session replaces this blocker below.
            if (shouldStartAuxiliaryTargets)
            {
                HttpClientFactory.ConfigureRequestTransport(BlockNetworkRequestAsync);
            }

            CoordinatorChanged?.Invoke(Current);

            if (coordinator?.UsesOutOfProcessRenderer == true)
            {
                StartAuxiliaryTargets();
            }
        }

        public static Network.NetworkProcessSession CurrentNetworkSession => _networkHost?.Session;
        public static Targets.TargetProcessSession CurrentGpuSession => _gpuHost?.Session;
        public static Targets.TargetProcessSession CurrentUtilitySession => _utilityHost?.Session;

        private static void StartAuxiliaryTargets()
        {
            if (!IsAuxiliaryTargetAutoStartEnabled())
                return;

            _networkHost = new Network.NetworkChildProcessHost();
            if (!_networkHost.TryStart())
            {
                // Keep the fail-closed transport installed. OOP isolation was
                // requested, so a child-start failure must not restore broker sockets.
                _networkHost.Dispose();
                _networkHost = null;
            }
            else
            {
                var coordinator = new Network.NetworkProcessCoordinator();
                coordinator.AttachSession(_networkHost.Session);
                NetworkCoordinator = coordinator;

                // HttpClientFactory is the construction boundary used by BrowserHost
                // and Core network services. Installing the coordinator here makes the
                // sandboxed child authoritative for newly created browser clients.
                HttpClientFactory.ConfigureRequestTransport(
                    (request, cancellationToken) => coordinator.SendAsync(
                        request,
                        GetInitiatorOrigin(request),
                        cancellationToken));
            }

            _gpuHost = new Targets.TargetChildProcessHost(Targets.TargetProcessKind.Gpu);
            if (!_gpuHost.TryStart())
            {
                _gpuHost.Dispose();
                _gpuHost = null;
            }

            _utilityHost = new Targets.TargetChildProcessHost(Targets.TargetProcessKind.Utility);
            if (!_utilityHost.TryStart())
            {
                _utilityHost.Dispose();
                _utilityHost = null;
            }
        }

        private static Task<HttpResponseMessage> BlockNetworkRequestAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = request?.RequestUri?.AbsoluteUri ?? "unknown target";
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException(
                    $"Sandboxed network process is unavailable; request to {target} blocked by process-isolation policy."));
        }

        private static string GetInitiatorOrigin(HttpRequestMessage request)
        {
            if (request?.Headers != null &&
                request.Headers.TryGetValues("Origin", out var origins))
            {
                foreach (var origin in origins)
                {
                    if (!string.IsNullOrWhiteSpace(origin) &&
                        !string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
                    {
                        return origin.Trim();
                    }
                }
            }

            // Same-origin and top-level navigation requests often omit Origin.
            // Fetch/CORS authorization remains in ResourceManager; the network IPC
            // field is descriptive and must not invent a different origin policy.
            return string.Empty;
        }

        private static void ShutdownAuxiliaryTargets()
        {
            // Restore direct transport only as part of an explicit process-isolation
            // transition/shutdown, before disposing the coordinator captured by the
            // broker transport delegate.
            HttpClientFactory.ClearRequestTransport();

            TryDispose(_utilityHost, "utility-target-host");
            _utilityHost = null;

            TryDispose(_gpuHost, "gpu-target-host");
            _gpuHost = null;

            TryDispose(NetworkCoordinator, "network-coordinator");
            NetworkCoordinator = null;
            TryDispose(_networkHost, "network-child-host");
            _networkHost = null;
        }

        private static void TryDispose(IDisposable disposable, string resourceName)
        {
            if (disposable == null)
            {
                return;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug, $"[ProcessIsolationRuntime] Dispose failed for {resourceName}: {ex.Message}");
            }
        }

        private static bool IsAuxiliaryTargetAutoStartEnabled()
        {
            var value = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return !string.Equals(value, "0", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
