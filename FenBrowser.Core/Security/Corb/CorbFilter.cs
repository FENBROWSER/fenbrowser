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
        Allow,
        Block,
        AllowSafeHeaders,
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
        private static readonly HashSet<string> SensitiveMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "text/html",
            "text/xml",
            "application/xml",
            "application/xhtml+xml",
            "image/svg+xml",
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

        private static readonly HashSet<string> SafeMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif",
            "video/mp4", "video/webm", "video/ogg",
            "audio/mpeg", "audio/ogg", "audio/wav", "audio/webm",
            "font/woff", "font/woff2", "font/ttf", "font/otf",
            "application/octet-stream",
        };

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

        public CorbFilterResult Evaluate(
            string requestMode,
            string requestOrigin,
            string responseUrl,
            string contentType,
            string contentTypeOptions,
            ReadOnlySpan<byte> responseBodyPrefix)
        {
            if (!string.Equals(requestMode, "no-cors", StringComparison.OrdinalIgnoreCase))
                return CorbFilterResult.Allow("Not a no-cors request");

            if (IsSameOrigin(requestOrigin, responseUrl))
                return CorbFilterResult.Allow("Same origin");

            var mime = ParseMimeType(contentType);

            var shouldSniff = IsSensitiveMimeType(mime) || IsSniffableMimeType(mime);
            if (!shouldSniff)
                return CorbFilterResult.Allow($"MIME type '{mime}' not sensitive");

            bool nosniff = string.Equals(contentTypeOptions?.Trim(), "nosniff", StringComparison.OrdinalIgnoreCase);
            if (nosniff && IsSensitiveMimeType(mime))
                return CorbFilterResult.Block($"CORB blocked: nosniff + sensitive MIME '{mime}'");

            var sniffed = SniffMimeType(responseBodyPrefix, mime);
            if (IsSensitiveMimeType(sniffed))
                return CorbFilterResult.Block($"CORB blocked: sniffed MIME '{sniffed}' confirms sensitive type");

            return CorbFilterResult.Allow($"Sniffed MIME '{sniffed}' not sensitive despite declared '{mime}'");
        }

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

        public static BlockedCorbResponse CreateBlockedResponse(
            string requestId,
            Dictionary<string, string> originalHeaders)
        {
            return new BlockedCorbResponse
            {
                RequestId = requestId,
                StatusCode = 200,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["content-type"] = "text/plain",
                    ["content-length"] = "0",
                },
                Body = Array.Empty<byte>(),
            };
        }

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
            if (prefix.IsEmpty) return declared;

            // Require real tag/declaration boundaries. Prefix-only checks classify
            // ordinary text such as <htmlx>, <scripture> or <svgwhatever> as active
            // markup and can incorrectly zero a cross-origin response.
            if (StartsWithMarkupToken(prefix, "<!doctype"u8, declarationToken: true) ||
                StartsWithMarkupToken(prefix, "<html"u8) ||
                StartsWithMarkupToken(prefix, "<head"u8) ||
                StartsWithMarkupToken(prefix, "<body"u8) ||
                StartsWithMarkupToken(prefix, "<script"u8) ||
                StartsWithMarkupToken(prefix, "<iframe"u8))
            {
                return "text/html";
            }

            if (StartsWithMarkupToken(prefix, "<?xml"u8, declarationToken: true) ||
                StartsWithMarkupToken(prefix, "<svg"u8))
            {
                return "text/xml";
            }

            // JSON sniffing remains intentionally conservative: an opaque response
            // whose first non-whitespace byte is an object/array opener is treated as
            // sensitive data.
            byte first = SkipWhitespace(prefix);
            if (first == '{' || first == '[')
                return "application/json";

            return declared;
        }

        private static bool StartsWithMarkupToken(
            ReadOnlySpan<byte> data,
            ReadOnlySpan<byte> token,
            bool declarationToken = false)
        {
            var offset = SkipWhitespaceOffset(data);
            if (data.Length - offset < token.Length)
                return false;

            for (var i = 0; i < token.Length; i++)
            {
                var actual = data[offset + i];
                var expected = token[i];
                if (actual >= (byte)'A' && actual <= (byte)'Z') actual = (byte)(actual + 0x20);
                if (expected >= (byte)'A' && expected <= (byte)'Z') expected = (byte)(expected + 0x20);
                if (actual != expected)
                    return false;
            }

            var end = offset + token.Length;
            if (end >= data.Length)
                return true;

            var next = data[end];
            if (declarationToken)
            {
                return next == (byte)'>' || next == (byte)'?' || IsHtmlWhitespace(next);
            }

            return next == (byte)'>' || next == (byte)'/' || IsHtmlWhitespace(next);
        }

        private static int SkipWhitespaceOffset(ReadOnlySpan<byte> data)
        {
            var i = 0;
            while (i < data.Length && IsHtmlWhitespace(data[i]))
                i++;
            return i;
        }

        private static byte SkipWhitespace(ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
                if (!IsHtmlWhitespace(b)) return b;
            return 0;
        }

        private static bool IsHtmlWhitespace(byte value) =>
            value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C;

        private static ReadOnlySpan<byte> TrimLeadingNoise(ReadOnlySpan<byte> data)
        {
            var offset = 0;

            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                offset = 3;
            else if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
                offset = 2;

            var slice = data.Slice(offset);
            while (slice.Length > 0 && IsHtmlWhitespace(slice[0]))
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
    }

    public sealed class BlockedCorbResponse
    {
        public string RequestId { get; init; }
        public int StatusCode { get; init; }
        public Dictionary<string, string> Headers { get; init; }
        public byte[] Body { get; init; }
    }
}
