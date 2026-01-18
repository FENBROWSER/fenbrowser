// =============================================================================
// CapabilityGuard.cs
// WebDriver Security - Capability Enforcement
// 
// PURPOSE: Enforces capability restrictions to limit dangerous operations.
// SECURITY: Block insecure certs, restrict file access, limit scripts.
// =============================================================================

using System.Collections.Generic;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver.Security
{
    /// <summary>
    /// Enforces security restrictions based on session capabilities.
    /// </summary>
    public class CapabilityGuard
    {
        private readonly Session _session;
        
        public CapabilityGuard(Session session)
        {
            _session = session;
        }
        
        /// <summary>
        /// Check if insecure certificates are allowed.
        /// </summary>
        public bool AllowInsecureCerts => _session.Capabilities.AcceptInsecureCerts;
        
        /// <summary>
        /// Check if a URL is allowed for navigation.
        /// </summary>
        public bool IsUrlAllowed(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            
            // Block file:// URLs by default for security
            if (url.StartsWith("file://", System.StringComparison.OrdinalIgnoreCase))
            {
                return AllowFileUrls();
            }
            
            // Block javascript: URLs
            if (url.StartsWith("javascript:", System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            
            // Block data: URLs with scripts
            if (url.StartsWith("data:", System.StringComparison.OrdinalIgnoreCase))
            {
                return !url.Contains("script", System.StringComparison.OrdinalIgnoreCase);
            }
            
            return true;
        }
        
        /// <summary>
        /// Check if file:// URLs are allowed.
        /// </summary>
        private bool AllowFileUrls()
        {
            var fenOptions = _session.Capabilities.FenOptions;
            if (fenOptions?.Args != null)
            {
                foreach (var arg in fenOptions.Args)
                {
                    if (arg == "--allow-file-access")
                        return true;
                }
            }
            return false;
        }
        
        /// <summary>
        /// Check if a script is allowed to execute.
        /// </summary>
        public bool IsScriptAllowed(string script)
        {
            if (string.IsNullOrEmpty(script))
                return false;
            
            // Block potentially dangerous patterns
            var blockedPatterns = new[]
            {
                "process.exit",
                "require('child_process')",
                "eval(atob(",
                "Function('return this')()"
            };
            
            foreach (var pattern in blockedPatterns)
            {
                if (script.Contains(pattern, System.StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// Get effective timeout for script.
        /// </summary>
        public int GetScriptTimeout()
        {
            return _session.Timeouts.Script ?? 30000;
        }
        
        /// <summary>
        /// Get effective timeout for page load.
        /// </summary>
        public int GetPageLoadTimeout()
        {
            return _session.Timeouts.PageLoad ?? 300000;
        }
    }
}
