using System;

namespace FenBrowser.Host.ProcessIsolation
{
    internal static class ProcessIsolationEnvPolicy
    {
        private const string UnsafeDeveloperModeEnvKey = "FEN_UNSAFE_DEVELOPER_MODE";

        internal static bool IsUnsandboxedFallbackEnabled(string envKey)
        {
            if (string.IsNullOrWhiteSpace(envKey))
            {
                return false;
            }

#if !DEBUG
            // Unsandboxed child-process fallback is a local-development escape hatch,
            // never a deployable runtime policy. Release builds fail closed even when
            // an inherited environment contains one of the legacy override variables.
            return false;
#else
            // Debug builds still require a two-step opt-in so setting a subsystem's
            // fallback variable alone cannot silently disable a process sandbox.
            if (!IsEnabled(UnsafeDeveloperModeEnvKey))
            {
                return false;
            }

            return IsEnabled(envKey);
#endif
        }

#if DEBUG
        private static bool IsEnabled(string envKey)
        {
            var raw = Environment.GetEnvironmentVariable(envKey);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            raw = raw.Trim();
            return string.Equals(raw, "1", StringComparison.Ordinal) ||
                   string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
#endif
    }
}
