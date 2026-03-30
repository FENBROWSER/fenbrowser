using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.Core.Logging
{
    public static class DebugConfig
    {
        // Toggle these flags to enable detailed subsystems
        public static bool EnableDeepDebug = true;
        public static bool LogAllClasses = false;

        public static bool LogCssComputed = true;
        public static bool LogDomTree = true;
        public static bool LogLayoutConstraints = true; // Checking text issue
        public static bool LogPaintCommands = true;
        
        // Extended Logging Scopes (Phase 8)
        public static bool LogResourceLoader = true;  // 1. Network/MIME
        public static bool LogHtmlParse = true;      // 2. Recovery/Implied tags
        // LogDomTree already exists (3)
        public static bool LogCssParse = true;       // 4. Ignored/Unsupported props
        public static bool LogCssCascade = true;     // 5. Specificity/Winning rules
        // LogCssComputed already exists (6)
        public static bool LogTextShaping = true;     // 7. Font/Glyphs
        public static bool LogFlexLayout = true;      // 9. Flex decisions
        public static bool LogEventWiring = true;     // 11. Events
        public static bool LogFrameTiming = true;
        public static bool LogVerification = true;     // 12. Orchestrator


        // Filter to specific elements to avoid massive logs
        public static string[] DebugClasses = new[] { "site-name", "site-link", "site-icon", "search-box", "container" };

        public static IReadOnlyList<string> NormalizedDebugClasses =>
            NormalizeDebugClasses(DebugClasses);

        public static bool ShouldLog(string className)
        {
            if (!EnableDeepDebug)
            {
                return false;
            }

            if (LogAllClasses)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(className))
            {
                return false;
            }

            string normalizedClassName = className.Trim();
            foreach (var filter in NormalizedDebugClasses)
            {
                if (normalizedClassName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static void ResetToDefaults()
        {
            EnableDeepDebug = true;
            LogAllClasses = false;
            LogCssComputed = true;
            LogDomTree = true;
            LogLayoutConstraints = true;
            LogPaintCommands = true;
            LogResourceLoader = true;
            LogHtmlParse = true;
            LogCssParse = true;
            LogCssCascade = true;
            LogTextShaping = true;
            LogFlexLayout = true;
            LogEventWiring = true;
            LogFrameTiming = true;
            LogVerification = true;
            DebugClasses = new[] { "site-name", "site-link", "site-icon", "search-box", "container" };
        }

        private static IReadOnlyList<string> NormalizeDebugClasses(IEnumerable<string> values)
        {
            if (values == null)
            {
                return Array.Empty<string>();
            }

            return values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}
