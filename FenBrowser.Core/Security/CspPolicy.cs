using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.Core.Security
{
    public class CspPolicy
    {
        public Dictionary<string, CspDirective> Directives { get; } = new Dictionary<string, CspDirective>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Check if a resource URL is allowed by the policy.
        /// </summary>
        public bool IsAllowed(string directiveName, Uri url, bool isInline = false, bool isEval = false)
        {
            return IsAllowed(directiveName, url, null, isInline, isEval);
        }

        /// <summary>
        /// Check if a resource is allowed, optionally validating a nonce.
        /// </summary>
        public bool IsAllowed(string directiveName, Uri url, string nonce, bool isInline = false, bool isEval = false)
        {
            // If no policy, everything allowed
            if (Directives.Count == 0) return true;

            // Resolve directive (fallback to default-src)
            CspDirective directive = null;
            if (!Directives.TryGetValue(directiveName, out directive))
            {
                if (!Directives.TryGetValue("default-src", out directive))
                {
                    // No default-src fallback for base-uri, form-action, frame-ancestors
                    var noFallback = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                    { 
                        "base-uri", "form-action", "frame-ancestors", "sandbox" 
                    };

                    if (noFallback.Contains(directiveName)) return true;

                    // If default-src is missing, fallback directives are allowed
                    return true;
                }
            }

            if (directive == null) return true; 

            return directive.IsAllowed(url, nonce, isInline, isEval);
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
        public HashSet<string> Nonces { get; } = new HashSet<string>(StringComparer.Ordinal); // Case-sensitive nonces

        public CspDirective(string name, string[] sources)
        {
            Name = name;
            foreach (var s in sources) 
            {
                if (s.StartsWith("'nonce-", StringComparison.OrdinalIgnoreCase) && s.EndsWith("'"))
                {
                    // Extract nonce value: 'nonce-abc' -> abc
                    var val = s.Substring(7, s.Length - 8);
                    Nonces.Add(val);
                }
                Sources.Add(s);
            }
        }

        public bool IsAllowed(Uri url, string nonce, bool isInline = false, bool isEval = false, Uri origin = null)
        {
            if (Sources.Contains("'none'")) return false;

            // 1. Nonce Check (Overrides inline checks if present)
            if (!string.IsNullOrEmpty(nonce))
            {
                if (Nonces.Contains(nonce)) return true;
                // If nonce is provided but doesn't match, do we fail immediately?
                // CSP Spec: "If 'nonce-source' is present in the list of allowed sources... 
                // matches if the element has a nonce attribute..."
                // If the element HAS a nonce, it MUST allow it. 
                // But if the policy requires a nonce (contains 'nonce-...') and the element nonce is bad, is it blocked?
                // Actually, CSP allows if ANY source matches.
            }

            // 2. Inline Check
            if (isInline) 
            {
                // If 'unsafe-inline' is present, it's allowed UNLESS a nonce/hash is present in the policy (in modern CSP).
                // "If a directive contains a nonce-source or hash-source... 'unsafe-inline' is ignored."
                // For simplicity, we'll implement strict check: if we have nonces, we ignore unsafe-inline.
                bool hasNonces = Nonces.Count > 0;
                if (!hasNonces && Sources.Contains("'unsafe-inline'")) return true;
                
                // If we have nonces, we only allow if the nonce matched above.
                if (hasNonces) return !string.IsNullOrEmpty(nonce) && Nonces.Contains(nonce);

                return false;
            }

            // 3. Eval Check
            if (isEval) return Sources.Contains("'unsafe-eval'");
            
            // 4. URL Check
            if (url == null) return false; 

            if (Sources.Contains("*") || (url.Scheme == "data" && Sources.Contains("data:")) || (url.Scheme == "blob" && Sources.Contains("blob:"))) return true;

            // Check 'self'
            if (Sources.Contains("'self'") && origin != null)
            {
                if (IsSameOrigin(url, origin)) return true;
            }

            foreach (var src in Sources)
            {
                if (src == "*") return true;
                if (src.StartsWith("'")) continue; // keywords

                // Scheme check
                if (src.EndsWith(":"))
                {
                    if (string.Equals(url.Scheme + ":", src, StringComparison.OrdinalIgnoreCase)) return true;
                    continue;
                }

                // Host matching logic (simplified)
                string srcHost = src;
                string srcScheme = null;
                string srcPort = null;

                if (src.Contains("://"))
                {
                    var uriParts = src.Split(new[] { "://" }, 2, StringSplitOptions.None);
                    srcScheme = uriParts[0];
                    srcHost = uriParts[1];
                }

                var portIdx = srcHost.IndexOf(':');
                if (portIdx >= 0)
                {
                    srcPort = srcHost.Substring(portIdx + 1);
                    srcHost = srcHost.Substring(0, portIdx);
                }
                
                var slash = srcHost.IndexOf('/');
                if (slash >= 0) srcHost = srcHost.Substring(0, slash);

                if (srcScheme != null && !string.Equals(url.Scheme, srcScheme, StringComparison.OrdinalIgnoreCase)) continue;

                if (srcPort != null)
                {
                    var uPort = url.Port;
                    if (uPort == -1) uPort = url.Scheme == "https" ? 443 : 80;
                    if (srcPort != "*" && int.TryParse(srcPort, out int p) && p != uPort) continue;
                }

                if (srcHost == "*") return true;
                if (srcHost.StartsWith("*."))
                {
                    if (url.Host.EndsWith(srcHost.Substring(2), StringComparison.OrdinalIgnoreCase)) return true;
                }
                else
                {
                    if (string.Equals(url.Host, srcHost, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        private static bool IsSameOrigin(Uri url, Uri origin)
        {
            if (url == null || origin == null) return false;
            if (!string.Equals(url.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(url.Host, origin.Host, StringComparison.OrdinalIgnoreCase)) return false;
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
