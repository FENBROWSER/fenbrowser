using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// Feature identifiers understood by the Permissions Policy engine.
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
        Unknown = 16,
        EncryptedMedia = 17,
        SpeakerSelection = 18
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
                return true;

            var normalizedOrigin = PermissionsPolicy.NormalizeOrigin(origin);
            if (normalizedOrigin.Length == 0)
                return false;

            if (AllowsSelf)
            {
                var normalizedDocumentOrigin = PermissionsPolicy.NormalizeOrigin(documentOrigin);
                if (normalizedDocumentOrigin.Length > 0 &&
                    string.Equals(normalizedOrigin, normalizedDocumentOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return AllowedOrigins.Contains(normalizedOrigin);
        }
    }

    /// <summary>
    /// Parsed Permissions-Policy (or legacy Feature-Policy) declaration.
    /// </summary>
    public sealed class PermissionsPolicy
    {
        public Dictionary<PolicyControlledFeature, FeatureAllowlist> Allowlists { get; } =
            new Dictionary<PolicyControlledFeature, FeatureAllowlist>();

        public bool DefaultAllowsAll { get; set; }
        public string RawHeader { get; set; }

        // Never expose a process-wide mutable policy instance. Callers historically
        // treated None as an empty sentinel, but the policy object itself is mutable.
        // Returning a fresh empty policy prevents one document from mutating the
        // effective baseline of every later document.
        public static PermissionsPolicy None => new PermissionsPolicy();

        public static PermissionsPolicy Parse(string headerValue)
        {
            var policy = new PermissionsPolicy();
            if (string.IsNullOrWhiteSpace(headerValue))
                return policy;

            policy.RawHeader = headerValue.Trim();
            foreach (var segment in SplitTopLevel(headerValue, ','))
            {
                var trimmed = segment.Trim();
                if (trimmed.Length == 0)
                    continue;

                var parts = SplitTopLevel(trimmed, '=');
                if (parts.Count > 2)
                    continue;

                string featureName = parts[0].Trim().ToLowerInvariant();
                var feature = ParseFeature(featureName);
                if (feature == PolicyControlledFeature.Unknown)
                    continue;

                var allowlist = new FeatureAllowlist();

                // A feature named with nothing after it has an empty allowlist: the header
                // mentions the feature, and mentioning it with no origins takes it away.
                if (parts.Count == 1)
                {
                    policy.Allowlists[feature] = allowlist;
                    continue;
                }

                string allowlistValue = parts[1].Trim();

                if (allowlistValue.Length == 0 || allowlistValue == "()")
                {
                    policy.Allowlists[feature] = allowlist;
                    continue;
                }

                if (allowlistValue == "*")
                {
                    allowlist.AllowsAll = true;
                    policy.Allowlists[feature] = allowlist;
                    continue;
                }

                string inner = allowlistValue;
                if (allowlistValue.StartsWith("(", StringComparison.Ordinal) &&
                    allowlistValue.EndsWith(")", StringComparison.Ordinal))
                {
                    inner = allowlistValue.Substring(1, allowlistValue.Length - 2);
                }
                else if (allowlistValue.StartsWith("(", StringComparison.Ordinal) ||
                         allowlistValue.EndsWith(")", StringComparison.Ordinal))
                {
                    // Unbalanced allowlist syntax is an invalid directive.
                    continue;
                }

                foreach (var rawToken in SplitAllowlistTokens(inner))
                {
                    var token = StripQuotes(rawToken);
                    if (token == "*")
                    {
                        allowlist.AllowsAll = true;
                    }
                    else if (string.Equals(token, "self", StringComparison.OrdinalIgnoreCase))
                    {
                        allowlist.AllowsSelf = true;
                    }
                    else
                    {
                        var normalized = NormalizeOrigin(token);
                        if (normalized.Length > 0)
                            allowlist.AllowedOrigins.Add(normalized);
                    }
                }

                policy.Allowlists[feature] = allowlist;
            }

            return policy;
        }

        public static PermissionsPolicy ParseLegacyFeaturePolicy(string headerValue)
        {
            var policy = new PermissionsPolicy();
            if (string.IsNullOrWhiteSpace(headerValue))
                return policy;

            policy.RawHeader = headerValue.Trim();

            foreach (var segment in SplitTopLevel(headerValue, ';'))
            {
                var trimmed = segment.Trim();
                if (trimmed.Length == 0)
                    continue;

                var tokens = trimmed.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;

                var feature = ParseFeature(tokens[0].Trim().ToLowerInvariant());
                if (feature == PolicyControlledFeature.Unknown)
                    continue;

                var allowlist = new FeatureAllowlist();
                for (int i = 1; i < tokens.Length; i++)
                {
                    var token = StripQuotes(tokens[i]);
                    if (token == "*")
                    {
                        allowlist.AllowsAll = true;
                    }
                    else if (string.Equals(token, "self", StringComparison.OrdinalIgnoreCase))
                    {
                        allowlist.AllowsSelf = true;
                    }
                    else if (string.Equals(token, "src", StringComparison.OrdinalIgnoreCase))
                    {
                        // Feature-Policy's src keyword is source-origin scoped. This
                        // header-only parser has no iframe source origin, so treating
                        // it as '*' is a privilege escalation. Leave the directive
                        // empty and let origin-aware iframe evaluation decide it.
                        continue;
                    }
                    else
                    {
                        if (token.Length >= 2 && token[0] == '(' && token[token.Length - 1] == ')')
                            token = StripQuotes(token.Substring(1, token.Length - 2).Trim());

                        var normalized = NormalizeOrigin(token);
                        if (normalized.Length > 0)
                            allowlist.AllowedOrigins.Add(normalized);
                    }
                }

                policy.Allowlists[feature] = allowlist;
            }

            return policy;
        }

        public bool IsFeatureAllowed(PolicyControlledFeature feature, string origin, string documentOrigin)
        {
            if (Allowlists.TryGetValue(feature, out var allowlist))
                return allowlist.Allows(origin, documentOrigin);

            // A policy that says nothing about a feature leaves that feature's own default
            // allowlist in force. Falling back to DefaultAllowsAll instead meant one
            // Permissions-Policy header took away every feature it did not mention, even
            // from the document that sent it.
            return DefaultAllowsAll || DefaultAllowlistAllows(feature, origin, documentOrigin);
        }

        /// <summary>
        /// The default allowlist each policy-controlled feature carries when no policy
        /// names it: "*" for the two features whose specifications say so, and "self" for
        /// the rest, which is what every one of these features defaults to.
        /// </summary>
        /// <summary>Whether the feature's default allowlist is "*" rather than "self".</summary>
        public static bool DefaultAllowlistIsAll(PolicyControlledFeature feature) =>
            feature is PolicyControlledFeature.PictureInPicture or PolicyControlledFeature.Gamepad;

        public static bool DefaultAllowlistAllows(PolicyControlledFeature feature, string origin, string documentOrigin)
        {
            if (DefaultAllowlistIsAll(feature))
                return true;

            if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(documentOrigin))
                return true; // nothing to compare; an unknown origin is the document's own

            return string.Equals(origin.TrimEnd('/'), documentOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        public bool IsFeatureAllowedInFrame(
            PolicyControlledFeature feature,
            string frameOrigin,
            string documentOrigin,
            string iframeAllowAttribute)
        {
            // The iframe allow attribute is an additional restriction. Its absence
            // does not mean "deny every feature"; the inherited/header policy remains
            // authoritative. The old behavior broke ordinary iframes that relied on
            // the parent policy/default allowlist without an explicit allow= value.
            if (!IsFeatureAllowed(feature, frameOrigin, documentOrigin))
                return false;

            if (string.IsNullOrWhiteSpace(iframeAllowAttribute))
                return true;

            return IsIframeFeatureAllowed(
                iframeAllowAttribute,
                feature,
                frameOrigin,
                documentOrigin);
        }

        /// <summary>
        /// Permissions Policy §9.7 steps 3-4 for a container: whether the iframe's allow
        /// attribute enables <paramref name="feature"/> for a document at
        /// <paramref name="frameOrigin"/> embedded by one at <paramref name="parentOrigin"/>,
        /// or null when the attribute does not name the feature and its default allowlist
        /// decides instead.
        /// </summary>
        public static bool? EvaluateContainerAllow(
            string allowAttribute,
            PolicyControlledFeature feature,
            string frameOrigin,
            string parentOrigin)
        {
            if (!ParseIframeAllowAttribute(allowAttribute).ContainsKey(feature))
                return null;

            return IsIframeFeatureAllowed(allowAttribute, feature, frameOrigin, parentOrigin);
        }

        /// <summary>
        /// Parses iframe allow= directives into a compatibility boolean map.
        /// This method reports whether each directive has a non-empty allowlist;
        /// IsFeatureAllowedInFrame performs the origin-aware check.
        /// </summary>
        public static Dictionary<PolicyControlledFeature, bool> ParseIframeAllowAttribute(string allowAttribute)
        {
            var result = new Dictionary<PolicyControlledFeature, bool>();
            if (string.IsNullOrWhiteSpace(allowAttribute))
                return result;

            foreach (var segment in SplitTopLevel(allowAttribute, ';'))
            {
                if (!TryParseIframeDirective(segment, out var feature, out var tokens))
                    continue;

                bool denied = ContainsNoneToken(tokens);
                result[feature] = !denied;
            }

            return result;
        }

        private static bool IsIframeFeatureAllowed(
            string allowAttribute,
            PolicyControlledFeature requestedFeature,
            string frameOrigin,
            string documentOrigin)
        {
            var normalizedFrameOrigin = NormalizeOrigin(frameOrigin);
            var normalizedDocumentOrigin = NormalizeOrigin(documentOrigin);

            foreach (var segment in SplitTopLevel(allowAttribute, ';'))
            {
                if (!TryParseIframeDirective(segment, out var feature, out var tokens) ||
                    feature != requestedFeature)
                {
                    continue;
                }

                // A bare feature name uses the iframe source origin as its allowlist.
                if (tokens.Count == 0)
                    return normalizedFrameOrigin.Length > 0;

                if (ContainsNoneToken(tokens))
                    return false;

                foreach (var rawToken in tokens)
                {
                    var token = StripQuotes(rawToken);
                    if (token == "*")
                        return true;

                    if (string.Equals(token, "src", StringComparison.OrdinalIgnoreCase))
                        return normalizedFrameOrigin.Length > 0;

                    if (string.Equals(token, "self", StringComparison.OrdinalIgnoreCase))
                    {
                        if (normalizedFrameOrigin.Length > 0 &&
                            normalizedDocumentOrigin.Length > 0 &&
                            string.Equals(normalizedFrameOrigin, normalizedDocumentOrigin, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                        continue;
                    }

                    var allowedOrigin = NormalizeOrigin(token);
                    if (allowedOrigin.Length > 0 &&
                        string.Equals(allowedOrigin, normalizedFrameOrigin, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            return false;
        }

        private static bool TryParseIframeDirective(
            string segment,
            out PolicyControlledFeature feature,
            out List<string> allowlistTokens)
        {
            feature = PolicyControlledFeature.Unknown;
            allowlistTokens = new List<string>();

            var trimmed = segment?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return false;

            int separatorIndex = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];
                if (ch == '=' || char.IsWhiteSpace(ch))
                {
                    separatorIndex = i;
                    break;
                }
            }

            string featureName;
            string allowlistText;
            if (separatorIndex < 0)
            {
                featureName = trimmed;
                allowlistText = string.Empty;
            }
            else
            {
                featureName = trimmed.Substring(0, separatorIndex).Trim();
                int valueStart = separatorIndex;
                while (valueStart < trimmed.Length &&
                       (trimmed[valueStart] == '=' || char.IsWhiteSpace(trimmed[valueStart])))
                {
                    valueStart++;
                }
                allowlistText = valueStart < trimmed.Length ? trimmed.Substring(valueStart).Trim() : string.Empty;
            }

            feature = ParseFeature(featureName.ToLowerInvariant());
            if (feature == PolicyControlledFeature.Unknown)
                return false;

            if (allowlistText.StartsWith("(", StringComparison.Ordinal) &&
                allowlistText.EndsWith(")", StringComparison.Ordinal))
            {
                allowlistText = allowlistText.Substring(1, allowlistText.Length - 2).Trim();
            }

            allowlistTokens.AddRange(SplitAllowlistTokens(allowlistText));
            return true;
        }

        private static bool ContainsNoneToken(List<string> tokens)
        {
            foreach (var rawToken in tokens)
            {
                if (string.Equals(StripQuotes(rawToken), "none", StringComparison.OrdinalIgnoreCase) ||
                    rawToken == "0")
                {
                    return true;
                }
            }
            return false;
        }

        private static string[] SplitAllowlistTokens(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries);
        }

        internal static List<string> SplitTopLevel(string value, char separator)
        {
            var parts = new List<string>();
            if (value == null)
                return parts;

            int depth = 0;
            int start = 0;
            bool inQuote = false;
            char quote = '\0';

            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (inQuote)
                {
                    if (ch == '\\' && i + 1 < value.Length)
                    {
                        i++;
                        continue;
                    }
                    if (ch == quote)
                    {
                        inQuote = false;
                        quote = '\0';
                    }
                    continue;
                }

                if (ch is '\'' or '"')
                {
                    inQuote = true;
                    quote = ch;
                }
                else if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    if (depth > 0)
                        depth--;
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
                "encrypted-media" => PolicyControlledFeature.EncryptedMedia,
                "speaker-selection" => PolicyControlledFeature.SpeakerSelection,
                _ => PolicyControlledFeature.Unknown
            };
        }

        private static string StripQuotes(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[value.Length - 1] == '"') ||
                 (value[0] == '\'' && value[value.Length - 1] == '\'')))
            {
                value = value.Substring(1, value.Length - 2).Trim();
            }
            return value;
        }

        public static string NormalizeOrigin(string origin)
        {
            origin = StripQuotes(origin);
            if (origin.Length == 0)
                return string.Empty;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                string.IsNullOrEmpty(uri.Scheme) ||
                string.IsNullOrEmpty(uri.Host))
            {
                return string.Empty;
            }

            var scheme = uri.Scheme.ToLowerInvariant();
            var host = uri.IdnHost.ToLowerInvariant();
            if (host.IndexOf(':') >= 0 &&
                !host.StartsWith("[", StringComparison.Ordinal) &&
                !host.EndsWith("]", StringComparison.Ordinal))
            {
                host = "[" + host + "]";
            }

            bool omitPort = uri.IsDefaultPort || uri.Port <= 0;
            return omitPort
                ? $"{scheme}://{host}"
                : $"{scheme}://{host}:{uri.Port}";
        }
    }
}
