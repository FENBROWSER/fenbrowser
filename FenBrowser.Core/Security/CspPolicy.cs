// SpecRef: Content Security Policy Level 3, source-list enforcement
// CapabilityId: SECURITY-CSP-ENFORCEMENT-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// CSP directive names as defined in CSP Level 3.
    /// </summary>
    public static class CspDirectiveNames
    {
        public const string DefaultSrc = "default-src";
        public const string ScriptSrc = "script-src";
        public const string ScriptSrcElem = "script-src-elem";
        public const string ScriptSrcAttr = "script-src-attr";
        public const string StyleSrc = "style-src";
        public const string StyleSrcElem = "style-src-elem";
        public const string StyleSrcAttr = "style-src-attr";
        public const string ImgSrc = "img-src";
        public const string ConnectSrc = "connect-src";
        public const string FontSrc = "font-src";
        public const string ObjectSrc = "object-src";
        public const string MediaSrc = "media-src";
        public const string FrameSrc = "frame-src";
        public const string ChildSrc = "child-src";
        public const string WorkerSrc = "worker-src";
        public const string ManifestSrc = "manifest-src";
        public const string PrefetchSrc = "prefetch-src";
        public const string BaseUri = "base-uri";
        public const string FormAction = "form-action";
        public const string FrameAncestors = "frame-ancestors";
        public const string NavigateTo = "navigate-to";
        public const string Sandbox = "sandbox";
        public const string ReportUri = "report-uri";
        public const string ReportTo = "report-to";
        public const string TrustedTypes = "trusted-types";
        public const string RequireTrustedTypesFor = "require-trusted-types-for";
        public const string UpgradeInsecureRequests = "upgrade-insecure-requests";
        public const string BlockAllMixedContent = "block-all-mixed-content";
        public const string RequireSriFor = "require-sri-for";
        
        /// <summary>
        /// All directive names that use source-list syntax.
        /// </summary>
        public static readonly HashSet<string> SourceListDirectives = new(StringComparer.OrdinalIgnoreCase)
        {
            DefaultSrc, ScriptSrc, ScriptSrcElem, ScriptSrcAttr,
            StyleSrc, StyleSrcElem, StyleSrcAttr,
            ImgSrc, ConnectSrc, FontSrc, ObjectSrc, MediaSrc,
            FrameSrc, ChildSrc, WorkerSrc, ManifestSrc, PrefetchSrc
        };
        
        /// <summary>
        /// Directives that do NOT fall back to default-src.
        /// </summary>
        public static readonly HashSet<string> NoFallbackDirectives = new(StringComparer.OrdinalIgnoreCase)
        {
            BaseUri, FormAction, FrameAncestors, Sandbox, ReportUri, ReportTo,
            TrustedTypes, RequireTrustedTypesFor, UpgradeInsecureRequests,
            BlockAllMixedContent, RequireSriFor, NavigateTo
        };
    }
    public class CspPolicy
    {
        public Dictionary<string, CspDirective> Directives { get; } = new Dictionary<string, CspDirective>(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// The original CSP header value, stored for inspection (e.g., upgrade-insecure-requests check).
        /// </summary>
        public string HeaderValue { get; private set; }

        /// <summary>
        /// Check if a resource URL is allowed by the policy.
        /// </summary>
        public bool IsAllowed(string directiveName, Uri url, bool isInline = false, bool isEval = false)
        {
            return IsAllowed(directiveName, url, nonce: null, origin: null, isInline: isInline, isEval: isEval);
        }

        /// <summary>
        /// Check if a resource URL is allowed by the policy with an explicit origin context.
        /// </summary>
        public bool IsAllowed(string directiveName, Uri url, Uri origin, bool isInline = false, bool isEval = false)
        {
            return IsAllowed(directiveName, url, nonce: null, origin: origin, isInline: isInline, isEval: isEval);
        }

        /// <summary>
        /// Check if a resource is allowed, optionally validating a nonce.
        /// </summary>
        public bool IsAllowed(string directiveName, Uri url, string nonce, bool isInline = false, bool isEval = false, string elementHash = null, string elementTrustedType = null)
        {
            return IsAllowed(directiveName, url, nonce, origin: null, isInline: isInline, isEval: isEval, elementHash: elementHash, elementTrustedType: elementTrustedType);
        }

        /// <summary>
        /// Check if a resource is allowed with explicit nonce and origin context.
        /// </summary>
        public bool IsAllowed(string directiveName, Uri url, string nonce, Uri origin, bool isInline = false, bool isEval = false, string elementHash = null, string elementTrustedType = null)
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

            return directive.IsAllowed(url, nonce, isInline, isEval, origin, elementHash, elementTrustedType);
        }

        public static CspPolicy Parse(string headerValue)
        {
            var policy = new CspPolicy
            {
                HeaderValue = headerValue
            };
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

        /// <summary>
        /// Checks if the policy contains the upgrade-insecure-requests directive.
        /// </summary>
        public bool HasUpgradeInsecureRequests()
        {
            if (string.IsNullOrWhiteSpace(HeaderValue)) return false;
            
            var directives = HeaderValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in directives)
            {
                var trimmed = dir.Trim();
                if (trimmed.Equals("upgrade-insecure-requests", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (trimmed.StartsWith("upgrade-insecure-requests", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    public class CspDirective
    {
        public string Name { get; }
        public HashSet<string> Sources { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Nonces { get; } = new HashSet<string>(StringComparer.Ordinal); // Case-sensitive nonces
        public HashSet<string> Hashes { get; } = new HashSet<string>(StringComparer.Ordinal); // Case-sensitive hashes (sha256-...)
        public bool HasStrictDynamic { get; private set; }
        public bool HasReportSample { get; private set; }
        public HashSet<string> TrustedTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string ReportUri { get; private set; }
        public string ReportTo { get; private set; }

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
                    Sources.Add(s);
                }
                else if (s.StartsWith("'sha256-", StringComparison.OrdinalIgnoreCase) && s.EndsWith("'"))
                {
                    // Extract hash value: 'sha256-abc' -> sha256-abc
                    var val = s.Substring(1, s.Length - 2);
                    Hashes.Add(val);
                    Sources.Add(s);
                }
                else if (s.StartsWith("'sha384-", StringComparison.OrdinalIgnoreCase) && s.EndsWith("'"))
                {
                    var val = s.Substring(1, s.Length - 2);
                    Hashes.Add(val);
                    Sources.Add(s);
                }
                else if (s.StartsWith("'sha512-", StringComparison.OrdinalIgnoreCase) && s.EndsWith("'"))
                {
                    var val = s.Substring(1, s.Length - 2);
                    Hashes.Add(val);
                    Sources.Add(s);
                }
                else if (string.Equals(s, "'strict-dynamic'", StringComparison.OrdinalIgnoreCase))
                {
                    HasStrictDynamic = true;
                    Sources.Add(s);
                }
                else if (string.Equals(s, "'report-sample'", StringComparison.OrdinalIgnoreCase))
                {
                    HasReportSample = true;
                    Sources.Add(s);
                }
                else if (s.StartsWith("'trusted-types", StringComparison.OrdinalIgnoreCase) && s.EndsWith("'"))
                {
                    // 'trusted-types policyName'
                    var val = s.Substring(14, s.Length - 15);
                    TrustedTypes.Add(val);
                    Sources.Add(s);
                }
                else if (s.StartsWith("report-uri", StringComparison.OrdinalIgnoreCase))
                {
                    ReportUri = ExtractReportUri(s);
                    Sources.Add(s);
                }
                else if (s.StartsWith("report-to", StringComparison.OrdinalIgnoreCase))
                {
                    ReportTo = ExtractReportTo(s);
                    Sources.Add(s);
                }
                else
                {
                    Sources.Add(s);
                }
            }
        }

        private static string ExtractReportUri(string s)
        {
            // report-uri <url> or report-uri(<url>)
            var start = s.IndexOf(' ', StringComparison.Ordinal);
            if (start >= 0)
            {
                var val = s.Substring(start + 1).Trim();
                if (val.StartsWith("(", StringComparison.Ordinal) && val.EndsWith(")", StringComparison.Ordinal))
                {
                    return val.Substring(1, val.Length - 2);
                }
                return val;
            }
            return null;
        }

        private static string ExtractReportTo(string s)
        {
            // report-to <groupname>
            var start = s.IndexOf(' ', StringComparison.Ordinal);
            if (start >= 0)
            {
                return s.Substring(start + 1).Trim();
            }
            return null;
        }

        public bool IsAllowed(Uri url, string nonce, bool isInline = false, bool isEval = false, Uri origin = null, string elementHash = null, string elementTrustedType = null)
        {
            if (Sources.Contains("'none'")) return false;

            // 1. Hash Check (for inline scripts/styles)
            // If element has a hash attribute (integrity) that matches a hash in the policy, allow it
            if (!string.IsNullOrEmpty(elementHash))
            {
                if (Hashes.Contains(elementHash)) return true;
            }

            // 2. Nonce Check (Overrides inline checks if present)
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

            // 3. Inline Check
            if (isInline) 
            {
                // Hash-source check for inline content
                if (!string.IsNullOrEmpty(elementHash) && Hashes.Contains(elementHash)) return true;

                // Trusted-types check for inline scripts
                if (!string.IsNullOrEmpty(elementTrustedType) && TrustedTypes.Contains(elementTrustedType)) return true;

                // If 'unsafe-inline' is present, it's allowed UNLESS a nonce/hash is present in the policy (in modern CSP).
                // "If a directive contains a nonce-source or hash-source... 'unsafe-inline' is ignored."
                bool hasNoncesOrHashes = Nonces.Count > 0 || Hashes.Count > 0;
                if (!hasNoncesOrHashes && Sources.Contains("'unsafe-inline'")) return true;
                
                // If we have nonces/hashes, we only allow if the nonce/hash matched above.
                if (hasNoncesOrHashes) return false;

                return false;
            }

            // 3. Eval Check
            if (isEval) return Sources.Contains("'unsafe-eval'");
            
            // Trusted-types check for eval (createPolicy)
            if (isEval && !string.IsNullOrEmpty(elementTrustedType) && TrustedTypes.Contains(elementTrustedType)) return true;
            
            // 4. URL Check
            if (url == null) return false; 

            // strict-dynamic: if present, allow scripts loaded by trusted scripts
            // This is a simplified implementation - full strict-dynamic requires tracking script provenance
            if (HasStrictDynamic)
            {
                // In strict-dynamic mode, scripts loaded by trusted scripts are allowed
                // For now, we'll just log that strict-dynamic is active
                // Full implementation would track script chain provenance
            }

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
