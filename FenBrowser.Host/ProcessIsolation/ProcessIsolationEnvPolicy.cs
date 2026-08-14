using System;

namespace FenBrowser.Host.ProcessIsolation
{
    internal static class ProcessIsolationEnvPolicy
    {
        internal static bool IsUnsandboxedFallbackEnabled(string envKey)
        {
            if (string.IsNullOrWhiteSpace(envKey))
            {
                return false;
            }

            var raw = Environment.GetEnvironmentVariable(envKey);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            raw = raw.Trim();
            return string.Equals(raw, "1", StringComparison.Ordinal) ||
                   string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
