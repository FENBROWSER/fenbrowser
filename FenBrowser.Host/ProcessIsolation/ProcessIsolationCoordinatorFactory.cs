using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Selects the active process model.
    /// Defaults to brokered (per-tab renderer child process model).
    /// Explicitly set FEN_PROCESS_ISOLATION=in-process to opt out.
    /// </summary>
    public static class ProcessIsolationCoordinatorFactory
    {
        public static IProcessIsolationCoordinator CreateFromEnvironment()
        {
            var mode = (Environment.GetEnvironmentVariable("FEN_PROCESS_ISOLATION") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(mode))
            {
                EngineLogBridge.Info("[ProcessIsolation] Defaulting to brokered mode (per-tab renderer child process model). Set FEN_PROCESS_ISOLATION=in-process to disable.", LogCategory.General);
                return new BrokeredProcessIsolationCoordinator();
            }

            if (IsInProcessMode(mode))
            {
                EngineLogBridge.Info($"[ProcessIsolation] '{mode}' requested; using in-process mode.", LogCategory.General);
                return new InProcessIsolationCoordinator();
            }

            if (IsBrokeredMode(mode))
            {
                EngineLogBridge.Info($"[ProcessIsolation] '{mode}' requested; enabling brokered mode.", LogCategory.General);
                return new BrokeredProcessIsolationCoordinator();
            }

            EngineLogBridge.Warn($"[ProcessIsolation] Unknown mode '{mode}'. Falling back to brokered mode. Supported values: brokered|auto|in-process.", LogCategory.General);
            return new BrokeredProcessIsolationCoordinator();
        }

        private static bool IsBrokeredMode(string mode)
        {
            return string.Equals(mode, "brokered", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "enabled", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInProcessMode(string mode)
        {
            return string.Equals(mode, "in-process", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "inproc", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "disabled", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "0", StringComparison.OrdinalIgnoreCase);
        }
    }
}

