using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Selects the active process model.
    /// FEN_PROCESS_ISOLATION=brokered enables the per-tab renderer child process model.
    /// </summary>
    public static class ProcessIsolationCoordinatorFactory
    {
        public static IProcessIsolationCoordinator CreateFromEnvironment()
        {
            var mode = Environment.GetEnvironmentVariable("FEN_PROCESS_ISOLATION");
            if (string.Equals(mode, "brokered", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase))
            {
                EngineLogBridge.Info($"[ProcessIsolation] '{mode}' requested; enabling per-tab renderer child process model.", LogCategory.General);
                return new BrokeredProcessIsolationCoordinator();
            }

            EngineLogBridge.Info("[ProcessIsolation] Process isolation defaults to 'in-process'. Set FEN_PROCESS_ISOLATION=brokered or auto to enable out-of-process renderer mode.", LogCategory.General);
            return new InProcessIsolationCoordinator();
        }
    }
}

