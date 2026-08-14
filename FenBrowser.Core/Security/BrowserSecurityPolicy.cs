using System;
using System.Collections.Generic;
using System.Net;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// Central security-policy decisions for navigation, remote debugging, and network fetches.
    /// </summary>
    public static class BrowserSecurityPolicy
    {
        public static SecurityDecision EvaluateNetworkRequest(Uri uri)
        {
            var data = CreateUriData(uri);

            if (uri == null || !uri.IsAbsoluteUri)
            {
                return SecurityDecision.Deny(
                    "network-request",
                    "invalid-uri",
                    "Blocked network request because the URI is missing or invalid.",
                    data);
            }

            var scheme = uri.Scheme?.ToLowerInvariant();
            if (scheme == "http" || scheme == "https")
            {
                return SecurityDecision.Allow(
                    "network-request",
                    "allowed",
                    $"Network request allowed for scheme '{scheme}'.",
                    data);
            }

            return SecurityDecision.Deny(
                "network-request",
                "unsupported-scheme",
                $"Blocked network request for unsupported scheme '{scheme}'.",
                data);
        }

        public static SecurityDecision EvaluateTopLevelNavigation(
            Uri uri,
            bool isUserInput,
            bool automationContext,
            bool allowFileSchemeNavigation,
            bool allowAutomationFileNavigation)
        {
            var data = CreateUriData(uri);
            data["requestSource"] = isUserInput ? "user" : "programmatic";
            data["automationContext"] = automationContext;

            if (uri == null || !uri.IsAbsoluteUri)
            {
                return SecurityDecision.Deny(
                    "top-level-navigation",
                    "invalid-uri",
                    "Blocked navigation because the URI is missing or invalid.",
                    data);
            }

            var scheme = uri.Scheme?.ToLowerInvariant();
            switch (scheme)
            {
                case "http":
                case "https":
                case "about":
                case "data":
                case "fen":
                    return SecurityDecision.Allow(
                        "top-level-navigation",
                        "allowed",
                        $"Navigation allowed for scheme '{scheme}'.",
                        data);
                case "file":
                    if (!allowFileSchemeNavigation)
                    {
                        return SecurityDecision.Deny(
                            "top-level-navigation",
                            "file-navigation-disabled",
                            "Blocked file:// navigation because the browser policy disables local file navigation.",
                            data);
                    }

                    if (!isUserInput && automationContext && !allowAutomationFileNavigation)
                    {
                        return SecurityDecision.Deny(
                            "top-level-navigation",
                            "automation-file-navigation-disabled",
                            "Blocked file:// navigation because automation file navigation is disabled.",
                            data);
                    }

                    return SecurityDecision.Allow(
                        "top-level-navigation",
                        "file-navigation-allowed",
                        "Navigation allowed for file:// URI.",
                        data);
                default:
                    return SecurityDecision.Deny(
                        "top-level-navigation",
                        "unsupported-scheme",
                        $"Blocked navigation for unsupported scheme '{scheme}'.",
                        data);
            }
        }

        public static SecurityDecision EvaluateRemoteDebugBinding(
            IPAddress bindAddress,
            bool allowRemoteClients,
            bool hasAuthToken)
        {
            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["bindAddress"] = bindAddress?.ToString() ?? string.Empty,
                ["allowRemoteClients"] = allowRemoteClients,
                ["hasAuthToken"] = hasAuthToken
            };

            if (bindAddress == null)
            {
                return SecurityDecision.Deny(
                    "remote-debug-bind",
                    "invalid-address",
                    "Blocked remote debugging because the bind address is invalid.",
                    data);
            }

            if (IPAddress.IsLoopback(bindAddress))
            {
                return SecurityDecision.Allow(
                    "remote-debug-bind",
                    "loopback",
                    "Remote debugging bound to loopback.",
                    data);
            }

            if (!allowRemoteClients)
            {
                return SecurityDecision.Deny(
                    "remote-debug-bind",
                    "remote-bind-disabled",
                    "Blocked remote debugging on a non-loopback interface. Set FEN_REMOTE_DEBUG_ALLOW_REMOTE=1 to opt in explicitly.",
                    data);
            }

            if (!hasAuthToken)
            {
                return SecurityDecision.Deny(
                    "remote-debug-bind",
                    "missing-auth-token",
                    "Blocked remote debugging on a non-loopback interface because no authentication token is configured.",
                    data);
            }

            return SecurityDecision.Allow(
                "remote-debug-bind",
                "remote-bind-allowed",
                "Remote debugging allowed on a non-loopback interface.",
                data);
        }

        private static Dictionary<string, object> CreateUriData(Uri uri)
        {
            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["uri"] = GetSafeUriForTelemetry(uri),
                ["scheme"] = uri?.Scheme ?? string.Empty,
                ["host"] = uri?.IsAbsoluteUri == true ? uri.Host ?? string.Empty : string.Empty,
                ["port"] = uri?.IsAbsoluteUri == true && !uri.IsDefaultPort ? uri.Port : -1,
                ["hasQuery"] = uri?.IsAbsoluteUri == true && !string.IsNullOrEmpty(uri.Query),
                ["hasFragment"] = uri?.IsAbsoluteUri == true && !string.IsNullOrEmpty(uri.Fragment)
            };

            // Paths can themselves contain credentials, local filenames, signed object
            // names, or user identifiers. Record only their size for diagnostics.
            data["pathLength"] = uri?.IsAbsoluteUri == true
                ? Math.Min(uri.AbsolutePath?.Length ?? 0, 1_000_000)
                : 0;

            return data;
        }

        private static string GetSafeUriForTelemetry(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return string.Empty;
            }

            // Never log userinfo, query, fragment, data: payloads, local file paths,
            // or custom-scheme opaque content from a security-decision object.
            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return uri.GetLeftPart(UriPartial.Authority);
                }
                catch
                {
                    return uri.Scheme + ":";
                }
            }

            return uri.Scheme + ":";
        }
    }
}
