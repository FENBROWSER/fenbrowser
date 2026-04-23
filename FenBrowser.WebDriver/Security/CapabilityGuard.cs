// =============================================================================
// CapabilityGuard.cs
// WebDriver Security - Capability Enforcement
// 
// PURPOSE: Enforces capability restrictions to limit dangerous operations.
// SECURITY: Block insecure certs, restrict file access, limit scripts.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver.Security
{
    /// <summary>
    /// Enforces security restrictions based on session capabilities.
    /// </summary>
    public class CapabilityGuard
    {
        private readonly Session _session;
        private static readonly HashSet<string> RiskyCapabilityArgs = new(StringComparer.OrdinalIgnoreCase)
        {
            "--allow-file-access",
            "--allow-insecure-localhost",
            "--disable-web-security"
        };
        private const string RiskyCapabilityOptIn = "--webdriver-allow-risky-capabilities";
        
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
            return EvaluateUrlPolicy(url).Allowed;
        }

        public SecurityDecision EvaluateUrlPolicy(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return SecurityDecision.Block(SecurityBlockReasons.NavigationUrlInvalid, "Navigation URL is empty");
            }
            
            // Block file:// URLs by default for security
            if (url.StartsWith("file://", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!AllowFileUrls())
                {
                    return SecurityDecision.Block(
                        SecurityBlockReasons.NavigationUrlBlocked,
                        "file:// navigation requires explicit risky capability opt-in");
                }

                return SecurityDecision.Allow();
            }
            
            // Block javascript: URLs
            if (url.StartsWith("javascript:", System.StringComparison.OrdinalIgnoreCase))
            {
                return SecurityDecision.Block(SecurityBlockReasons.NavigationUrlBlocked, "javascript: navigation is blocked");
            }
            
            // Block data: URLs with scripts
            if (url.StartsWith("data:", System.StringComparison.OrdinalIgnoreCase))
            {
                if (url.Contains("script", System.StringComparison.OrdinalIgnoreCase))
                {
                    return SecurityDecision.Block(SecurityBlockReasons.NavigationUrlBlocked, "data: navigation containing script is blocked");
                }

                return SecurityDecision.Allow();
            }
            
            return SecurityDecision.Allow();
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
            return EvaluateScriptPolicy(script).Allowed;
        }

        public SecurityDecision EvaluateScriptPolicy(string script)
        {
            if (string.IsNullOrEmpty(script))
            {
                return SecurityDecision.Block(SecurityBlockReasons.ScriptBlocked, "Script source is empty");
            }
            
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
                    return SecurityDecision.Block(
                        SecurityBlockReasons.ScriptBlocked,
                        $"Script contains blocked pattern: {pattern}");
                }
            }
            
            return SecurityDecision.Allow();
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

        public static SecurityDecision ValidateRequestedCapabilities(Capabilities? caps)
        {
            var args = caps?.FenOptions?.Args ?? new List<string>();
            if (args.Count == 0)
            {
                return SecurityDecision.Allow();
            }

            var hasRiskyOptIn = args.Any(arg => string.Equals(arg, RiskyCapabilityOptIn, StringComparison.OrdinalIgnoreCase));
            var riskyArgs = args.Where(arg => RiskyCapabilityArgs.Contains(arg)).ToArray();
            if (riskyArgs.Length == 0)
            {
                return SecurityDecision.Allow();
            }

            if (!hasRiskyOptIn)
            {
                return SecurityDecision.Block(
                    SecurityBlockReasons.CapabilityPolicyViolation,
                    $"Risky capability arguments require explicit opt-in {RiskyCapabilityOptIn}: {string.Join(", ", riskyArgs)}");
            }

            return SecurityDecision.Allow();
        }
    }

    public readonly struct SecurityDecision
    {
        public bool Allowed { get; }
        public string ReasonCode { get; }
        public string Detail { get; }

        private SecurityDecision(bool allowed, string reasonCode, string detail)
        {
            Allowed = allowed;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public static SecurityDecision Allow() => new(true, string.Empty, string.Empty);

        public static SecurityDecision Block(string reasonCode, string detail) => new(false, reasonCode, detail);
    }
}
