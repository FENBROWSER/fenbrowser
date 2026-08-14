using System;
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
        private static readonly object _transitionLock = new();
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
            lock (_transitionLock)
            {
                var shouldStartAuxiliaryTargets =
                    coordinator?.UsesOutOfProcessRenderer == true &&
                    IsAuxiliaryTargetAutoStartEnabled();

                // Install the blocker BEFORE retiring an existing OOP network stack.
                // Clearing the old transport first creates a window where another
                // thread can acquire a direct-socket shared client during an OOP->OOP
                // coordinator transition.
                if (shouldStartAuxiliaryTargets)
                {
                    HttpClientFactory.ConfigureRequestTransport(BlockNetworkRequestAsync);
                }

                ShutdownAuxiliaryTargets(
                    restoreDirectTransport: !shouldStartAuxiliaryTargets);

                Current = coordinator;
                CoordinatorChanged?.Invoke(Current);

                if (coordinator?.UsesOutOfProcessRenderer == true)
                {
                    StartAuxiliaryTargets();
                }
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
                try
                {
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
                catch
                {
                    coordinator.Dispose();
                    NetworkCoordinator = null;
                    _networkHost.Dispose();
                    _networkHost = null;
                    // The blocker installed by SetCoordinator remains authoritative.
                    HttpClientFactory.ConfigureRequestTransport(BlockNetworkRequestAsync);
                    throw;
                }
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
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException(
                    $"Sandboxed network process is unavailable; request to {GetSafeUriForMessage(request?.RequestUri)} blocked by process-isolation policy."));
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

        private static void ShutdownAuxiliaryTargets(bool restoreDirectTransport)
        {
            if (restoreDirectTransport)
            {
                // Only explicit transitions to a mode that does not require the
                // auxiliary OOP network target may restore direct transport.
                HttpClientFactory.ClearRequestTransport();
            }

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
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug, $"[ProcessIsolationRuntime] Dispose failed for {resourceName}: {ex.GetType().Name}");
            }
        }

        private static bool IsAuxiliaryTargetAutoStartEnabled()
        {
            var value = Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSafeUriForMessage(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return "unknown target";
            }

            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return uri.GetLeftPart(UriPartial.Authority);
                }
                catch
                {
                    return uri.Scheme + ":";
                }
            }

            return uri.Scheme + ":";
        }
    }
}
