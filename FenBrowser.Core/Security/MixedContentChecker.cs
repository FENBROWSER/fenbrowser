using System;
using System.Collections.Generic;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// Mixed Content blocking and upgrade decisions for requests initiated by secure pages.
    /// </summary>
    public static class MixedContentChecker
    {
        private static readonly HashSet<string> BlockableMixedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "script",
            "iframe",
            "frame",
            "object",
            "embed",
            "applet",
            "link",
            "style",
            "worker",
            "sharedworker",
            "serviceworker",
            "manifest",
            "xmlhttprequest",
            "fetch",
            "websocket",
            "eventsource"
        };

        private static readonly HashSet<string> UpgradeableMixedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image",
            "img",
            "picture",
            "audio",
            "video",
            "source"
        };

        /// <summary>
        /// Checks an insecure request initiated by a secure page.
        /// upgrade-insecure-requests is applied before the mixed-content decision.
        /// </summary>
        public static MixedContentDecision CheckMixedContent(
            Uri requestUrl,
            Uri pageUrl,
            string requestType,
            bool isUpgradeInsecureRequestsEnabled = false)
        {
            if (pageUrl == null || !string.Equals(pageUrl.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return MixedContentDecision.Allow();
            }

            if (requestUrl == null)
            {
                return MixedContentDecision.Allow();
            }

            if (string.Equals(requestUrl.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(requestUrl.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            {
                return MixedContentDecision.Allow();
            }

            var isHttp = string.Equals(requestUrl.Scheme, "http", StringComparison.OrdinalIgnoreCase);
            var isWs = string.Equals(requestUrl.Scheme, "ws", StringComparison.OrdinalIgnoreCase);
            if (!isHttp && !isWs)
            {
                return MixedContentDecision.Allow();
            }

            // UIR rewrites non-navigation insecure resource requests before mixed-content
            // blocking. A rewritten request is then evaluated as the secure URL.
            if (isUpgradeInsecureRequestsEnabled &&
                !string.Equals(requestType, "navigation", StringComparison.OrdinalIgnoreCase))
            {
                var upgradedUrl = UpgradeToSecureTransport(requestUrl);
                return MixedContentDecision.Upgrade(
                    $"Insecure request upgraded before mixed-content checks: {requestType} from {requestUrl} -> {upgradedUrl}",
                    "upgrade-insecure-requests",
                    upgradedUrl);
            }

            // Top-level navigations are not mixed-content subresource requests. UIR may
            // upgrade navigations back to the protected resource's own host/port tuple;
            // third-party HTTP navigations remain allowed rather than being blocked as
            // mixed content.
            if (string.Equals(requestType, "navigation", StringComparison.OrdinalIgnoreCase))
            {
                if (isUpgradeInsecureRequestsEnabled && IsSameHostAndEffectivePort(requestUrl, pageUrl))
                {
                    var upgradedUrl = UpgradeToSecureTransport(requestUrl);
                    return MixedContentDecision.Upgrade(
                        $"Insecure navigation upgraded: {requestUrl} -> {upgradedUrl}",
                        "upgrade-insecure-navigation",
                        upgradedUrl);
                }

                return MixedContentDecision.Allow();
            }

            var isBlockable = BlockableMixedContentTypes.Contains(requestType);
            var isUpgradeable = UpgradeableMixedContentTypes.Contains(requestType);

            if (!isBlockable && !isUpgradeable)
            {
                isBlockable = true;
            }

            if (isUpgradeable)
            {
                var upgradedUrl = UpgradeToSecureTransport(requestUrl);
                return MixedContentDecision.Upgrade(
                    $"Upgradeable mixed content rewritten to secure transport: {requestType} from {requestUrl} -> {upgradedUrl}",
                    "upgradeable-mixed-content",
                    upgradedUrl);
            }

            return MixedContentDecision.Block(
                $"Blockable mixed content blocked: {requestType} from {requestUrl} on secure page {pageUrl}",
                "blockable-mixed-content");
        }

        /// <summary>
        /// Checks if a CSP header contains the value-less upgrade-insecure-requests directive.
        /// </summary>
        public static bool HasUpgradeInsecureRequestsDirective(string cspHeader)
        {
            if (string.IsNullOrWhiteSpace(cspHeader)) return false;

            var directives = cspHeader.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var directive in directives)
            {
                if (directive.Trim().Equals("upgrade-insecure-requests", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static Uri UpgradeToSecureTransport(Uri insecureUrl)
        {
            if (insecureUrl == null) return null;

            var sourceScheme = insecureUrl.Scheme;
            var targetScheme = string.Equals(sourceScheme, "ws", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "https";

            var preservePort = !insecureUrl.IsDefaultPort && insecureUrl.Port != 80;
            var originalPort = insecureUrl.Port;
            var builder = new UriBuilder(insecureUrl)
            {
                Scheme = targetScheme,
                Port = preservePort ? originalPort : -1
            };
            return builder.Uri;
        }

        private static bool IsSameHostAndEffectivePort(Uri requestUrl, Uri pageUrl)
        {
            if (requestUrl == null || pageUrl == null)
            {
                return false;
            }

            if (!string.Equals(requestUrl.Host, pageUrl.Host, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var requestPort = requestUrl.IsDefaultPort ? 80 : requestUrl.Port;
            var pagePort = pageUrl.IsDefaultPort ? 80 : pageUrl.Port;
            return requestPort == pagePort;
        }

        public static MixedContentDecision CheckNavigationMixedContent(
            Uri navigationUrl,
            Uri pageUrl,
            bool isUpgradeInsecureRequestsEnabled = false)
        {
            return CheckMixedContent(navigationUrl, pageUrl, "navigation", isUpgradeInsecureRequestsEnabled);
        }
    }

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
