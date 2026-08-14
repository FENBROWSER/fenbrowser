// SpecRef: Content Security Policy Level 3, source-list enforcement
// CapabilityId: SECURITY-CSP-ENFORCEMENT-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FenBrowser.Core.Logging;

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
        /// CSP header names.
        /// </summary>
        public const string HeaderName = "Content-Security-Policy";
        public const string HeaderNameReportOnly = "Content-Security-Policy-Report-Only";
        
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

            var directive = ResolveEffectiveDirective(directiveName);
            if (directive == null) return true;

            return directive.IsAllowed(url, nonce, isInline, isEval, origin, elementHash, elementTrustedType);
        }

        private CspDirective ResolveEffectiveDirective(string directiveName)
        {
            if (string.IsNullOrWhiteSpace(directiveName))
            {
                return null;
            }

            if (Directives.TryGetValue(directiveName, out var direct))
            {
                return direct;
            }

            if (CspDirectiveNames.NoFallbackDirectives.Contains(directiveName))
            {
                return null;
            }

            // Fetch directive fallback chains are not uniformly "directive -> default-src".
            // Element/attribute script and style directives first inherit their family
            // directive; frame-src inherits child-src; worker-src inherits child-src and
            // script-src before reaching default-src.
            if (string.Equals(directiveName, CspDirectiveNames.ScriptSrcElem, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directiveName, CspDirectiveNames.ScriptSrcAttr, StringComparison.OrdinalIgnoreCase))
            {
                if (Directives.TryGetValue(CspDirectiveNames.ScriptSrc, out var script)) return script;
            }
            else if (string.Equals(directiveName, CspDirectiveNames.StyleSrcElem, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(directiveName, CspDirectiveNames.StyleSrcAttr, StringComparison.OrdinalIgnoreCase))
            {
                if (Directives.TryGetValue(CspDirectiveNames.StyleSrc, out var style)) return style;
            }
            else if (string.Equals(directiveName, CspDirectiveNames.FrameSrc, StringComparison.OrdinalIgnoreCase))
            {
                if (Directives.TryGetValue(CspDirectiveNames.ChildSrc, out var child)) return child;
            }
            else if (string.Equals(directiveName, CspDirectiveNames.WorkerSrc, StringComparison.OrdinalIgnoreCase))
            {
                if (Directives.TryGetValue(CspDirectiveNames.ChildSrc, out var child)) return child;
                if (Directives.TryGetValue(CspDirectiveNames.ScriptSrc, out var script)) return script;
            }

            return Directives.TryGetValue(CspDirectiveNames.DefaultSrc, out var fallback)
                ? fallback
                : null;
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

                // CSP uses ASCII whitespace between directive tokens. Splitting only
                // on a literal space let a tab turn the whole directive into an unknown
                // name and silently disable enforcement for that directive.
                var tokens = trimmed.Split(
                    new[] { ' ', '\t', '\r', '\n', '\f' },
                    StringSplitOptions.RemoveEmptyEntries);
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
            return Directives.ContainsKey(CspDirectiveNames.UpgradeInsecureRequests);
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

            // report-uri/report-to are directives, not source expressions. Preserve
            // their first configured endpoint/group directly so report-only policy
            // lookup does not depend on source-token parsing that can never match.
            if (string.Equals(name, CspDirectiveNames.ReportUri, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var source in sources)
                {
                    Sources.Add(source);
                }
                ReportUri = sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                return;
            }

            if (string.Equals(name, CspDirectiveNames.ReportTo, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var source in sources)
                {
                    Sources.Add(source);
                }
                ReportTo = sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                return;
            }

            if (string.Equals(name, CspDirectiveNames.TrustedTypes, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var source in sources)
                {
                    Sources.Add(source);
                    if (!source.StartsWith("'", StringComparison.Ordinal))
                    {
                        TrustedTypes.Add(source);
                    }
                }
                return;
            }

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
                    var val = s.Substring(14, s.Length - 15);
                    TrustedTypes.Add(val);
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
            // Kept for compatibility with older serialized directive tokens.
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
            if (!string.IsNullOrEmpty(elementHash))
            {
                if (Hashes.Contains(elementHash)) return true;
            }

            // 2. Nonce Check
            if (!string.IsNullOrEmpty(nonce))
            {
                if (Nonces.Contains(nonce)) return true;
            }

            // 3. Inline Check
            if (isInline) 
            {
                if (!string.IsNullOrEmpty(elementHash) && Hashes.Contains(elementHash)) return true;
                if (!string.IsNullOrEmpty(elementTrustedType) && TrustedTypes.Contains(elementTrustedType)) return true;

                bool hasNoncesOrHashes = Nonces.Count > 0 || Hashes.Count > 0;
                if (!hasNoncesOrHashes && Sources.Contains("'unsafe-inline'")) return true;
                if (hasNoncesOrHashes) return false;

                return false;
            }

            // 4. Eval Check. Keep the entire decision in one branch so future
            // Trusted Types integration cannot be accidentally placed after an
            // unconditional return and become unreachable.
            if (isEval)
            {
                if (!string.IsNullOrEmpty(elementTrustedType) && TrustedTypes.Contains(elementTrustedType))
                {
                    return true;
                }
                return Sources.Contains("'unsafe-eval'");
            }
            
            // 5. URL Check
            if (url == null) return false; 

            // strict-dynamic requires script provenance tracking. Do not pretend the
            // keyword itself authorizes a URL; nonce/hash checks above remain enforced
            // while URL source matching continues for compatibility until provenance
            // is represented by the caller.

            if (Sources.Contains("*")) return true;

            // Check 'self'
            if (Sources.Contains("'self'") && origin != null)
            {
                if (IsSameOrigin(url, origin)) return true;
            }

            foreach (var src in Sources)
            {
                if (src == "*") return true;
                if (src.StartsWith("'", StringComparison.Ordinal)) continue; // keywords
                if (MatchesSourceExpression(src, url)) return true;
            }

            return false;
        }

        private static bool MatchesSourceExpression(string source, Uri url)
        {
            if (string.IsNullOrWhiteSpace(source) || url == null || !url.IsAbsoluteUri)
            {
                return false;
            }

            var src = source.Trim();

            // scheme-source, e.g. https:, data:, blob:. It has no host/port/path.
            if (src.EndsWith(":", StringComparison.Ordinal) &&
                src.IndexOf('/') < 0 &&
                src.IndexOf("[", StringComparison.Ordinal) < 0)
            {
                return string.Equals(
                    url.Scheme,
                    src.Substring(0, src.Length - 1),
                    StringComparison.OrdinalIgnoreCase);
            }

            string srcHost = src;
            string srcScheme = null;
            string srcPort = null;
            string srcPath = null;
            bool explicitScheme = false;

            var schemeSeparator = src.IndexOf("://", StringComparison.Ordinal);
            if (schemeSeparator >= 0)
            {
                explicitScheme = true;
                srcScheme = src.Substring(0, schemeSeparator);
                srcHost = src.Substring(schemeSeparator + 3);
                if (string.IsNullOrWhiteSpace(srcScheme)) return false;
            }

            var slash = srcHost.IndexOf('/');
            if (slash >= 0)
            {
                srcPath = srcHost.Substring(slash);
                srcHost = srcHost.Substring(0, slash);
            }

            if (string.IsNullOrWhiteSpace(srcHost)) return false;

            if (srcHost.StartsWith("[", StringComparison.Ordinal))
            {
                var closeBracket = srcHost.IndexOf(']');
                if (closeBracket <= 0) return false;

                if (closeBracket + 1 < srcHost.Length)
                {
                    if (srcHost[closeBracket + 1] != ':') return false;
                    srcPort = srcHost.Substring(closeBracket + 2);
                }
                srcHost = srcHost.Substring(0, closeBracket + 1);
            }
            else
            {
                var portIdx = srcHost.LastIndexOf(':');
                if (portIdx >= 0)
                {
                    srcPort = srcHost.Substring(portIdx + 1);
                    srcHost = srcHost.Substring(0, portIdx);
                }
            }

            if (string.IsNullOrWhiteSpace(srcHost)) return false;

            if (explicitScheme && !string.Equals(url.Scheme, srcScheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!PortMatches(url, srcScheme, srcPort, explicitScheme))
            {
                return false;
            }

            var urlHost = url.Host.TrimEnd('.');
            if (srcHost == "*")
            {
                // Host wildcard still obeys any explicit scheme/port/path restrictions.
            }
            else if (srcHost.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = srcHost.Substring(2).TrimEnd('.');
                if (string.IsNullOrEmpty(suffix) ||
                    !urlHost.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else if (!string.Equals(urlHost, srcHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return PathMatches(url, srcPath);
        }

        private static bool PortMatches(Uri url, string sourceScheme, string sourcePort, bool explicitScheme)
        {
            var urlPort = url.IsDefaultPort ? GetDefaultPort(url.Scheme) : url.Port;

            if (!string.IsNullOrEmpty(sourcePort))
            {
                if (sourcePort == "*") return true;
                return int.TryParse(sourcePort, out var parsedPort) &&
                       parsedPort >= 0 && parsedPort <= 65535 &&
                       parsedPort == urlPort;
            }

            // A scheme-qualified host source with no explicit port is constrained to
            // that scheme's default port. Preserve existing host-only behavior where
            // the protected resource's scheme is not available to this directive.
            if (!explicitScheme) return true;

            var expectedPort = GetDefaultPort(sourceScheme);
            return expectedPort < 0 || urlPort == expectedPort;
        }

        private static bool PathMatches(Uri url, string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return true;

            var requestPath = string.IsNullOrEmpty(url.AbsolutePath) ? "/" : url.AbsolutePath;
            if (sourcePath.EndsWith("/", StringComparison.Ordinal))
            {
                return requestPath.StartsWith(sourcePath, StringComparison.Ordinal);
            }

            return string.Equals(requestPath, sourcePath, StringComparison.Ordinal);
        }

        private static bool IsSameOrigin(Uri url, Uri origin)
        {
            if (url == null || origin == null) return false;
            if (!string.Equals(url.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(url.Host, origin.Host, StringComparison.OrdinalIgnoreCase)) return false;
            int urlPort = url.IsDefaultPort ? GetDefaultPort(url.Scheme) : url.Port;
            int originPort = origin.IsDefaultPort ? GetDefaultPort(origin.Scheme) : origin.Port;
            return urlPort == originPort;
        }

        private static int GetDefaultPort(string scheme)
        {
            return scheme?.ToLowerInvariant() switch
            {
                "http" => 80,
                "https" => 443,
                "ftp" => 21,
                "ws" => 80,
                "wss" => 443,
                _ => -1
            };
        }
    }

    /// <summary>
    /// Represents a CSP policy in report-only mode.
    /// Report-only policies do not block violations but report them to the specified endpoint.
    /// </summary>
    public sealed class CspPolicyReportOnly
    {
        public CspPolicy Policy { get; }
        public string HeaderValue { get; }

        private CspPolicyReportOnly(CspPolicy policy, string headerValue)
        {
            Policy = policy;
            HeaderValue = headerValue;
        }

        /// <summary>
        /// Parses a Content-Security-Policy-Report-Only header value.
        /// </summary>
        public static CspPolicyReportOnly Parse(string headerValue)
        {
            var policy = CspPolicy.Parse(headerValue);
            return new CspPolicyReportOnly(policy, headerValue);
        }

        /// <summary>
        /// Checks if a request would violate this report-only policy.
        /// Returns true if allowed (would not violate), false if would violate.
        /// </summary>
        public bool WouldAllow(string directiveName, Uri url, string nonce = null, Uri origin = null, 
            bool isInline = false, bool isEval = false, string elementHash = null, string elementTrustedType = null)
        {
            return Policy.IsAllowed(
                directiveName,
                url,
                nonce,
                origin,
                isInline,
                isEval,
                elementHash,
                elementTrustedType);
        }

        /// <summary>
        /// Reports a violation to the configured report endpoint.
        /// </summary>
        public void ReportViolation(CspViolationReport report)
        {
            if (Policy == null) return;
            
            var reportUri = GetReportUri();
            if (string.IsNullOrEmpty(reportUri)) return;

            // Transport of reports is owned by the browser networking layer. Until a
            // report dispatcher is wired, retain a security diagnostic rather than
            // performing ad-hoc network I/O from the policy object.
            EngineLogCompat.Warn($"[CSP Report-Only] Violation: {report}", FenBrowser.Core.Logging.LogCategory.Security);
        }

        private string GetReportUri()
        {
            if (Policy.Directives.TryGetValue(CspDirectiveNames.ReportTo, out var reportToDirective) &&
                !string.IsNullOrWhiteSpace(reportToDirective.ReportTo))
            {
                return reportToDirective.ReportTo;
            }
            
            if (Policy.Directives.TryGetValue(CspDirectiveNames.ReportUri, out var reportUriDirective) &&
                !string.IsNullOrWhiteSpace(reportUriDirective.ReportUri))
            {
                return reportUriDirective.ReportUri;
            }

            return null;
        }
    }

    /// <summary>
    /// Represents a CSP violation report.
    /// </summary>
    public sealed class CspViolationReport
    {
        public string DocumentUri { get; set; }
        public string Referrer { get; set; }
        public string ViolatedDirective { get; set; }
        public string EffectiveDirective { get; set; }
        public string OriginalPolicy { get; set; }
        public string BlockedUri { get; set; }
        public int LineNumber { get; set; }
        public int ColumnNumber { get; set; }
        public string SourceFile { get; set; }
        public string Sample { get; set; }
        public long TimeStamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
