// =============================================================================
// CapabilityGuard.cs
// WebDriver Security - Capability Enforcement
//
// PURPOSE: Enforces capability restrictions at real browser/driver boundaries.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver.Security
{
    /// <summary>
    /// Enforces security restrictions based on session capabilities.
    /// </summary>
    public class CapabilityGuard
    {
        private readonly Session _session;
        private static readonly HashSet<string> RiskyCapabilityArgs = new(StringComparer.OrdinalIgnoreCase)
        {
            "--allow-file-access",
            "--allow-insecure-localhost",
            "--disable-web-security"
        };
        private const string RiskyCapabilityOptIn = "--webdriver-allow-risky-capabilities";

        public CapabilityGuard(Session session)
        {
            _session = session;
        }

        /// <summary>
        /// Check if insecure certificates are allowed.
        /// </summary>
        public bool AllowInsecureCerts => _session.Capabilities.AcceptInsecureCerts == true;

        /// <summary>
        /// Check if a URL is allowed for navigation.
        /// </summary>
        public bool IsUrlAllowed(string url)
        {
            return EvaluateUrlPolicy(url).Allowed;
        }

        public SecurityDecision EvaluateUrlPolicy(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return SecurityDecision.Block(SecurityBlockReasons.NavigationUrlInvalid, "Navigation URL is empty");
            }

            // Block file:// URLs by default for security
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (!AllowFileUrls())
                {
                    return SecurityDecision.Block(
                        SecurityBlockReasons.NavigationUrlBlocked,
                        "file:// navigation requires explicit risky capability opt-in");
                }

                return SecurityDecision.Allow();
            }

            // Block javascript: URLs as top-level navigation in this driver surface.
            if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                return SecurityDecision.Block(SecurityBlockReasons.NavigationUrlBlocked, "javascript: navigation is blocked");
            }

            // Keep the existing explicit data:-navigation restriction separate from
            // Execute Script. Script execution itself is a WebDriver command and must
            // not be authorized by scanning JavaScript source text for substrings.
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (url.Contains("script", StringComparison.OrdinalIgnoreCase))
                {
                    return SecurityDecision.Block(SecurityBlockReasons.NavigationUrlBlocked, "data: navigation containing script is blocked");
                }

                return SecurityDecision.Allow();
            }

            return SecurityDecision.Allow();
        }

        private bool AllowFileUrls()
        {
            var fenOptions = _session.Capabilities.FenOptions;
            if (fenOptions?.Args != null)
            {
                foreach (var arg in fenOptions.Args)
                {
                    if (arg == "--allow-file-access")
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// WebDriver Execute Script accepts a JavaScript function body. Security must
        /// be enforced by the browsing-context sandbox/capabilities, not by substring
        /// matching source text such as "process.exit" or "eval(atob(".
        /// </summary>
        public bool IsScriptAllowed(string script)
        {
            return EvaluateScriptPolicy(script).Allowed;
        }

        public SecurityDecision EvaluateScriptPolicy(string script)
        {
            if (script == null)
            {
                return SecurityDecision.Block(SecurityBlockReasons.ScriptBlocked, "Script source is null");
            }

            return SecurityDecision.Allow();
        }

        public int GetScriptTimeout()
        {
            return ToRuntimeTimeoutMs(_session.Timeouts.Script, 30000);
        }

        public int GetPageLoadTimeout()
        {
            return ToRuntimeTimeoutMs(_session.Timeouts.PageLoad, 300000);
        }

        private static int ToRuntimeTimeoutMs(long? configuredTimeoutMs, int fallbackMs)
        {
            var value = configuredTimeoutMs ?? fallbackMs;
            if (value <= 0)
            {
                return fallbackMs;
            }

            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        public static SecurityDecision ValidateRequestedCapabilities(Capabilities? caps)
        {
            // The current default WebDriver server installs NoOpBiDiTransportBootstrap,
            // while the concrete transport is a separately-owned listener whose
            // endpoint/lifecycle is not yet integrated with the HTTP remote end.
            // Never turn `webSocketUrl: true` into an advertised URL until the server
            // can actually start and own a standards-compliant BiDi listener.
            if (Capabilities.NormalizeWebSocketUrl(caps?.WebSocketUrl) is bool webSocketRequested && webSocketRequested)
            {
                return SecurityDecision.Block(
                    SecurityBlockReasons.CapabilityPolicyViolation,
                    "webSocketUrl/BiDi is unavailable until the WebDriver remote end owns a standards-compliant BiDi transport");
            }

            var args = caps?.FenOptions?.Args ?? new List<string>();
            if (args.Count == 0)
            {
                return SecurityDecision.Allow();
            }

            var hasRiskyOptIn = args.Any(arg => string.Equals(arg, RiskyCapabilityOptIn, StringComparison.OrdinalIgnoreCase));
            var riskyArgs = args.Where(arg => RiskyCapabilityArgs.Contains(arg)).ToArray();
            if (riskyArgs.Length == 0)
            {
                return SecurityDecision.Allow();
            }

            if (!hasRiskyOptIn)
            {
                return SecurityDecision.Block(
                    SecurityBlockReasons.CapabilityPolicyViolation,
                    $"Risky capability arguments require explicit opt-in {RiskyCapabilityOptIn}: {string.Join(", ", riskyArgs)}");
            }

            return SecurityDecision.Allow();
        }
    }

    public readonly struct SecurityDecision
    {
        public bool Allowed { get; }
        public string ReasonCode { get; }
        public string Detail { get; }

        private SecurityDecision(bool allowed, string reasonCode, string detail)
        {
            Allowed = allowed;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public static SecurityDecision Allow() => new(true, string.Empty, string.Empty);

        public static SecurityDecision Block(string reasonCode, string detail) => new(false, reasonCode, detail);
    }
}
