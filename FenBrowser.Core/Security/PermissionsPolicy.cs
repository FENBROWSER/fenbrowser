using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// Feature identifiers understood by the Permissions Policy engine
    /// (Permissions-Policy specification, W3C).
    /// </summary>
    public enum PolicyControlledFeature
    {
        Fullscreen = 0,
        Geolocation = 1,
        Camera = 2,
        Microphone = 3,
        Notifications = 4,
        Payment = 5,
        Autoplay = 6,
        ClipboardRead = 7,
        ClipboardWrite = 8,
        Usb = 9,
        Serial = 10,
        Battery = 11,
        Vibrate = 12,
        PictureInPicture = 13,
        ScreenWakeLock = 14,
        Gamepad = 15,
        Unknown = 16
    }

    /// <summary>
    /// One feature's allowlist: the set of origins granted the feature plus a
    /// flag for the self-origin and the special '*' (all origins) token.
    /// </summary>
    public sealed class FeatureAllowlist
    {
        public bool AllowsAll { get; set; }
        public bool AllowsSelf { get; set; }
        public HashSet<string> AllowedOrigins { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool Allows(string origin, string documentOrigin)
        {
            if (AllowsAll)
            {
                return true;
            }

            if (AllowsSelf && string.Equals(origin, documentOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrEmpty(origin) && AllowedOrigins.Contains(origin);
        }
    }

    /// <summary>
    /// Parsed Permissions-Policy (or legacy Feature-Policy) declaration.
    /// Tracks the default policy applied to documents and per-feature
    /// allowlists. Deny-by-default: a feature not mentioned is disabled unless
    /// the default allowlist grants it.
    /// </summary>
    public sealed class PermissionsPolicy
    {
        /// <summary>
        /// Per-feature allowlists parsed from the header.
        /// </summary>
        public Dictionary<PolicyControlledFeature, FeatureAllowlist> Allowlists { get; } =
            new Dictionary<PolicyControlledFeature, FeatureAllowlist>();

        /// <summary>
        /// Default allowlist for features not explicitly mentioned: '*' for
        /// secure-by-default features, 'self' otherwise. Mirrors the spec's
        /// "default allowlist" concept.
        /// </summary>
        public bool DefaultAllowsAll { get; set; }

        public string RawHeader { get; set; }

        public static readonly PermissionsPolicy None = new PermissionsPolicy();

        /// <summary>
        /// Parses a Permissions-Policy header value.
        /// Grammar: feature (space)* [= (space)* allowlist]? (comma feature...)*
        /// Example: "geolocation=(self), camera=(), fullscreen=*"
        /// </summary>
        public static PermissionsPolicy Parse(string headerValue)
        {
            var policy = new PermissionsPolicy();
            if (string.IsNullOrWhiteSpace(headerValue))
            {
                return policy;
            }

            policy.RawHeader = headerValue.Trim();
            var segments = SplitTopLevel(headerValue, ',');

            foreach (var segment in segments)
            {
                var trimmed = segment.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var parts = SplitTopLevel(trimmed, '=');
                string featureName = parts[0].Trim().ToLowerInvariant();
                if (featureName.Length == 0)
                {
                    continue;
                }

                var feature = ParseFeature(featureName);
                var allowlist = new FeatureAllowlist();

                if (parts.Count < 2)
                {
                    // No allowlist: the feature is disabled.
                    allowlist.AllowsSelf = false;
                    policy.Allowlists[feature] = allowlist;
                    continue;
                }

                string allowlistValue = parts[1].Trim();
                if (allowlistValue.Length == 0 || allowlistValue == "()")
                {
                    // Empty allowlist: disabled for everyone.
                    policy.Allowlists[feature] = allowlist;
                    continue;
                }

                if (allowlistValue == "*")
                {
                    allowlist.AllowsAll = true;
                    policy.Allowlists[feature] = allowlist;
                    continue;
                }

                if (allowlistValue == "self")
                {
                    allowlist.AllowsSelf = true;
                    policy.Allowlists[feature] = allowlist;
                    continue;
                }

                // Parenthesized origin list: (https://a.example https://b.example)
                string inner = allowlistValue;
                if (allowlistValue.StartsWith("(", StringComparison.Ordinal) &&
                    allowlistValue.EndsWith(")", StringComparison.Ordinal))
                {
                    inner = allowlistValue.Substring(1, allowlistValue.Length - 2);
                }

                var origins = inner.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var rawOrigin in origins)
                {
                    if (rawOrigin == "*")
                    {
                        allowlist.AllowsAll = true;
                    }
                    else if (rawOrigin == "self")
                    {
                        allowlist.AllowsSelf = true;
                    }
                    else
                    {
                        allowlist.AllowedOrigins.Add(NormalizeOrigin(rawOrigin));
                    }
                }

                policy.Allowlists[feature] = allowlist;
            }

            return policy;
        }

        /// <summary>
        /// Parses a legacy Feature-Policy header value. Uses the same declaration
        /// syntax but ';' separates declarations and the allowlist is written
        /// unparenthesized ("fullscreen *", "geolocation 'self' https://x").
        /// </summary>
        public static PermissionsPolicy ParseLegacyFeaturePolicy(string headerValue)
        {
            var policy = new PermissionsPolicy();
            if (string.IsNullOrWhiteSpace(headerValue))
            {
                return policy;
            }

            policy.RawHeader = headerValue.Trim();

            foreach (var segment in SplitTopLevel(headerValue, ';'))
            {
                var trimmed = segment.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                // "feature allowlist" — first token is the feature, the rest is
                // the allowlist. Features are separated by whitespace from their
                // allowlists (unlike Permissions-Policy's '=').
                var tokens = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                {
                    continue;
                }

                var feature = ParseFeature(tokens[0].Trim().ToLowerInvariant());
                var allowlist = new FeatureAllowlist();

                if (tokens.Length == 1)
                {
                    policy.Allowlists[feature] = allowlist;
                    continue;
                }

                for (int i = 1; i < tokens.Length; i++)
                {
                    var token = tokens[i].Trim();
                    if (token == "*")
                    {
                        allowlist.AllowsAll = true;
                    }
                    else if (token == "'self'" || token == "self")
                    {
                        allowlist.AllowsSelf = true;
                    }
                    else if (token == "src")
                    {
                        // Legacy 'src' keyword: allowed for any iframe source.
                        allowlist.AllowsAll = true;
                    }
                    else if (token.Length >= 3 &&
                             token[0] == '(' &&
                             token[token.Length - 1] == ')')
                    {
                        // Parenthesized single keyword, e.g. "(self)".
                        var inner = token.Substring(1, token.Length - 2).Trim();
                        if (inner == "*")
                        {
                            allowlist.AllowsAll = true;
                        }
                        else if (inner == "'self'" || inner == "self")
                        {
                            allowlist.AllowsSelf = true;
                        }
                        else if (inner == "src")
                        {
                            allowlist.AllowsAll = true;
                        }
                        else if (inner.Length > 0)
                        {
                            allowlist.AllowedOrigins.Add(NormalizeOrigin(inner));
                        }
                    }
                    else
                    {
                        allowlist.AllowedOrigins.Add(NormalizeOrigin(token));
                    }
                }

                policy.Allowlists[feature] = allowlist;
            }

            return policy;
        }

        /// <summary>
        /// Checks whether a feature is allowed for the given origin in a
        /// document served with this policy.
        /// </summary>
        public bool IsFeatureAllowed(PolicyControlledFeature feature, string origin, string documentOrigin)
        {
            if (Allowlists.TryGetValue(feature, out var allowlist))
            {
                return allowlist.Allows(origin, documentOrigin);
            }

            // Feature not mentioned: apply the default allowlist.
            return DefaultAllowsAll;
        }

        /// <summary>
        /// Enforces the header policy for a feature inside an iframe given the
        /// iframe's allow attribute allowlist (if any). A feature is allowed in
        /// the frame only when both the header policy and the frame allowlist
        /// permit the frame origin.
        /// </summary>
        public bool IsFeatureAllowedInFrame(
            PolicyControlledFeature feature,
            string frameOrigin,
            string documentOrigin,
            string iframeAllowAttribute)
        {
            bool headerAllows = IsFeatureAllowed(feature, frameOrigin, documentOrigin);
            if (!headerAllows)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(iframeAllowAttribute))
            {
                return false;
            }

            var frameAllowlist = ParseIframeAllowAttribute(iframeAllowAttribute);
            if (frameAllowlist.TryGetValue(feature, out var allow))
            {
                return allow;
            }

            return false;
        }

        /// <summary>
        /// Parses an iframe allow attribute (a permissions-policy-declaration
        /// list) into per-feature booleans. Unknown features are ignored.
        /// </summary>
        public static Dictionary<PolicyControlledFeature, bool> ParseIframeAllowAttribute(string allowAttribute)
        {
            var result = new Dictionary<PolicyControlledFeature, bool>();
            if (string.IsNullOrWhiteSpace(allowAttribute))
            {
                return result;
            }

            foreach (var segment in SplitTopLevel(allowAttribute, ';'))
            {
                var trimmed = segment.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var parts = SplitTopLevel(trimmed, '=');
                string featureName = parts[0].Trim().ToLowerInvariant();
                if (featureName.Length == 0)
                {
                    continue;
                }

                var feature = ParseFeature(featureName);
                if (feature == PolicyControlledFeature.Unknown)
                {
                    continue;
                }

                if (parts.Count < 2)
                {
                    result[feature] = true;
                    continue;
                }

                string value = parts[1].Trim().ToLowerInvariant();
                result[feature] = value != "none" && value != "0";
            }

            return result;
        }

        internal static List<string> SplitTopLevel(string value, char separator)
        {
            var parts = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    if (depth > 0)
                    {
                        depth--;
                    }
                }
                else if (ch == separator && depth == 0)
                {
                    parts.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }

            parts.Add(value.Substring(start));
            return parts;
        }

        private static PolicyControlledFeature ParseFeature(string name)
        {
            return name switch
            {
                "fullscreen" or "full-screen" => PolicyControlledFeature.Fullscreen,
                "geolocation" => PolicyControlledFeature.Geolocation,
                "camera" => PolicyControlledFeature.Camera,
                "microphone" or "mic" => PolicyControlledFeature.Microphone,
                "notifications" => PolicyControlledFeature.Notifications,
                "payment" or "paymentrequest" => PolicyControlledFeature.Payment,
                "autoplay" => PolicyControlledFeature.Autoplay,
                "clipboard-read" => PolicyControlledFeature.ClipboardRead,
                "clipboard-write" => PolicyControlledFeature.ClipboardWrite,
                "usb" => PolicyControlledFeature.Usb,
                "serial" => PolicyControlledFeature.Serial,
                "battery" => PolicyControlledFeature.Battery,
                "vibrate" => PolicyControlledFeature.Vibrate,
                "picture-in-picture" => PolicyControlledFeature.PictureInPicture,
                "screen-wake-lock" or "wake-lock" => PolicyControlledFeature.ScreenWakeLock,
                "gamepad" => PolicyControlledFeature.Gamepad,
                _ => PolicyControlledFeature.Unknown
            };
        }

        private static string NormalizeOrigin(string origin)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                return string.Empty;
            }

            origin = origin.Trim().TrimEnd('/');

            // Strip surrounding double quotes used in the header grammar:
            // (self "https://a.example")
            if (origin.Length >= 2 &&
                origin[0] == '"' &&
                origin[origin.Length - 1] == '"')
            {
                origin = origin.Substring(1, origin.Length - 2).Trim();
            }

            return origin;
        }
    }
}
