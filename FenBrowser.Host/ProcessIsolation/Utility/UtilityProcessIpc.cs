using FenBrowser.Host.ProcessIsolation.Targets;

namespace FenBrowser.Host.ProcessIsolation.Utility
{
    /// <summary>
    /// Canonical startup contract for the dedicated utility target process.
    /// The utility target uses the generic target IPC transport, but owns its own
    /// profile/capability contract so host launchers and child loops cannot drift.
    /// </summary>
    internal static class UtilityProcessIpc
    {
        public static TargetProcessContract Contract { get; } = new(
            TargetProcessKind.Utility,
            profileName: "utility_process",
            capabilitySet: "utility,shared-memory",
            launchArgument: "--utility-child",
            readyTimeoutEnvKey: "FEN_UTILITY_READY_TIMEOUT_MS",
            allowUnsandboxedEnvKey: "FEN_UTILITY_ALLOW_UNSANDBOXED",
            childEnvironmentFlag: "FEN_UTILITY_CHILD");
    }
}
