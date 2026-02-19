namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Global runtime access point for process-isolation routing from host UI code paths.
    /// </summary>
    public static class ProcessIsolationRuntime
    {
        public static IProcessIsolationCoordinator Current { get; private set; }

        public static void SetCoordinator(IProcessIsolationCoordinator coordinator)
        {
            Current = coordinator;
        }
    }
}
