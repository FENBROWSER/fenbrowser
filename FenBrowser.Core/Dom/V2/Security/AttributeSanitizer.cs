// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2.Security - Attribute Sanitization

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FenBrowser.Core.Dom.V2.Security
{
    /// <summary>
    /// Validates and sanitizes element attributes to prevent XSS and other attacks.
    /// Based on OWASP recommendations and browser security best practices.
    /// </summary>
    public static class AttributeSanitizer
    {
        // --- Configuration ---

        /// <summary>
        /// Set to true to enable strict security mode (blocks more potentially dangerous attributes).
        /// Default: true
        /// </summary>
        public static bool StrictMode { get; set; } = true;

        /// <summary>
        /// Set to true to log blocked attributes for debugging.
        /// </summary>
        public static bool LogBlocked { get; set; } = false;

        /// <summary>
        /// Set to true to clear inline event-handler values (e.g. onclick) while in strict mode.
        /// Default is false for browser-compatibility with HTML content.
        /// </summary>
        public static bool BlockInlineEventHandlersInStrictMode { get; set; } = false;

        // --- Dangerous URL Schemes ---

        private static readonly HashSet<string> DangerousSchemes = new(StringComparer.OrdinalIgnoreCase)
        {
            "javascript",
            "vbscript",
            "data",  // Can embed scripts in some contexts
            "file",  // Security risk
        };

        private static readonly HashSet<string> AllowedDataMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/png",
            "image/jpeg",
            "image/gif",
            "image/webp",
            "image/svg+xml", // Note: SVG can contain scripts, may want to filter
            "text/plain",
        };

        // --- Event Handler Attributes ---

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
            // Additional security-sensitive handlers
            "onbeforeunload", "onhashchange", "onlanguagechange", "onmessage",
            "onmessageerror", "onoffline", "ononline", "onpagehide", "onpageshow",
            "onpopstate", "onrejectionhandled", "onstorage", "onunhandledrejection",
            "onunload"
        };

        // --- URL Attributes ---

        private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "href", "src", "action", "formaction", "data", "poster",
            "cite", "background", "codebase", "dynsrc", "lowsrc",
            "usemap", "longdesc", "profile", "xmlns", "xlink:href"
        };

        // --- Dangerous Attribute Patterns ---

        private static readonly Regex JavaScriptPattern = new(
            @"javascript\s*:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex VbScriptPattern = new(
            @"vbscript\s*:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ExpressionPattern = new(
            @"expression\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DataUriPattern = new(
            @"^data:([^;,]+)?(;[^,]+)*,",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // --- Validation API ---

        /// <summary>
        /// Validates an attribute name.
        /// </summary>
        /// <param name="name">The attribute name</param>
        /// <returns>Validation result</returns>
        public static AttributeValidationResult ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return AttributeValidationResult.Invalid("Attribute name cannot be empty");

            // Check for invalid characters per XML Name production
            if (!IsValidXmlName(name))
                return AttributeValidationResult.Invalid($"Invalid characters in attribute name: {name}");

            // Warn about event handlers
            if (IsEventHandler(name))
                return AttributeValidationResult.Sanitize($"Event handler attribute: {name}");

            return AttributeValidationResult.Valid();
        }

        /// <summary>
        /// Validates and optionally sanitizes an attribute value.
        /// </summary>
        /// <param name="name">The attribute name</param>
        /// <param name="value">The attribute value</param>
        /// <param name="sanitizedValue">The sanitized value (if sanitization occurred)</param>
        /// <returns>Validation result</returns>
        public static AttributeValidationResult ValidateValue(
            string name, string value, out string sanitizedValue)
        {
            sanitizedValue = value;

            if (string.IsNullOrEmpty(value))
                return AttributeValidationResult.Valid();

            // Inline event handlers are valid HTML and must be preserved for web compatibility.
            // Hardened deployments can still opt into blocking via BlockInlineEventHandlersInStrictMode.
            if (StrictMode && BlockInlineEventHandlersInStrictMode && IsEventHandler(name))
            {
                if (LogBlocked)
                    System.Diagnostics.Debug.WriteLine($"[Security] Blocked event handler: {name}");

                sanitizedValue = "";
                return AttributeValidationResult.Sanitize("Event handler values blocked in strict mode");
            }

            // Check URL attributes for dangerous schemes
            if (IsUrlAttribute(name))
            {
                var urlResult = ValidateUrl(value, out var sanitizedUrl);
                sanitizedValue = sanitizedUrl;
                return urlResult;
            }

            // Check style attribute for dangerous CSS
            if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                var styleResult = ValidateStyleAttribute(value, out var sanitizedStyle);
                sanitizedValue = sanitizedStyle;
                return styleResult;
            }

            // Check srcdoc for iframe (dangerous - can contain arbitrary HTML)
            if (name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase) && StrictMode)
            {
                sanitizedValue = "";
                return AttributeValidationResult.Sanitize("srcdoc blocked in strict mode");
            }

            return AttributeValidationResult.Valid();
        }

        /// <summary>
        /// Checks if an attribute name is an event handler.
        /// </summary>
        public static bool IsEventHandler(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return EventHandlerAttributes.Contains(name) ||
                   name.StartsWith("on", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if an attribute typically contains a URL.
        /// </summary>
        public static bool IsUrlAttribute(string name)
        {
            return UrlAttributes.Contains(name);
        }

        // --- Internal Validation Methods ---

        private static bool IsValidXmlName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // First character must be letter, underscore, or colon
            char first = name[0];
            if (!char.IsLetter(first) && first != '_' && first != ':')
                return false;

            // Rest can include letters, digits, hyphens, underscores, colons, periods
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

            // Check for javascript: scheme
            if (JavaScriptPattern.IsMatch(trimmed))
            {
                if (LogBlocked)
                    System.Diagnostics.Debug.WriteLine($"[Security] Blocked javascript: URL");
                sanitizedUrl = "";
                return AttributeValidationResult.Sanitize("javascript: URLs are blocked");
            }

            // Check for vbscript: scheme
            if (VbScriptPattern.IsMatch(trimmed))
            {
                if (LogBlocked)
                    System.Diagnostics.Debug.WriteLine($"[Security] Blocked vbscript: URL");
                sanitizedUrl = "";
                return AttributeValidationResult.Sanitize("vbscript: URLs are blocked");
            }

            // Check data: URLs
            var dataMatch = DataUriPattern.Match(trimmed);
            if (dataMatch.Success)
            {
                var mimeType = dataMatch.Groups[1].Value;
                if (!AllowedDataMimeTypes.Contains(mimeType))
                {
                    if (LogBlocked)
                        System.Diagnostics.Debug.WriteLine($"[Security] Blocked data: URL with mime type: {mimeType}");
                    sanitizedUrl = "";
                    return AttributeValidationResult.Sanitize($"data: URLs with mime type '{mimeType}' are blocked");
                }
            }

            // Check for dangerous scheme in general
            int colonIndex = trimmed.IndexOf(':');
            if (colonIndex > 0 && colonIndex < 20) // Scheme shouldn't be too long
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

            // Check for javascript in style (expression() is IE-specific but still blocked)
            if (JavaScriptPattern.IsMatch(style))
            {
                sanitizedStyle = "";
                return AttributeValidationResult.Sanitize("javascript: in style attribute blocked");
            }

            // Check for expression() (IE CSS expression - XSS vector)
            if (ExpressionPattern.IsMatch(style))
            {
                sanitizedStyle = "";
                return AttributeValidationResult.Sanitize("CSS expression() blocked");
            }

            // Check for url() with dangerous content
            if (style.Contains("url(", StringComparison.OrdinalIgnoreCase))
            {
                // Extract and validate URLs in style
                var urlResult = SanitizeStyleUrls(style, out var sanitizedStyleUrls);
                sanitizedStyle = sanitizedStyleUrls;
                return urlResult;
            }

            return AttributeValidationResult.Valid();
        }

        private static readonly Regex StyleUrlPattern = new(
            @"url\s*\(\s*['""]?([^'""\)]+)['""]?\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static AttributeValidationResult SanitizeStyleUrls(string style, out string sanitizedStyle)
        {
            sanitizedStyle = style;
            bool modified = false;

            var matches = StyleUrlPattern.Matches(style);
            foreach (Match match in matches)
            {
                var url = match.Groups[1].Value;
                var urlResult = ValidateUrl(url, out _);
                if (!urlResult.IsValid)
                {
                    // Remove the entire url() call
                    sanitizedStyle = sanitizedStyle.Replace(match.Value, "none");
                    modified = true;
                }
            }

            return modified
                ? AttributeValidationResult.Sanitize("Dangerous URLs in style removed")
                : AttributeValidationResult.Valid();
        }
    }

    /// <summary>
    /// Result of attribute validation.
    /// </summary>
    public readonly struct AttributeValidationResult
    {
        /// <summary>Whether the attribute is valid.</summary>
        public bool IsValid { get; }

        /// <summary>Whether the value was sanitized.</summary>
        public bool WasSanitized { get; }

        /// <summary>Message describing the validation result.</summary>
        public string Message { get; }

        private AttributeValidationResult(bool isValid, bool wasSanitized, string message)
        {
            IsValid = isValid;
            WasSanitized = wasSanitized;
            Message = message;
        }

        /// <summary>Creates a valid result.</summary>
        public static AttributeValidationResult Valid() => new(true, false, null);

        /// <summary>Creates an invalid result.</summary>
        public static AttributeValidationResult Invalid(string message) => new(false, false, message);

        /// <summary>Creates a sanitized result (value was modified).</summary>
        public static AttributeValidationResult Sanitize(string message) => new(true, true, message);
    }
}
