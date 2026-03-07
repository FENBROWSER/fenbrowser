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
        /// Broker-side coordinator that routes all network I/O through the
        /// sandboxed Network child process (with in-process fallback).
        /// Available whenever the network child is running; null in in-process mode.
        /// </summary>
        public static Network.NetworkProcessCoordinator NetworkCoordinator { get; private set; }

        public static IProcessIsolationCoordinator Current { get; private set; }

        public static void SetCoordinator(IProcessIsolationCoordinator coordinator)
        {
            ShutdownAuxiliaryTargets();
            Current = coordinator;

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
                _networkHost.Dispose();
                _networkHost = null;
            }
            else
            {
                // Wire the live session into the coordinator so all broker-side
                // network requests flow through the sandboxed network process.
                var coordinator = new Network.NetworkProcessCoordinator();
                coordinator.AttachSession(_networkHost.Session);
                NetworkCoordinator = coordinator;
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

        private static void ShutdownAuxiliaryTargets()
        {
            try { _utilityHost?.Dispose(); } catch { }
            _utilityHost = null;

            try { _gpuHost?.Dispose(); } catch { }
            _gpuHost = null;

            try
            {
                NetworkCoordinator?.Dispose();
                NetworkCoordinator = null;
                _networkHost?.Dispose();
            }
            catch { }
            _networkHost = null;
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
