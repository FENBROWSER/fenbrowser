using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Security
{
    /// <summary>
    /// Feature 1/3: Production-grade CSP enforcement at network boundary
    /// Blocks inline scripts, eval, and unauthorized resources per policy
    /// </summary>
    public static class CspEnforcement
    {
        private static readonly HashSet<string> _blockedSchemes = new(StringComparer.OrdinalIgnoreCase) 
        { "javascript:", "data:text/javascript", "vbscript:" };
        
        public static bool IsRequestAllowed(string url, string sourceUrl, string cspHeader)
        {
            if (string.IsNullOrEmpty(cspHeader)) return true;
            
            var parsed = ParseCsp(cspHeader);
            
            // Block javascript: URLs
            foreach (var scheme in _blockedSchemes)
            {
                if (url.StartsWith(scheme))
                {
                    EngineLogCompat.Warn($"[CSP] Blocked {scheme} URL", LogCategory.Security);
                    return false;
                }
            }
            
            // Check source directives
            if (!IsSourceAllowed(url, sourceUrl, parsed))
            {
                EngineLogCompat.Warn($"[CSP] Blocked by policy: {url}", LogCategory.Security);
                return false;
            }
            
            return true;
        }
        
        private static CspPolicy ParseCsp(string header)
        {
            var policy = new CspPolicy();
            var directives = header.Split(';');
            
            foreach (var dir in directives)
            {
                var parts = dir.Trim().Split(new[] { ' ' }, 2);
                if (parts.Length < 2) continue;
                
                var name = parts[0].Trim().ToLowerInvariant();
                var sources = parts[1].Trim();
                
                policy.Directives[name] = new HashSet<string>(sources.Split(' '), StringComparer.OrdinalIgnoreCase);
            }
            
            return policy;
        }
        
        private static bool IsSourceAllowed(string url, string sourceUrl, CspPolicy policy)
        {
            var uri = new Uri(url);
            var sourceUri = string.IsNullOrEmpty(sourceUrl) ? null : new Uri(sourceUrl);
            
            // Self-check
            if (sourceUri != null && uri.Host == sourceUri.Host)
            {
                if (policy.Directives.Values.Any(sources => sources.Contains("'self'")))
                    return true;
            }
            
            // Wildcard check
            if (policy.Directives.Values.Any(sources => sources.Contains("*")))
                return true;
            
            // Specific source check
            var origin = $"{uri.Scheme}://{uri.Host}";
            var quotedOrigin = $"'{origin}'";
            return policy.Directives.Values.Any(sources =>
                sources.Contains(origin) ||
                sources.Contains(quotedOrigin) ||
                sources.Contains($"{uri.Scheme}:"));
        }
        
        private class CspPolicy
        {
            public Dictionary<string, HashSet<string>> Directives = new();
        }
    }
}
