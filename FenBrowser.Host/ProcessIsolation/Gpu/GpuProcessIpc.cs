using FenBrowser.Host.ProcessIsolation.Targets;

namespace FenBrowser.Host.ProcessIsolation.Gpu
{
    /// <summary>
    /// Canonical startup contract for the dedicated GPU target process.
    /// The GPU target uses the generic target IPC transport, but owns its own
    /// profile/capability contract so host launchers and child loops cannot drift.
    /// </summary>
    internal static class GpuProcessIpc
    {
        public static TargetProcessContract Contract { get; } = new(
            TargetProcessKind.Gpu,
            profileName: "gpu_process",
            capabilitySet: "gpu,shared-memory,compositor",
            launchArgument: "--gpu-child",
            readyTimeoutEnvKey: "FEN_GPU_READY_TIMEOUT_MS",
            allowUnsandboxedEnvKey: "FEN_GPU_ALLOW_UNSANDBOXED",
            childEnvironmentFlag: "FEN_GPU_CHILD");
    }
}
