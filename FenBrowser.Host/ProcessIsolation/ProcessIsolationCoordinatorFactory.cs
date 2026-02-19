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
            if (string.Equals(mode, "brokered", StringComparison.OrdinalIgnoreCase))
            {
                FenLogger.Info("[ProcessIsolation] 'brokered' requested; enabling per-tab renderer child process model.", LogCategory.General);
                return new BrokeredProcessIsolationCoordinator();
            }

            return new InProcessIsolationCoordinator();
        }
    }
}
