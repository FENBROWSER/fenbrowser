using System;

namespace FenBrowser.Core.Network
{
    /// <summary>
    /// Conservative MIME type sniffing for resources whose supplied type is missing
    /// or explicitly unknown. A valid supplied MIME type is authoritative here; the
    /// caller can apply context-specific MIME Sniffing rules separately when needed.
    /// </summary>
    public static class MimeSniffer
    {
        /// <summary>
        /// Sniffs the actual MIME type from bytes only when the supplied type is absent
        /// or one of the standard unknown sentinels.
        /// </summary>
        public static string SniffMimeType(byte[] bytes, string declaredMime)
        {
            var supplied = NormalizeSuppliedMime(declaredMime);
            if (!IsUnknownMime(supplied))
            {
                // Do not promote server-declared low-privilege content into HTML,
                // JavaScript, CSS, or another active type based on body heuristics.
                return declaredMime.Trim();
            }

            if (bytes == null || bytes.Length == 0)
            {
                return "application/octet-stream";
            }

            var sniffed = SniffFromMagicBytes(bytes);
            if (!string.IsNullOrEmpty(sniffed))
            {
                return sniffed;
            }

            // WHATWG's unknown-type algorithm ends by distinguishing text from binary.
            // Plain textual bytes are text/plain, never implicitly privileged HTML.
            return LooksBinary(bytes) ? "application/octet-stream" : "text/plain";
        }

        private static string NormalizeSuppliedMime(string declaredMime)
        {
            if (string.IsNullOrWhiteSpace(declaredMime))
            {
                return null;
            }

            var semicolon = declaredMime.IndexOf(';');
            var essence = semicolon >= 0 ? declaredMime[..semicolon] : declaredMime;
            return essence.Trim().ToLowerInvariant();
        }

        private static bool IsUnknownMime(string mime)
        {
            return string.IsNullOrEmpty(mime) ||
                   string.Equals(mime, "unknown/unknown", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mime, "application/unknown", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mime, "*/*", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks for binary data bytes using the MIME Sniffing Standard's byte class.
        /// </summary>
        private static bool LooksBinary(byte[] bytes)
        {
            var checkLen = Math.Min(bytes.Length, 1445);
            for (var i = 0; i < checkLen; i++)
            {
                var b = bytes[i];
                if (b <= 0x08 || b == 0x0B || (b >= 0x0E && b <= 0x1A) || (b >= 0x1C && b <= 0x1F))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Sniffs well-defined byte signatures for an unknown supplied type.
        /// Deliberately does not guess JavaScript/CSS/JSON from source text.
        /// </summary>
        private static string SniffFromMagicBytes(byte[] bytes)
        {
            var len = bytes.Length;

            // BOM signatures take precedence over any later active-content signature.
            // In the unknown-type algorithm they identify text/plain, so a BOM-prefixed
            // "<html>" body must not be promoted to scriptable HTML.
            if (len >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return "text/plain";
            if (len >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return "text/plain";
            if (len >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return "text/plain";

            var offset = SkipInsignificantPrefix(bytes, len);
            if (offset >= len)
            {
                return null;
            }

            // Scriptable signatures from the unknown-type sniffing family.
            if (len - offset >= 15 && StartsWithIgnoreCase(bytes, offset, "<!DOCTYPE html")) return "text/html";
            if (len - offset >= 5 && StartsWithIgnoreCase(bytes, offset, "<html")) return "text/html";
            if (len - offset >= 5 && StartsWithIgnoreCase(bytes, offset, "<head")) return "text/html";
            if (len - offset >= 6 && StartsWithIgnoreCase(bytes, offset, "<body")) return "text/html";
            if (len - offset >= 5 && StartsWithIgnoreCase(bytes, offset, "<?xml")) return "text/xml";
            if (len >= 5 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 && bytes[4] == 0x2D)
                return "application/pdf";

            // Images.
            if (len >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 &&
                bytes[3] == 0x38 && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
                return "image/gif";
            if (len >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
                return "image/png";
            if (len >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "image/jpeg";
            if (len >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return "image/webp";

            // Audio/video.
            if (len >= 4 && bytes[0] == 0x4F && bytes[1] == 0x67 && bytes[2] == 0x67 && bytes[3] == 0x53)
                return "application/ogg";
            if (len >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
                return "video/webm";
            if (len >= 3 && bytes[0] == 0x49 && bytes[1] == 0x44 && bytes[2] == 0x33)
                return "audio/mpeg";
            if (len >= 12 && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
                return "video/mp4";

            // Fonts.
            if (len >= 4 && bytes[0] == 0x00 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
                return "font/ttf";
            if (len >= 4 && bytes[0] == 0x4F && bytes[1] == 0x54 && bytes[2] == 0x54 && bytes[3] == 0x4F)
                return "font/otf";
            if (len >= 4 && bytes[0] == 0x77 && bytes[1] == 0x4F && bytes[2] == 0x46 && bytes[3] == 0x46)
                return "font/woff";
            if (len >= 4 && bytes[0] == 0x77 && bytes[1] == 0x4F && bytes[2] == 0x46 && bytes[3] == 0x32)
                return "font/woff2";

            return null;
        }

        private static bool StartsWithIgnoreCase(byte[] bytes, int offset, string pattern)
        {
            if (offset < 0 || bytes.Length - offset < pattern.Length) return false;
            for (var i = 0; i < pattern.Length; i++)
            {
                var b = bytes[offset + i];
                var expected = (byte)pattern[i];
                if (b == expected) continue;

                // All signatures passed here are ASCII. Fold only ASCII letters; do
                // not involve culture/Unicode character casing in byte matching.
                if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 0x20);
                if (expected >= (byte)'A' && expected <= (byte)'Z') expected = (byte)(expected + 0x20);
                if (b != expected) return false;
            }
            return true;
        }

        private static int SkipInsignificantPrefix(byte[] bytes, int len)
        {
            var index = 0;
            while (index < len && IsHttpWhitespace(bytes[index]))
            {
                index++;
            }

            return index;
        }

        private static bool IsHttpWhitespace(byte b) =>
            b is 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

        /// <summary>
        /// Returns true if the MIME type indicates text content suitable for parsing.
        /// </summary>
        public static bool IsTextMime(string mime)
        {
            if (string.IsNullOrEmpty(mime)) return false;
            var lower = mime.ToLowerInvariant();
            return lower.StartsWith("text/") ||
                   lower.Contains("javascript") ||
                   lower.Contains("json") ||
                   lower.Contains("xml") ||
                   lower.Contains("+xml");
        }
    }
}
