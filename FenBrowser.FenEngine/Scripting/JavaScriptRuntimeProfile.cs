using System;
using FenBrowser.FenEngine.Configuration;

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
        // Keep balanced profile correctness-first for modern pages that rely on large bootstrap bundles.
        public bool DeferOversizedExternalPageScripts { get; init; } = false;
        public int OversizedExternalPageScriptBytes { get; init; } = 256 * 1024;
        // Soft DoS guard, not a performance budget. Real bundles (x.com's vendor
        // bundle is 670KB minified, Google xjs is similar) take >60s on our
        // interpreter — V8 finishes in <1s. We need a cap that allows full
        // bootstrap on a slow tree-walking interpreter rather than killing it
        // mid-execution. 300s is generous but matches real-world worst case
        // (we've measured vendor.js needing ~90s on the bytecode VM).
        public TimeSpan MaxExecutionTime { get; init; } = TimeSpan.FromSeconds(300);
        // Companion DoS guard on retired bytecode instructions. Match the time cap:
        // bundles that take 300s on our interpreter retire ~5B instructions.
        public long MaxInstructionCount { get; init; } = 5_000_000_000;

        /// <summary>
        /// Engine-wide options for rendering, CSS, security, and resource loading.
        /// If null, FenEngineOptions.Default is used.
        /// </summary>
        public FenEngineOptions EngineOptions { get; init; } = null;

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
                MaxInstructionCount = 10_000_000,
                EngineOptions = FenEngineOptions.CreateLockedDown()
            };
        }

        /// <summary>
        /// Creates a development profile with lenient settings for debugging.
        /// </summary>
        public static JavaScriptRuntimeProfile CreateDevelopment()
        {
            return new JavaScriptRuntimeProfile
            {
                Name = "development",
                EnableExecutionLogging = true,
                EnableStructuredExecutionLogs = true,
                WriteExecutionArtifacts = true,
                FreezeIntrinsicPrototypes = false,
                UseSandboxedResourceLimits = false,
                AllowDynamicCodeEvaluation = true,
                LargeScriptWarningBytes = 256 * 1024,
                DeferOversizedExternalPageScripts = false,
                OversizedExternalPageScriptBytes = 1024 * 1024,
                MaxExecutionTime = TimeSpan.FromSeconds(60),
                MaxInstructionCount = 500_000_000,
                EngineOptions = FenEngineOptions.Development
            };
        }

        /// <summary>
        /// Gets the effective engine options, falling back to default if not set.
        /// </summary>
        public FenEngineOptions GetEffectiveOptions() => EngineOptions ?? FenEngineOptions.Default;
    }
}
