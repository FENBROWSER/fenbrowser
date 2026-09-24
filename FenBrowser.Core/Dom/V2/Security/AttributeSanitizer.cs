// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2.Security - Optional Attribute Sanitization

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FenBrowser.Core.Accessibility;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Dom.V2.Security
{
    /// <summary>
    /// Attribute validation plus optional hardened-content sanitization.
    /// Normal browser DOM mutation preserves attribute values; execution and
    /// navigation policy is enforced by the subsystem that consumes each value.
    /// </summary>
    public static class AttributeSanitizer
    {
        // --- Configuration ---

        /// <summary>
        /// Enables additional hardened-content checks. This must not alter ordinary
        /// web-platform DOM semantics by itself.
        /// </summary>
        public static bool StrictMode { get; set; } = true;

        /// <summary>
        /// Explicit opt-in for content-sanitizer behavior. Defaults to false because
        /// Element.setAttribute() is a DOM primitive, not an HTML sanitizer.
        /// </summary>
        public static bool SanitizePotentiallyActiveContent { get; set; } = false;

        /// <summary>
        /// Set to true to log blocked attributes for debugging.
        /// </summary>
        public static bool LogBlocked { get; set; } = false;

        /// <summary>
        /// Set to true to clear inline event-handler values in hardened-content mode.
        /// </summary>
        public static bool BlockInlineEventHandlersInStrictMode { get; set; } = false;

        /// <summary>
        /// Set to true to clear srcdoc values in hardened-content mode.
        /// </summary>
        public static bool BlockSrcdocInStrictMode { get; set; } = false;

        private static readonly HashSet<string> DangerousSchemes = new(StringComparer.OrdinalIgnoreCase)
        {
            "javascript",
            "vbscript",
            "data",
            "file",
        };

        // If optional content sanitization is enabled, keep the data URL allowlist
        // non-active. SVG and JavaScript MIME types are intentionally excluded.
        private static readonly HashSet<string> AllowedDataMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/png",
            "image/jpeg",
            "image/gif",
            "image/webp",
            "text/plain",
        };

        private static readonly HashSet<string> EventHandlerAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "onabort", "onblur", "oncancel", "oncanplay", "oncanplaythrough",
            "onchange", "onclick", "onclose", "oncontextmenu", "oncopy",
            "oncuechange", "oncut", "ondblclick", "ondrag", "ondragend",
            "ondragenter", "ondragleave", "ondragover", "ondragstart", "ondrop",
            "ondurationchange", "onemptied", "onended", "onerror", "onfocus",
            "onfocusin", "onfocusout", "onformdata", "ongotpointercapture",
            "oninput", "oninvalid", "onkeydown", "onkeypress", "onkeyup",
            "onload", "onloadeddata", "onloadedmetadata", "onloadstart",
            "onlostpointercapture", "onmousedown", "onmouseenter", "onmouseleave",
            "onmousemove", "onmouseout", "onmouseover", "onmouseup", "onmousewheel",
            "onpaste", "onpause", "onplay", "onplaying", "onpointercancel",
            "onpointerdown", "onpointerenter", "onpointerleave", "onpointermove",
            "onpointerout", "onpointerover", "onpointerup", "onprogress",
            "onratechange", "onreset", "onresize", "onscroll", "onsecuritypolicyviolation",
            "onseeked", "onseeking", "onselect", "onselectionchange", "onselectstart",
            "onslotchange", "onstalled", "onsubmit", "onsuspend", "ontimeupdate",
            "ontoggle", "ontouchcancel", "ontouchend", "ontouchmove", "ontouchstart",
            "ontransitioncancel", "ontransitionend", "ontransitionrun", "ontransitionstart",
            "onvolumechange", "onwaiting", "onwebkitanimationend", "onwebkitanimationiteration",
            "onwebkitanimationstart", "onwebkittransitionend", "onwheel",
            "onbeforeunload", "onhashchange", "onlanguagechange", "onmessage",
            "onmessageerror", "onoffline", "ononline", "onpagehide", "onpageshow",
            "onpopstate", "onrejectionhandled", "onstorage", "onunhandledrejection",
            "onunload"
        };

        private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "href", "src", "action", "formaction", "data", "poster",
            "cite", "background", "codebase", "dynsrc", "lowsrc",
            "usemap", "longdesc", "profile", "xmlns", "xlink:href"
        };

        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

        private static readonly Regex JavaScriptPattern = new(
            @"javascript\s*:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            RegexTimeout);

        private static readonly Regex VbScriptPattern = new(
            @"vbscript\s*:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            RegexTimeout);

        private static readonly Regex ExpressionPattern = new(
            @"expression\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            RegexTimeout);

        private static readonly Regex DataUriPattern = new(
            @"^data:([^;,]+)?(;[^,]+)*,",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            RegexTimeout);

        private static readonly Regex StyleUrlPattern = new(
            @"url\s*\(\s*['""]?([^'""\)]+)['""]?\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            RegexTimeout);

        public static AttributeValidationResult ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return AttributeValidationResult.Invalid("Attribute name cannot be empty");

            // DOM 4.9 setAttribute: a valid attribute local name.
            if (!DomNames.IsValidAttributeLocalName(name))
                return AttributeValidationResult.Invalid($"Invalid characters in attribute name: {name}");

            // Event-handler names are valid DOM attributes. Mark them as noteworthy
            // without treating the name itself as invalid.
            if (IsEventHandler(name))
                return AttributeValidationResult.Sanitize($"Event handler attribute: {name}");

            return AttributeValidationResult.Valid();
        }

        public static AttributeValidationResult ValidateValue(
            string name, string value, out string sanitizedValue)
        {
            sanitizedValue = value;

            if (string.IsNullOrEmpty(value))
                return AttributeValidationResult.Valid();

            // DOM mutation must preserve authored values. Optional sanitization is for
            // explicitly hardened/untrusted-content embedding modes, never the normal
            // browser path. CSP, navigation, script and fetch policy remain responsible
            // for deciding whether a preserved value may execute or load.
            if (!SanitizePotentiallyActiveContent)
            {
                ValidateAriaForDiagnostics(name, value);
                return AttributeValidationResult.Valid();
            }

            if (StrictMode && BlockInlineEventHandlersInStrictMode && IsEventHandler(name))
            {
                if (LogBlocked)
                    LogBlockedDecision(name, value, "event-handler-blocked");

                sanitizedValue = "";
                return AttributeValidationResult.Sanitize("Event handler values blocked in hardened-content mode");
            }

            if (IsUrlAttribute(name))
            {
                var urlResult = ValidateUrl(value, out var sanitizedUrl);
                sanitizedValue = sanitizedUrl;
                return urlResult;
            }

            if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                var styleResult = ValidateStyleAttribute(value, out var sanitizedStyle);
                sanitizedValue = sanitizedStyle;
                return styleResult;
            }

            if (name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase) &&
                StrictMode && BlockSrcdocInStrictMode)
            {
                sanitizedValue = "";
                return AttributeValidationResult.Sanitize("srcdoc blocked in hardened-content mode");
            }

            ValidateAriaForDiagnostics(name, value);
            return AttributeValidationResult.Valid();
        }

        public static bool IsEventHandler(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return EventHandlerAttributes.Contains(name) ||
                   name.StartsWith("on", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsUrlAttribute(string name)
        {
            return !string.IsNullOrEmpty(name) && UrlAttributes.Contains(name);
        }

        private static void ValidateAriaForDiagnostics(string name, string value)
        {
            if (string.IsNullOrEmpty(name) ||
                !name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase) ||
                AriaSpec.IsValidPropertyValue(name, value) ||
                !LogBlocked)
            {
                return;
            }

            EngineLogCompat.Warn(
                $"[AttributeSanitizer] Invalid ARIA value treated as missing. name={SanitizeForLog(name, 128)}, value={SanitizeForLog(value, 512)}",
                LogCategory.Accessibility);
        }

        private static bool IsValidXmlName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            char first = name[0];
            if (!char.IsLetter(first) && first != '_' && first != ':')
                return false;

            for (int i = 1; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != ':' && c != '.')
                    return false;
            }

            return true;
        }

        private static AttributeValidationResult ValidateUrl(string url, out string sanitizedUrl)
        {
            sanitizedUrl = url;

            if (string.IsNullOrWhiteSpace(url))
                return AttributeValidationResult.Valid();

            var trimmed = url.Trim();

            try
            {
                if (JavaScriptPattern.IsMatch(trimmed))
                {
                    if (LogBlocked)
                        LogBlockedDecision(null, trimmed, "javascript-url");
                    sanitizedUrl = "";
                    return AttributeValidationResult.Sanitize("javascript: URLs are blocked");
                }

                if (VbScriptPattern.IsMatch(trimmed))
                {
                    if (LogBlocked)
                        LogBlockedDecision(null, trimmed, "vbscript-url");
                    sanitizedUrl = "";
                    return AttributeValidationResult.Sanitize("vbscript: URLs are blocked");
                }

                var dataMatch = DataUriPattern.Match(trimmed);
                if (dataMatch.Success)
                {
                    var mimeType = dataMatch.Groups[1].Value;
                    if (!AllowedDataMimeTypes.Contains(mimeType))
                    {
                        if (LogBlocked)
                            LogBlockedDecision(null, trimmed, $"data-url-mime-blocked:{mimeType}");
                        sanitizedUrl = "";
                        return AttributeValidationResult.Sanitize($"data: URLs with mime type '{mimeType}' are blocked");
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                sanitizedUrl = "";
                return AttributeValidationResult.Sanitize("Attribute URL exceeded sanitizer complexity budget");
            }

            int colonIndex = trimmed.IndexOf(':');
            if (colonIndex > 0 && colonIndex < 20)
            {
                var scheme = trimmed.Substring(0, colonIndex).Trim();
                if (DangerousSchemes.Contains(scheme) && !scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
                {
                    sanitizedUrl = "";
                    return AttributeValidationResult.Sanitize($"{scheme}: URLs are blocked");
                }
            }

            return AttributeValidationResult.Valid();
        }

        private static AttributeValidationResult ValidateStyleAttribute(string style, out string sanitizedStyle)
        {
            sanitizedStyle = style;

            if (string.IsNullOrWhiteSpace(style))
                return AttributeValidationResult.Valid();

            try
            {
                if (JavaScriptPattern.IsMatch(style))
                {
                    sanitizedStyle = "";
                    return AttributeValidationResult.Sanitize("javascript: in style attribute blocked");
                }

                if (ExpressionPattern.IsMatch(style))
                {
                    sanitizedStyle = "";
                    return AttributeValidationResult.Sanitize("CSS expression() blocked");
                }

                if (style.Contains("url(", StringComparison.OrdinalIgnoreCase))
                {
                    var urlResult = SanitizeStyleUrls(style, out var sanitizedStyleUrls);
                    sanitizedStyle = sanitizedStyleUrls;
                    return urlResult;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                sanitizedStyle = "";
                return AttributeValidationResult.Sanitize("Style attribute exceeded sanitizer complexity budget");
            }

            return AttributeValidationResult.Valid();
        }

        private static AttributeValidationResult SanitizeStyleUrls(string style, out string sanitizedStyle)
        {
            sanitizedStyle = style;
            bool modified = false;

            var matches = StyleUrlPattern.Matches(style);
            foreach (Match match in matches)
            {
                var url = match.Groups[1].Value;
                var urlResult = ValidateUrl(url, out _);
                if (!urlResult.IsValid || urlResult.WasSanitized)
                {
                    sanitizedStyle = sanitizedStyle.Replace(match.Value, "none", StringComparison.Ordinal);
                    modified = true;
                }
            }

            return modified
                ? AttributeValidationResult.Sanitize("Dangerous URLs in style removed")
                : AttributeValidationResult.Valid();
        }

        private static void LogBlockedDecision(string attributeName, string value, string reason)
        {
            EngineLogCompat.Warn(
                $"[AttributeSanitizer] Blocked or sanitized attribute content. reason={SanitizeForLog(reason, 128)}, attribute={SanitizeForLog(attributeName ?? "(n/a)", 128)}, value={SanitizeForLog(value, 512)}",
                LogCategory.Security);
        }

        private static string SanitizeForLog(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var length = Math.Min(value.Length, maxLength);
            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = char.IsControl(value[i]) ? ' ' : value[i];
            }

            return new string(chars) + (value.Length > maxLength ? "…" : string.Empty);
        }
    }

    public readonly struct AttributeValidationResult
    {
        public bool IsValid { get; }
        public bool WasSanitized { get; }
        public string Message { get; }

        private AttributeValidationResult(bool isValid, bool wasSanitized, string message)
        {
            IsValid = isValid;
            WasSanitized = wasSanitized;
            Message = message;
        }

        public static AttributeValidationResult Valid() => new(true, false, null);
        public static AttributeValidationResult Invalid(string message) => new(false, false, message);
        public static AttributeValidationResult Sanitize(string message) => new(true, true, message);
    }
}
