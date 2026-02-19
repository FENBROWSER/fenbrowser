using System;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Selects the active process model.
    /// FEN_PROCESS_ISOLATION=brokered reserves the broker/renderer split mode; currently falls back to in-process.
    /// </summary>
    public static class ProcessIsolationCoordinatorFactory
    {
        public static IProcessIsolationCoordinator CreateFromEnvironment()
        {
            var mode = Environment.GetEnvironmentVariable("FEN_PROCESS_ISOLATION");
            if (string.Equals(mode, "brokered", StringComparison.OrdinalIgnoreCase))
            {
                FenLogger.Warn("[ProcessIsolation] 'brokered' requested but not fully available yet; falling back to in-process.", LogCategory.General);
            }

            return new InProcessIsolationCoordinator();
        }
    }
}
