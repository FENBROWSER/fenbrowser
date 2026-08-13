using System;
using System.Collections.Generic;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Security.Sandbox
{
    /// <summary>
    /// Centralized sandbox acquisition policy. This keeps launch behavior explicit and
    /// prevents inactive/no-op sandboxes from masquerading as real enforcement when the
    /// caller has not explicitly opted into unsandboxed fallback.
    /// </summary>
    public static class SandboxLaunchPolicy
    {
        public static bool TryAcquire(
            string surface,
            IOsSandboxFactory sandboxFactory,
            OsSandboxProfile profile,
            bool allowUnsandboxedFallback,
            string overrideEnvKey,
            out ISandbox sandbox)
        {
            sandbox = null;

            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["surface"] = surface ?? string.Empty,
                ["profile"] = profile?.Kind.ToString() ?? string.Empty,
                ["allowUnsandboxedFallback"] = allowUnsandboxedFallback,
                ["overrideEnvKey"] = overrideEnvKey ?? string.Empty
            };

            if (profile == null)
            {
                SecurityDecision.Deny(
                    "sandbox-launch",
                    "missing-profile",
                    $"Refusing {surface} launch because no sandbox profile was provided.",
                    data).Log(LogCategory.ProcessIsolation);
                return false;
            }

            if (sandboxFactory == null)
            {
                if (!allowUnsandboxedFallback)
                {
                    SecurityDecision.Deny(
                        "sandbox-launch",
                        "missing-factory",
                        $"Refusing {surface} launch because no sandbox factory is available. Set {overrideEnvKey}=1 to override.",
                        data).Log(LogCategory.ProcessIsolation);
                    return false;
                }

                SecurityDecision.Allow(
                    "sandbox-launch",
                    "missing-factory-unsandboxed",
                    $"Launching {surface} without an OS sandbox because no sandbox factory is available.",
                    data).Log(LogCategory.ProcessIsolation, LogLevel.Warn);
                return true;
            }

            if (!sandboxFactory.IsSandboxingSupported)
            {
                if (!allowUnsandboxedFallback)
                {
                    SecurityDecision.Deny(
                        "sandbox-launch",
                        "unsupported-platform",
                        $"Refusing {surface} launch because the current platform does not provide the requested OS sandbox. Set {overrideEnvKey}=1 to override.",
                        data).Log(LogCategory.ProcessIsolation);
                    return false;
                }

                SecurityDecision.Allow(
                    "sandbox-launch",
                    "unsupported-platform-unsandboxed",
                    $"Launching {surface} without an OS sandbox because the current platform does not provide the requested sandbox primitives.",
                    data).Log(LogCategory.ProcessIsolation, LogLevel.Warn);
                return true;
            }

            try
            {
                var acquired = sandboxFactory.Create(profile);
                if (acquired == null)
                {
                    return HandleUnenforcedSandbox(
                        surface,
                        profile,
                        allowUnsandboxedFallback,
                        overrideEnvKey,
                        data,
                        "sandbox-factory-returned-null",
                        "sandbox factory returned no sandbox instance",
                        out sandbox);
                }

                if (acquired is NullSandbox || !acquired.IsActive)
                {
                    var reason = acquired is NullSandbox
                        ? "the resolved sandbox is NullSandbox"
                        : $"sandbox '{acquired.ProfileName}' is not active";
                    var decisionCode = acquired is NullSandbox
                        ? "null-sandbox"
                        : "inactive-sandbox";

                    acquired.Dispose();
                    return HandleUnenforcedSandbox(
                        surface,
                        profile,
                        allowUnsandboxedFallback,
                        overrideEnvKey,
                        data,
                        decisionCode,
                        reason,
                        out sandbox);
                }

                sandbox = acquired;
                data["capabilities"] = sandbox.Capabilities.ToString();
                SecurityDecision.Allow(
                    "sandbox-launch",
                    "sandbox-acquired",
                    $"Acquired active sandbox '{sandbox.ProfileName}' for {surface}.",
                    data).Log(LogCategory.ProcessIsolation);
                return true;
            }
            catch (Exception ex)
            {
                data["exceptionType"] = ex.GetType().Name;
                data["exceptionMessage"] = ex.Message;

                if (!allowUnsandboxedFallback)
                {
                    SecurityDecision.Deny(
                        "sandbox-launch",
                        "sandbox-creation-failed",
                        $"Refusing {surface} launch because sandbox creation failed: {ex.Message}",
                        data).Log(LogCategory.ProcessIsolation);
                    return false;
                }

                SecurityDecision.Allow(
                    "sandbox-launch",
                    "sandbox-creation-failed-unsandboxed",
                    $"Launching {surface} without an OS sandbox because sandbox creation failed: {ex.Message}",
                    data).Log(LogCategory.ProcessIsolation, LogLevel.Warn);
                return true;
            }
        }

        private static bool HandleUnenforcedSandbox(
            string surface,
            OsSandboxProfile profile,
            bool allowUnsandboxedFallback,
            string overrideEnvKey,
            Dictionary<string, object> data,
            string decisionCode,
            string reason,
            out ISandbox sandbox)
        {
            sandbox = null;
            data["profile"] = profile?.Kind.ToString() ?? string.Empty;

            if (!allowUnsandboxedFallback)
            {
                SecurityDecision.Deny(
                    "sandbox-launch",
                    decisionCode + "-denied",
                    $"Refusing {surface} launch because {reason}. Set {overrideEnvKey}=1 to override.",
                    data).Log(LogCategory.ProcessIsolation);
                return false;
            }

            SecurityDecision.Allow(
                "sandbox-launch",
                decisionCode + "-unsandboxed",
                $"Launching {surface} without OS sandbox enforcement because {reason}.",
                data).Log(LogCategory.ProcessIsolation, LogLevel.Warn);
            return true;
        }
    }
}
