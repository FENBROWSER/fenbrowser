using System;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// Runtime policy bundle for the browser-integrated JavaScript engine.
    /// Profiles let the host tune security, diagnostics, and resource envelopes
    /// without scattering ad-hoc flags throughout the engine bootstrap path.
    /// </summary>
    public sealed class JavaScriptRuntimeProfile
    {
        public static JavaScriptRuntimeProfile Balanced { get; } = new JavaScriptRuntimeProfile();

        public string Name { get; init; } = "balanced";
        public bool EnableExecutionLogging { get; init; } = true;
        public bool EnableStructuredExecutionLogs { get; init; } = true;
        public bool WriteExecutionArtifacts { get; init; } = false;
        public bool FreezeIntrinsicPrototypes { get; init; } = false;
        public bool UseSandboxedResourceLimits { get; init; } = false;
        public bool AllowDynamicCodeEvaluation { get; init; } = true;
        public int LargeScriptWarningBytes { get; init; } = 64 * 1024;
        public bool DeferOversizedExternalPageScripts { get; init; } = true;
        public int OversizedExternalPageScriptBytes { get; init; } = 256 * 1024;
        public TimeSpan MaxExecutionTime { get; init; } = TimeSpan.FromSeconds(15);
        public long MaxInstructionCount { get; init; } = 100_000_000;

        public static JavaScriptRuntimeProfile CreateLockedDown()
        {
            return new JavaScriptRuntimeProfile
            {
                Name = "locked-down",
                EnableExecutionLogging = true,
                EnableStructuredExecutionLogs = true,
                WriteExecutionArtifacts = false,
                FreezeIntrinsicPrototypes = true,
                UseSandboxedResourceLimits = true,
                AllowDynamicCodeEvaluation = false,
                LargeScriptWarningBytes = 16 * 1024,
                DeferOversizedExternalPageScripts = true,
                OversizedExternalPageScriptBytes = 64 * 1024,
                MaxExecutionTime = TimeSpan.FromSeconds(1),
                MaxInstructionCount = 10_000_000
            };
        }
    }
}
