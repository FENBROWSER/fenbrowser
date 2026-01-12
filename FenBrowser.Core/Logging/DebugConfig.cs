namespace FenBrowser.Core.Logging
{
    public static class DebugConfig
    {
        // Toggle these flags to enable detailed subsystems
        public static bool EnableDeepDebug = true;

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
        
        public static bool ShouldLog(string className)
        {
            // Uncomment to force all logs (verbose):
            return true; 

            if (!EnableDeepDebug || string.IsNullOrEmpty(className)) return false;
            foreach (var c in DebugClasses)
                if (className.Contains(c)) return true;
            return false;
        }
    }
}
