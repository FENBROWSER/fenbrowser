using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Network;

namespace FenBrowser.Core.Security.Corb
{
    // ── Cross-Origin Read Blocking (CORB) ─────────────────────────────────────
    // Spec: https://fetch.spec.whatwg.org/#corb
    //       https://chromium.googlesource.com/chromium/src/+/HEAD/services/network/cross_origin_read_blocking_explained.md
    //
    // CORB prevents cross-origin reads of sensitive resource types (HTML, XML, JSON)
    // into opaque-origin contexts (e.g., <img>, <script>, CSS background-image with
    // no-cors mode) where the renderer should not be able to inspect the bytes.
    //
    // Pipeline:
    //   1. Network process fetches the resource.
    //   2. CORB filter (Broker side) inspects Content-Type + body sniff.
    //   3. If blocked: return an empty (sanitised) response; log a console warning.
    //   4. If allowed: forward response normally.
    //
    // This implementation runs in the Broker/Network process — never in the renderer.
    // ─────────────────────────────────────────────────────────────────────────

    public enum CorbVerdict
    {
        Allow,            // Response is safe to deliver to the renderer
        Block,            // Block entire response (return 0-byte opaque)
        AllowSafeHeaders, // Deliver response with sensitive headers stripped
    }

    public sealed class CorbFilterResult
    {
        public CorbVerdict Verdict { get; }
        public string Reason { get; }
        public bool ShouldLogConsoleWarning { get; }

        public CorbFilterResult(CorbVerdict verdict, string reason, bool warn = false)
        {
            Verdict = verdict;
            Reason = reason;
            ShouldLogConsoleWarning = warn;
        }

        public static CorbFilterResult Allow(string reason = "allowed") =>
            new(CorbVerdict.Allow, reason);
        public static CorbFilterResult Block(string reason) =>
            new(CorbVerdict.Block, reason, warn: true);
        public static CorbFilterResult SafeHeaders(string reason) =>
            new(CorbVerdict.AllowSafeHeaders, reason);
    }

    /// <summary>
    /// CORB decision engine — runs in the Broker before a cross-origin response
    /// reaches the renderer.
    /// </summary>
    public sealed class CorbFilter
    {
        // Sensitive MIME types that must never reach an opaque renderer context
        private static readonly HashSet<string> SensitiveMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "text/html",
            "text/xml",
            "application/xml",
            "application/xhtml+xml",
            "image/svg+xml",
            // JSON types
            "application/json",
            "text/json",
            "application/ld+json",
        };

