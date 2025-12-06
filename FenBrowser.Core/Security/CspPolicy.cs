using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.Core.Security
{
    public class CspPolicy
    {
        public Dictionary<string, CspDirective> Directives { get; } = new Dictionary<string, CspDirective>(StringComparer.OrdinalIgnoreCase);

        public bool IsAllowed(string directiveName, Uri url, bool isInline = false, bool isEval = false)
        {
            // If no policy, everything allowed
            if (Directives.Count == 0) return true;

            // Resolve directive (fallback to default-src)
            CspDirective directive = null;
            if (!Directives.TryGetValue(directiveName, out directive))
            {
                if (!Directives.TryGetValue("default-src", out directive))
                {
                    // No default-src and specific directive not found -> Allowed by default in CSP spec?
                    // "If the directive is not present in the policy, it allows all accesses." (unless it falls back to default-src)
                    // default-src falls back for: script-src, style-src, img-src, connect-src, font-src, object-src, media-src, frame-src, etc.
                    // base-uri, form-action, frame-ancestors do NOT fall back.
                    
                    var fallbacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                    { 
                        "script-src", "style-src", "img-src", "connect-src", "font-src", 
                        "object-src", "media-src", "frame-src", "child-src", "worker-src", "manifest-src" 
                    };

                    if (fallbacks.Contains(directiveName)) return true; // Actually, if no default-src, these are allowed.
                    return true;
                }
            }

            if (directive == null) return true; // Should be covered above but safety check/logic flow

            return directive.IsAllowed(url, isInline, isEval);
        }

        public static CspPolicy Parse(string headerValue)
        {
            var policy = new CspPolicy();
            if (string.IsNullOrWhiteSpace(headerValue)) return policy;

            var parts = headerValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var tokens = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                var name = tokens[0];
                var sources = tokens.Skip(1).ToArray();

                if (!policy.Directives.ContainsKey(name))
                {
                    policy.Directives[name] = new CspDirective(name, sources);
                }
            }
            return policy;
        }
    }

    public class CspDirective
    {
        public string Name { get; }
        public HashSet<string> Sources { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CspDirective(string name, string[] sources)
        {
            Name = name;
            foreach (var s in sources) Sources.Add(s);
        }

        public bool IsAllowed(Uri url, bool isInline = false, bool isEval = false, Uri origin = null)
        {
            if (Sources.Contains("'none'")) return false;

            if (isInline) return Sources.Contains("'unsafe-inline'");
            if (isEval) return Sources.Contains("'unsafe-eval'");
            
            if (url == null) return false; // Inline/eval check passed/failed, strictly URL check now

            if (Sources.Contains("*")) return true;

            // Check 'self' - same origin check
            if (Sources.Contains("'self'") && origin != null)
            {
                if (IsSameOrigin(url, origin)) return true;
            }

            // Check 'self'
            // We need the origin context to check 'self'. 
            // Currently CspPolicy.IsAllowed signature doesn't take origin. 
            // We should assume the caller has resolved 'self' or we need to change signature.
            // For now, let's assume 'self' needs to be checked by caller or we ignore it here if we don't know origin?
            // BETTER: Pass originUri to IsAllowed. For now, let's match exact host if provided in list.
            
            // For this basic implementation, we support:
            // - scheme: (https:, data:)
            // - host (example.com, *.example.com)
            
            foreach (var src in Sources)
            {
                if (src == "*") return true;
                if (src.StartsWith("'")) continue; // keywords like 'self', 'unsafe-inline' (handled above/separately)

                // Scheme check (e.g. https:)
                if (src.EndsWith(":"))
                {
                    if (string.Equals(url.Scheme + ":", src, StringComparison.OrdinalIgnoreCase)) return true;
                    continue;
                }

                // Host check
                // src could be "example.com", "*.example.com", "https://example.com"
                
                // Simple parsing of src
                string srcScheme = null;
                string srcHost = src;
                string srcPort = null;

                if (src.Contains("://"))
                {
                    var uriParts = src.Split(new[] { "://" }, 2, StringSplitOptions.None);
                    srcScheme = uriParts[0];
                    srcHost = uriParts[1];
                }

                // Handle port
                var portIdx = srcHost.IndexOf(':');
                if (portIdx >= 0)
                {
                    srcPort = srcHost.Substring(portIdx + 1);
                    srcHost = srcHost.Substring(0, portIdx);
                }
                
                // Remove path if present (CSP hosts typically don't include path unless specific)
                var slash = srcHost.IndexOf('/');
                if (slash >= 0) srcHost = srcHost.Substring(0, slash);

                // Scheme matching
                if (srcScheme != null && !string.Equals(url.Scheme, srcScheme, StringComparison.OrdinalIgnoreCase)) continue;

                // Port matching
                if (srcPort != null)
                {
                    // If port specified, must match. 
                    // If url port is default (-1), we need to map to 80/443
                    var uPort = url.Port;
                    if (uPort == -1) uPort = url.Scheme == "https" ? 443 : 80;
                    
                    if (srcPort == "*") { /* allowed */ }
                    else if (int.TryParse(srcPort, out int p) && p != uPort) continue;
                }

                // Host matching
                if (srcHost == "*") return true;
                if (srcHost.StartsWith("*."))
                {
                    var suffix = srcHost.Substring(2);
                    if (url.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else
                {
                    if (string.Equals(url.Host, srcHost, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        /// <summary>Check if two URIs have the same origin (scheme, host, port)</summary>
        private static bool IsSameOrigin(Uri url, Uri origin)
        {
            if (url == null || origin == null) return false;
            
            // Scheme must match
            if (!string.Equals(url.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase))
                return false;
            
            // Host must match
            if (!string.Equals(url.Host, origin.Host, StringComparison.OrdinalIgnoreCase))
                return false;
            
            // Port must match (handle default ports)
            int urlPort = url.Port == -1 ? GetDefaultPort(url.Scheme) : url.Port;
            int originPort = origin.Port == -1 ? GetDefaultPort(origin.Scheme) : origin.Port;
            
            return urlPort == originPort;
        }

        private static int GetDefaultPort(string scheme)
        {
            return scheme?.ToLowerInvariant() switch
            {
                "http" => 80,
                "https" => 443,
                "ftp" => 21,
                _ => -1
            };
        }
    }
}
