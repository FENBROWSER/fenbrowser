using System;
using System.Collections.Generic;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// Mixed Content blocking per W3C Mixed Content specification.
    /// Blocks active mixed content (scripts, iframes, stylesheets, etc.) on secure pages.
    /// Optionally upgrades passive mixed content (images, video, audio).
    /// </summary>
    public static class MixedContentChecker
    {
        // Active mixed content types - always blocked on HTTPS pages
        private static readonly HashSet<string> ActiveMixedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "script",
            "iframe",
            "frame",
            "object",
            "embed",
            "applet",
            "link",        // stylesheets
            "audio",
            "video",
            "source",
            "track",
            "worker",
            "sharedworker",
            "serviceworker",
            "manifest",
            "xmlhttprequest",
            "fetch",
            "websocket",
            "eventsource",
            "navigation"   // top-level navigation
        };

        // Passive mixed content types - can be upgraded with Upgrade-Insecure-Requests
        private static readonly HashSet<string> PassiveMixedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image",
            "img",
            "picture",
            "audio",
            "video",
            "source"
        };

        /// <summary>
        /// Checks if a request constitutes mixed content on a secure page.
        /// </summary>
        /// <param name="requestUrl">The URL being requested</param>
        /// <param name="pageUrl">The URL of the page making the request (must be HTTPS)</param>
        /// <param name="requestType">The type of request (e.g., "script", "image", "style", "fetch")</param>
        /// <param name="isUpgradeInsecureRequestsEnabled">Whether Upgrade-Insecure-Requests CSP directive is active</param>
        /// <returns>MixedContentDecision indicating whether to block, upgrade, or allow</returns>
        public static MixedContentDecision CheckMixedContent(
            Uri requestUrl,
            Uri pageUrl,
            string requestType,
            bool isUpgradeInsecureRequestsEnabled = false)
        {
            // No mixed content if page is not secure
            if (pageUrl == null || !string.Equals(pageUrl.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return MixedContentDecision.Allow();
            }

            // No mixed content if request is also secure
            if (requestUrl != null && string.Equals(requestUrl.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return MixedContentDecision.Allow();
            }

            // Request is HTTP on HTTPS page = mixed content
            if (requestUrl == null || !string.Equals(requestUrl.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                // Not HTTP/HTTPS (data:, blob:, etc.) - allow
                return MixedContentDecision.Allow();
            }

            // Determine if this is active or passive mixed content
            bool isActive = ActiveMixedContentTypes.Contains(requestType);
            bool isPassive = PassiveMixedContentTypes.Contains(requestType);

            // If neither active nor passive, treat as active for safety
            if (!isActive && !isPassive)
            {
                isActive = true;
            }

            if (isActive)
            {
                // Active mixed content is ALWAYS blocked
                return MixedContentDecision.Block(
                    $"Active mixed content blocked: {requestType} from {requestUrl} on secure page {pageUrl}",
                    "active-mixed-content-blocked");
            }
            else // isPassive
            {
                if (isUpgradeInsecureRequestsEnabled)
                {
                    // Upgrade passive mixed content to HTTPS
                    var upgradedUrl = UpgradeToHttps(requestUrl);
                    return MixedContentDecision.Upgrade(
                        $"Passive mixed content upgraded to HTTPS: {requestType} from {requestUrl} -> {upgradedUrl}",
                        "passive-mixed-content-upgraded",
                        upgradedUrl);
                }
                else
                {
                    // Allow passive mixed content but log warning
                    EngineLogCompat.Warn(
                        $"[MixedContent] Passive mixed content allowed: {requestType} from {requestUrl} on secure page {pageUrl}",
                        LogCategory.Security);
                    return MixedContentDecision.Allow();
                }
            }
        }

        /// <summary>
        /// Checks if a CSP header contains the upgrade-insecure-requests directive.
        /// </summary>
        public static bool HasUpgradeInsecureRequestsDirective(string cspHeader)
        {
            if (string.IsNullOrWhiteSpace(cspHeader)) return false;
            
            var directives = cspHeader.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in directives)
            {
                var trimmed = dir.Trim();
                if (trimmed.Equals("upgrade-insecure-requests", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                // Also check if it's part of a directive value
                if (trimmed.StartsWith("upgrade-insecure-requests", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Upgrades an HTTP URL to HTTPS.
        /// </summary>
        private static Uri UpgradeToHttps(Uri httpUrl)
        {
            if (httpUrl == null) return null;
            
            var builder = new UriBuilder(httpUrl)
            {
                Scheme = "https",
                Port = -1 // Use default port (443)
            };
            return builder.Uri;
        }

        /// <summary>
        /// Checks if a navigation request should be blocked due to mixed content.
        /// </summary>
        public static MixedContentDecision CheckNavigationMixedContent(
            Uri navigationUrl,
            Uri pageUrl,
            bool isUpgradeInsecureRequestsEnabled = false)
        {
            return CheckMixedContent(navigationUrl, pageUrl, "navigation", isUpgradeInsecureRequestsEnabled);
        }
    }

    /// <summary>
    /// Result of a mixed content check.
    /// </summary>
    public sealed class MixedContentDecision
    {
        public MixedContentAction Action { get; }
        public string Reason { get; }
        public string Code { get; }
        public Uri UpgradedUrl { get; }

        private MixedContentDecision(MixedContentAction action, string reason, string code, Uri upgradedUrl = null)
        {
            Action = action;
            Reason = reason ?? string.Empty;
            Code = code ?? string.Empty;
            UpgradedUrl = upgradedUrl;
        }

        public static MixedContentDecision Allow() => new(MixedContentAction.Allow, string.Empty, string.Empty);
        
        public static MixedContentDecision Block(string reason, string code) => new(MixedContentAction.Block, reason, code);
        
        public static MixedContentDecision Upgrade(string reason, string code, Uri upgradedUrl) => new(MixedContentAction.Upgrade, reason, code, upgradedUrl);

        public bool IsBlocked => Action == MixedContentAction.Block;
        public bool IsUpgraded => Action == MixedContentAction.Upgrade;
        public bool IsAllowed => Action == MixedContentAction.Allow;
    }

    public enum MixedContentAction
    {
        Allow,
        Block,
        Upgrade
    }
}