        private static readonly HashSet<string> SniffableMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "text/plain",
            "application/octet-stream",
            "text/javascript",
            "application/javascript",
            "application/ecmascript",
            "text/ecmascript"
        };

        // MIME types always safe for cross-origin opaque reads
        private static readonly HashSet<string> SafeMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif",
            "video/mp4", "video/webm", "video/ogg",
            "audio/mpeg", "audio/ogg", "audio/wav", "audio/webm",
            "font/woff", "font/woff2", "font/ttf", "font/otf",
            "application/octet-stream",
        };

        // Response headers that MUST be stripped from blocked responses (CORB-safe headers)
        private static readonly HashSet<string> CorbSafeResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "cache-control", "content-language", "content-length", "content-type",
            "expires", "last-modified", "pragma",
            "access-control-allow-credentials",
            "access-control-allow-headers",
            "access-control-allow-methods",
            "access-control-allow-origin",
            "access-control-expose-headers",
            "access-control-max-age",
        };

        /// <summary>
        /// Determine whether a response should be blocked, allowed, or sanitised.
        /// Call this in the Network/Broker process before delivering to the renderer.
        /// </summary>
        /// <param name="requestMode">Fetch mode (cors, no-cors, navigate, same-origin, etc.).</param>
        /// <param name="requestOrigin">Origin of the renderer making the request.</param>
        /// <param name="responseUrl">Final URL of the response (after redirects).</param>
        /// <param name="contentType">Value of the Content-Type response header.</param>
        /// <param name="contentTypeOptions">Value of X-Content-Type-Options header.</param>
        /// <param name="responseBodyPrefix">First bytes of the response body (for MIME sniffing).</param>
        public CorbFilterResult Evaluate(
            string requestMode,
            string requestOrigin,
            string responseUrl,
            string contentType,
            string contentTypeOptions,
            ReadOnlySpan<byte> responseBodyPrefix)
        {
            // CORB only applies to cross-origin, no-cors requests (opaque responses).
            if (!string.Equals(requestMode, "no-cors", StringComparison.OrdinalIgnoreCase))
                return CorbFilterResult.Allow("Not a no-cors request");

            if (IsSameOrigin(requestOrigin, responseUrl))
                return CorbFilterResult.Allow("Same origin");

            // Parse MIME type
            var mime = ParseMimeType(contentType);

            var shouldSniff = IsSensitiveMimeType(mime) || IsSniffableMimeType(mime);
            if (!shouldSniff)
                return CorbFilterResult.Allow($"MIME type '{mime}' not sensitive");

            // nosniff header: if present and MIME is sensitive, block immediately
            bool nosniff = string.Equals(contentTypeOptions?.Trim(), "nosniff", StringComparison.OrdinalIgnoreCase);
            if (nosniff && IsSensitiveMimeType(mime))
                return CorbFilterResult.Block($"CORB blocked: nosniff + sensitive MIME '{mime}'");

            // MIME sniffing: verify declared MIME matches actual body
            var sniffed = SniffMimeType(responseBodyPrefix, mime);
            if (IsSensitiveMimeType(sniffed))
                return CorbFilterResult.Block($"CORB blocked: sniffed MIME '{sniffed}' confirms sensitive type");

            // Body doesn't match declared MIME — allow (might be misclassified, not leaking HTML/JSON/XML)
            return CorbFilterResult.Allow($"Sniffed MIME '{sniffed}' not sensitive despite declared '{mime}'");
        }

        /// <summary>
        /// Strip sensitive headers from a blocked response, returning only CORB-safe headers.
        /// </summary>
        public Dictionary<string, string> SanitiseHeaders(Dictionary<string, string> headers)
        {
            if (headers == null) return new();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in headers)
            {
                if (CorbSafeResponseHeaders.Contains(k))
                    result[k] = v;
            }
            return result;
        }

        /// <summary>Build a zero-byte opaque response for a blocked resource.</summary>
        public static BlockedCorbResponse CreateBlockedResponse(
            string requestId,
            Dictionary<string, string> originalHeaders)
        {
            return new BlockedCorbResponse
            {
                RequestId = requestId,
                StatusCode = 200,       // CORB returns 200 with empty body (not 403) to avoid leaking status
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["content-type"] = "text/plain",
                    ["content-length"] = "0",
                },
                Body = Array.Empty<byte>(),
            };
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static bool IsSameOrigin(string originA, string urlB)
        {
            if (string.IsNullOrEmpty(originA) || string.IsNullOrEmpty(urlB)) return false;
            if (Uri.TryCreate(originA, UriKind.Absolute, out var aUri) &&
                Uri.TryCreate(urlB, UriKind.Absolute, out var bUri))
            {
                var aPort = aUri.IsDefaultPort ? GetDefaultPort(aUri.Scheme) : aUri.Port;
                var bPort = bUri.IsDefaultPort ? GetDefaultPort(bUri.Scheme) : bUri.Port;
                return string.Equals(aUri.Scheme, bUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(aUri.Host, bUri.Host, StringComparison.OrdinalIgnoreCase) &&
                       aPort == bPort;
            }

            var a = WhatwgUrl.Parse(NormalizeOriginLikeInput(originA));
            var b = WhatwgUrl.Parse(urlB);
            if (a == null || b == null) return false;
            return a.ComputeOrigin().IsSameOrigin(b.ComputeOrigin());
        }

        private static string NormalizeOriginLikeInput(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (value.EndsWith("/", StringComparison.Ordinal))
            {
                return value;
            }

            return value + "/";
        }

        private static int GetDefaultPort(string scheme)
        {
            return scheme?.ToLowerInvariant() switch
            {
                "http" => 80,
                "https" => 443,
                "ws" => 80,
                "wss" => 443,
                "ftp" => 21,
                _ => -1
            };
        }

        private static string ParseMimeType(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return "application/octet-stream";
            var idx = contentType.IndexOf(';');
            return (idx >= 0 ? contentType.Substring(0, idx) : contentType).Trim().ToLowerInvariant();
        }

        private bool IsSensitiveMimeType(string mime)
        {
            if (string.IsNullOrEmpty(mime))
                return false;

            if (SensitiveMimeTypes.Contains(mime))
                return true;

            return mime.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
                   mime.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsSniffableMimeType(string mime)
            => !string.IsNullOrEmpty(mime) && SniffableMimeTypes.Contains(mime);

        private static string SniffMimeType(ReadOnlySpan<byte> prefix, string declared)
        {
            if (prefix.IsEmpty) return declared;
            prefix = TrimLeadingNoise(prefix);

            // HTML sniffing: look for BOM or <!DOCTYPE html or <html
            if (StartsWithSkippingWhitespace(prefix, "<!doctype"u8) ||
                StartsWithSkippingWhitespace(prefix, "<html"u8) ||
                StartsWithSkippingWhitespace(prefix, "<head"u8) ||
                StartsWithSkippingWhitespace(prefix, "<body"u8) ||
                StartsWithSkippingWhitespace(prefix, "<script"u8) ||
                StartsWithSkippingWhitespace(prefix, "<iframe"u8) ||
                HasHtmlBom(prefix))
                return "text/html";

            // XML sniffing
            if (StartsWithSkippingWhitespace(prefix, "<?xml"u8) ||
                StartsWithSkippingWhitespace(prefix, "<svg"u8) ||
                StartsWithSkippingWhitespace(prefix, "<?"u8))
                return "text/xml";

            // JSON sniffing: starts with { or [ (whitespace allowed)
            byte first = SkipWhitespace(prefix);
            if (first == '{' || first == '[')
                return "application/json";

            return declared;
        }

        private static bool StartsWithSkippingWhitespace(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
        {
            int i = 0;
            while (i < data.Length && (data[i] == ' ' || data[i] == '\t' || data[i] == '\n' || data[i] == '\r'))
                i++;
            var slice = data.Slice(i);
            if (slice.Length < prefix.Length) return false;
            for (int j = 0; j < prefix.Length; j++)
                if (char.ToLowerInvariant((char)slice[j]) != char.ToLowerInvariant((char)prefix[j]))
                    return false;
            return true;
        }

        private static byte SkipWhitespace(ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
                if (b != ' ' && b != '\t' && b != '\n' && b != '\r') return b;
            return 0;
        }

        private static ReadOnlySpan<byte> TrimLeadingNoise(ReadOnlySpan<byte> data)
        {
            var offset = 0;

            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                offset = 3;
            else if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
                offset = 2;

            var slice = data.Slice(offset);
            while (slice.Length > 0 && (slice[0] == ' ' || slice[0] == '\t' || slice[0] == '\n' || slice[0] == '\r'))
                slice = slice.Slice(1);

            if (slice.Length >= 5 &&
                slice[0] == ')' &&
                slice[1] == ']' &&
                slice[2] == '}' &&
                slice[3] == '\'' &&
                (slice[4] == '\n' || slice[4] == '\r'))
            {
                slice = slice.Slice(5);
            }

            return slice;
        }

        private static bool HasHtmlBom(ReadOnlySpan<byte> data)
        {
            // UTF-8 BOM: EF BB BF
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return true;
            // UTF-16 LE BOM: FF FE
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return true;
            // UTF-16 BE BOM: FE FF
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return true;
            return false;
        }
    }

    public sealed class BlockedCorbResponse
    {
        public string RequestId { get; init; }
        public int StatusCode { get; init; }
        public Dictionary<string, string> Headers { get; init; }
        public byte[] Body { get; init; }
    }
}
