using System;

namespace FenBrowser.Host.ProcessIsolation
{
    internal static class ProcessIsolationEnvPolicy
    {
        internal static bool IsUnsandboxedFallbackEnabled(string envKey)
        {
            var raw = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}
