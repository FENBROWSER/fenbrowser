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
        /// Per HTML spec §7.2.3.1, only certain properties are accessible cross-origin:
        /// window.location (setter only), window.close, window.closed, window.focus, window.blur,
        /// window.frames, window.length, window.top, window.opener, window.parent,
        /// window.postMessage, window.self, window.window
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
        /// Check if accessing a cross-origin Location property is allowed.
        /// Per spec, only Location.href (setter) and Location.replace are allowed cross-origin.
        /// </summary>
        public static bool IsCrossOriginLocationPropertyAllowed(string propertyName)
        {
            return propertyName == "href" || propertyName == "replace";
        }

        /// <summary>
        /// Verify same-origin access between two documents.
        /// Throws SecurityError if cross-origin access is not allowed.
        /// </summary>
        public static void EnforceSameOrigin(Document accessor, Document target, string operation)
        {
            if (accessor == null || target == null) return;
            if (accessor.Origin == null || target.Origin == null) return;

            if (!accessor.Origin.IsSameOrigin(target.Origin))
            {
                FenLogger.Warn(
                    $"[Security] Blocked cross-origin access: {accessor.Origin} → {target.Origin} ({operation})",
                    LogCategory.Errors);
                throw new DomException(
                    "SecurityError",
                    $"Blocked a frame with origin \"{accessor.Origin}\" from accessing a cross-origin frame.");
            }
        }

        /// <summary>
        /// Check if a script source is allowed by CSP.
        /// Returns true if no CSP policy is active or the source is allowed.
        /// </summary>
        public static bool IsScriptAllowedByCsp(CspPolicy csp, Uri scriptUri, Uri documentOrigin,
            bool isInline = false, bool isEval = false, string nonce = null)
        {
            if (csp == null) return true;
            return csp.IsAllowed("script-src", scriptUri, nonce, documentOrigin, isInline, isEval);
        }

        /// <summary>
        /// Check if eval() / new Function() is allowed by CSP.
        /// </summary>
        public static bool IsEvalAllowedByCsp(CspPolicy csp, Uri documentOrigin)
        {
            if (csp == null) return true;
            return csp.IsAllowed("script-src", url: null, origin: documentOrigin, isEval: true);
        }

        /// <summary>
        /// Check if an inline script/event handler is allowed by CSP.
        /// </summary>
        public static bool IsInlineScriptAllowedByCsp(CspPolicy csp, Uri documentOrigin, string nonce = null)
        {
            if (csp == null) return true;
            return csp.IsAllowed("script-src", url: null, nonce: nonce, origin: documentOrigin, isInline: true);
        }

        /// <summary>
        /// Validate the origin parameter for postMessage.
        /// Returns true if the message should be delivered to the target.
        /// </summary>
        public static bool ValidatePostMessageOrigin(string targetOrigin, Origin actualOrigin)
        {
            if (targetOrigin == "*") return true;
            if (targetOrigin == "/") return true; // Same origin shorthand

            try
            {
                var targetUri = new Uri(targetOrigin);
                var expected = Origin.FromUri(targetUri);
                return actualOrigin.IsSameOrigin(expected);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if a cookie should be sent with a request based on SameSite attribute.
        /// </summary>
        public static bool ShouldSendCookie(string sameSiteAttribute, bool isSameOriginRequest,
            bool isTopLevelNavigation, string requestMethod)
        {
            switch (sameSiteAttribute?.ToLowerInvariant())
            {
                case "strict":
                    return isSameOriginRequest;
                case "lax":
                    return isSameOriginRequest ||
                           (isTopLevelNavigation && IsSafeMethod(requestMethod));
                case "none":
                    return true; // Requires Secure flag in modern browsers
                default:
                    // Default to "Lax" per modern browser behavior
                    return isSameOriginRequest ||
                           (isTopLevelNavigation && IsSafeMethod(requestMethod));
            }
        }

        private static bool IsSafeMethod(string method) =>
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Validate CORS preflight response.
        /// Returns true if the actual request should proceed.
        /// </summary>
        public static bool ValidateCorsPreflight(
            string allowedOrigin, string allowedMethods, string allowedHeaders,
            Origin requestOrigin, string requestMethod, string[] requestHeaders)
        {
            // Check origin
            if (allowedOrigin != "*")
            {
                if (!string.Equals(allowedOrigin, requestOrigin?.ToString(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Check method
            if (!string.IsNullOrEmpty(requestMethod) && !string.IsNullOrEmpty(allowedMethods))
            {
                var methods = allowedMethods.Split(',', StringSplitOptions.TrimEntries);
                bool methodOk = false;
                foreach (var m in methods)
                {
                    if (string.Equals(m, requestMethod, StringComparison.OrdinalIgnoreCase) || m == "*")
                    {
                        methodOk = true;
                        break;
                    }
                }
                if (!methodOk) return false;
            }

            // Check headers
            if (requestHeaders != null && requestHeaders.Length > 0 && !string.IsNullOrEmpty(allowedHeaders))
            {
                var allowed = new HashSet<string>(
                    allowedHeaders.Split(',', StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
                if (!allowed.Contains("*"))
                {
                    foreach (var h in requestHeaders)
                    {
                        if (!IsCorsSimpleHeader(h) && !allowed.Contains(h))
                            return false;
                    }
                }
            }

            return true;
        }

        /// <summary>CORS-safelisted request headers that don't need preflight.</summary>
        private static bool IsCorsSimpleHeader(string header) => header?.ToLowerInvariant() switch
        {
            "accept" or "accept-language" or "content-language" => true,
            "content-type" => true, // Additional value check would be needed
            _ => false
        };

        /// <summary>Check if a request method is a CORS simple method.</summary>
        public static bool IsCorsSimpleMethod(string method) => method?.ToUpperInvariant() switch
        {
            "GET" or "HEAD" or "POST" => true,
            _ => false
        };
    }
}
