using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// Centralized security checks for Same-Origin Policy enforcement.
    /// All cross-origin boundary checks should go through this class.
    /// </summary>
    public static class SecurityChecks
    {
        /// <summary>
        /// Check if accessing a cross-origin Window property is allowed.
        /// Only the cross-origin Window surface is listed here; individual objects
        /// such as Location still need their own operation-level checks.
        /// </summary>
        public static bool IsCrossOriginWindowPropertyAllowed(string propertyName)
        {
            switch (propertyName)
            {
                case "location": case "close": case "closed": case "focus": case "blur":
                case "frames": case "length": case "top": case "opener": case "parent":
                case "postMessage": case "self": case "window":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Check if accessing a cross-origin Location member is potentially allowed.
        /// Callers must still distinguish the href setter from the href getter.
        /// </summary>
        public static bool IsCrossOriginLocationPropertyAllowed(string propertyName)
        {
            return propertyName == "href" || propertyName == "replace";
        }

        /// <summary>
        /// Verify same-origin access between two documents.
        /// Missing origin metadata must never turn into permission to cross a document
        /// boundary: an uninitialized/opaque origin is a security principal, not a wildcard.
        /// </summary>
        public static void EnforceSameOrigin(Document accessor, Document target, string operation)
        {
            if (accessor == null)
            {
                throw new ArgumentNullException(nameof(accessor));
            }
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (accessor.Origin == null || target.Origin == null)
            {
                EngineLogCompat.Warn(
                    $"[Security] Blocked document access with missing origin metadata ({operation})",
                    LogCategory.Errors);
                throw new DomException(
                    "SecurityError",
                    "Blocked cross-document access because an origin could not be established.");
            }

            if (!accessor.Origin.IsSameOrigin(target.Origin))
            {
                EngineLogCompat.Warn(
                    $"[Security] Blocked cross-origin access: {accessor.Origin} → {target.Origin} ({operation})",
                    LogCategory.Errors);
                throw new DomException(
                    "SecurityError",
                    $"Blocked a frame with origin \"{accessor.Origin}\" from accessing a cross-origin frame.");
            }
        }

        public static bool IsScriptAllowedByCsp(
            CspPolicy csp,
            Uri scriptUri,
            Uri documentOrigin,
            bool isInline = false,
            bool isEval = false,
            string nonce = null,
            string elementHash = null,
            string elementTrustedType = null)
        {
            if (csp == null) return true;
            return csp.IsAllowed(
                "script-src",
                scriptUri,
                nonce,
                documentOrigin,
                isInline,
                isEval,
                elementHash,
                elementTrustedType);
        }

        public static bool IsEvalAllowedByCsp(CspPolicy csp, Uri documentOrigin, string elementTrustedType = null)
        {
            if (csp == null) return true;
            return csp.IsAllowed(
                "script-src",
                url: null,
                nonce: null,
                origin: documentOrigin,
                isEval: true,
                elementTrustedType: elementTrustedType);
        }

        public static bool IsInlineScriptAllowedByCsp(
            CspPolicy csp,
            Uri documentOrigin,
            string nonce = null,
            string elementHash = null,
            string elementTrustedType = null)
        {
            if (csp == null) return true;
            return csp.IsAllowed(
                "script-src",
                url: null,
                nonce: nonce,
                origin: documentOrigin,
                isInline: true,
                elementHash: elementHash,
                elementTrustedType: elementTrustedType);
        }

        /// <summary>
        /// Validate the targetOrigin argument for postMessage.
        /// The two-argument overload cannot prove the '/' same-origin shorthand and
        /// therefore fails closed for that form. Call the three-argument overload when
        /// the incumbent/source origin is available.
        /// </summary>
        public static bool ValidatePostMessageOrigin(string targetOrigin, Origin actualOrigin)
            => ValidatePostMessageOrigin(targetOrigin, actualOrigin, sourceOrigin: null);

        /// <summary>
        /// Validate targetOrigin against the actual target and the incumbent/source
        /// origin. '/' means the target must be same-origin with the source; it is not
        /// an alias for '*'.
        /// </summary>
        public static bool ValidatePostMessageOrigin(
            string targetOrigin,
            Origin actualOrigin,
            Origin sourceOrigin)
        {
            if (actualOrigin == null || string.IsNullOrWhiteSpace(targetOrigin))
            {
                return false;
            }

            if (targetOrigin == "*")
            {
                return true;
            }

            if (targetOrigin == "/")
            {
                return sourceOrigin != null && actualOrigin.IsSameOrigin(sourceOrigin);
            }

            if (!Uri.TryCreate(targetOrigin, UriKind.Absolute, out var targetUri))
            {
                return false;
            }

            var expected = Origin.FromUri(targetUri);
            return !expected.IsOpaque && actualOrigin.IsSameOrigin(expected);
        }

        /// <summary>
        /// Check if a cookie should be sent based on SameSite context. The caller is
        /// responsible for enforcing the cookie's Secure attribute before using a
        /// SameSite=None result.
        /// </summary>
        public static bool ShouldSendCookie(
            string sameSiteAttribute,
            bool isSameOriginRequest,
            bool isTopLevelNavigation,
            string requestMethod)
        {
            switch (sameSiteAttribute?.ToLowerInvariant())
            {
                case "strict":
                    return isSameOriginRequest;
                case "lax":
                    return isSameOriginRequest ||
                           (isTopLevelNavigation && IsSafeMethod(requestMethod));
                case "none":
                    return true;
                default:
                    return isSameOriginRequest ||
                           (isTopLevelNavigation && IsSafeMethod(requestMethod));
            }
        }

        private static bool IsSafeMethod(string method) =>
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Validate the response fields needed by a CORS preflight. This helper is
        /// intentionally conservative when it lacks enough information to prove that a
        /// request header is CORS-safelisted.
        /// </summary>
        public static bool ValidateCorsPreflight(
            string allowedOrigin,
            string allowedMethods,
            string allowedHeaders,
            Origin requestOrigin,
            string requestMethod,
            string[] requestHeaders)
        {
            if (requestOrigin == null || requestOrigin.IsOpaque)
            {
                return false;
            }

            if (allowedOrigin != "*")
            {
                if (string.IsNullOrWhiteSpace(allowedOrigin) ||
                    !string.Equals(allowedOrigin.Trim(), requestOrigin.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(requestMethod) && !string.IsNullOrWhiteSpace(allowedMethods))
            {
                var methods = allowedMethods.Split(
                    ',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var methodOk = false;
                foreach (var method in methods)
                {
                    if (string.Equals(method, requestMethod, StringComparison.OrdinalIgnoreCase) || method == "*")
                    {
                        methodOk = true;
                        break;
                    }
                }

                if (!methodOk)
                {
                    return false;
                }
            }

            if (requestHeaders != null && requestHeaders.Length > 0)
            {
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(allowedHeaders))
                {
                    foreach (var header in allowedHeaders.Split(
                                 ',',
                                 StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        allowed.Add(header);
                    }
                }

                var wildcard = allowed.Contains("*");
                foreach (var rawHeader in requestHeaders)
                {
                    var header = rawHeader?.Trim();
                    if (string.IsNullOrEmpty(header))
                    {
                        continue;
                    }

                    // content-type is only safelisted for a narrow MIME set, and this
                    // API receives no header value. It therefore cannot safely waive
                    // preflight authorization merely from the name "content-type".
                    if (!IsCorsSafelistedHeaderNameWithoutValueCheck(header) &&
                        !wildcard &&
                        !allowed.Contains(header))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Header names that can be recognized as safelisted without inspecting a
        /// MIME value. Content-Type deliberately is not in this helper.
        /// </summary>
        private static bool IsCorsSafelistedHeaderNameWithoutValueCheck(string header)
            => header?.ToLowerInvariant() switch
            {
                "accept" or "accept-language" or "content-language" => true,
                _ => false
            };

        public static bool IsCorsSimpleMethod(string method) => method?.ToUpperInvariant() switch
        {
            "GET" or "HEAD" or "POST" => true,
            _ => false
        };
    }
}
