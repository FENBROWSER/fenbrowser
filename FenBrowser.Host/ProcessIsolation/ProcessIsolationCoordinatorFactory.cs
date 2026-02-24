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
            // Brokered mode is an incomplete architectural skeleton — frame pixel data is not
            // transferred back from the child process, so the UI shows nothing.  Default to
            // in-process until the shared-memory / texture-streaming path is implemented.
            if (string.Equals(mode, "brokered", StringComparison.OrdinalIgnoreCase))
            {
                FenLogger.Info("[ProcessIsolation] 'brokered' requested explicitly; enabling per-tab renderer child process model.", LogCategory.General);
                return new BrokeredProcessIsolationCoordinator();
            }

            FenLogger.Info("[ProcessIsolation] Process Isolation defaults to 'in-process' (single-process rendering).", LogCategory.General);
            return new InProcessIsolationCoordinator();
        }
    }
}
