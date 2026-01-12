using System;

namespace FenBrowser.Core.Network
{
    /// <summary>
    /// MIME type sniffing per WHATWG MIME Sniff Standard.
    /// https://mimesniff.spec.whatwg.org/
    /// 
    /// Used when Content-Type header is missing, unknown, or potentially wrong.
    /// </summary>
    public static class MimeSniffer
    {
        /// <summary>
        /// Sniffs the actual MIME type from bytes.
        /// </summary>
        /// <param name="bytes">First bytes of resource (512+ recommended).</param>
        /// <param name="declaredMime">MIME type from Content-Type header (may be null).</param>
        /// <returns>Sniffed MIME type or declared MIME if sniffing fails.</returns>
        public static string SniffMimeType(byte[] bytes, string declaredMime)
        {
            if (bytes == null || bytes.Length == 0)
                return declaredMime ?? "application/octet-stream";

            // If text/* declared, trust it unless obviously binary
            if (!string.IsNullOrEmpty(declaredMime) && declaredMime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                if (!LooksBinary(bytes))
                    return declaredMime;
            }

            // Magic byte sniffing
            var sniffed = SniffFromMagicBytes(bytes);
            if (!string.IsNullOrEmpty(sniffed))
                return sniffed;

            // If declared MIME is available, trust it
            if (!string.IsNullOrEmpty(declaredMime))
                return declaredMime;

            // Unknown - if not binary, assume HTML (legacy web behavior)
            return LooksBinary(bytes) ? "application/octet-stream" : "text/html";
        }

        /// <summary>
        /// Checks if content appears to be binary (contains NUL or high density of non-printable chars).
        /// </summary>
        private static bool LooksBinary(byte[] bytes)
        {
            int checkLen = Math.Min(bytes.Length, 512);
            int nonPrintable = 0;

            for (int i = 0; i < checkLen; i++)
            {
                byte b = bytes[i];
                
                // NUL byte = definitely binary
                if (b == 0) return true;
                
                // Non-printable (except common whitespace)
                if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D)
                    nonPrintable++;
            }

            // > 10% non-printable = likely binary
            return nonPrintable > (checkLen / 10);
        }

        /// <summary>
        /// Sniffs MIME type from magic bytes.
        /// </summary>
        private static string SniffFromMagicBytes(byte[] bytes)
        {
            int len = bytes.Length;

            // HTML signatures
            if (len >= 5 && StartsWithIgnoreCase(bytes, "<!DOC")) return "text/html";
            if (len >= 5 && StartsWithIgnoreCase(bytes, "<html")) return "text/html";
            if (len >= 5 && StartsWithIgnoreCase(bytes, "<head")) return "text/html";
            if (len >= 6 && StartsWithIgnoreCase(bytes, "<body")) return "text/html";
            if (len >= 15 && StartsWithIgnoreCase(bytes, "<!DOCTYPE html")) return "text/html";

            // XML
            if (len >= 5 && StartsWithIgnoreCase(bytes, "<?xml")) return "application/xml";

            // JSON (starts with { or [)
            if (len >= 1 && (bytes[0] == '{' || bytes[0] == '[')) return "application/json";

            // JavaScript (common patterns)
            if (len >= 8 && StartsWithIgnoreCase(bytes, "function")) return "application/javascript";
            if (len >= 4 && StartsWithIgnoreCase(bytes, "var ")) return "application/javascript";
            if (len >= 5 && StartsWithIgnoreCase(bytes, "const")) return "application/javascript";
            if (len >= 3 && StartsWithIgnoreCase(bytes, "let")) return "application/javascript";

            // CSS
            if (len >= 1 && bytes[0] == '@') return "text/css"; // @charset, @import, etc.
            if (len >= 4 && ContainsPattern(bytes, 0, 100, "{") && ContainsPattern(bytes, 0, 100, ":")) 
                return "text/css"; // Likely CSS ruleset

            // Images
            if (len >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
            if (len >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
            if (len >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
            if (len >= 4 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46) 
            {
                if (len >= 12 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                    return "image/webp";
            }
            if (len >= 4 && StartsWithIgnoreCase(bytes, "<svg")) return "image/svg+xml";

            // PDF
            if (len >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46) return "application/pdf";

            // Fonts
            if (len >= 4 && bytes[0] == 0x00 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00) return "font/ttf";
            if (len >= 4 && bytes[0] == 0x4F && bytes[1] == 0x54 && bytes[2] == 0x54 && bytes[3] == 0x4F) return "font/otf";
            if (len >= 4 && bytes[0] == 0x77 && bytes[1] == 0x4F && bytes[2] == 0x46 && bytes[3] == 0x46) return "font/woff";
            if (len >= 4 && bytes[0] == 0x77 && bytes[1] == 0x4F && bytes[2] == 0x46 && bytes[3] == 0x32) return "font/woff2";

            return null;
        }

        private static bool StartsWithIgnoreCase(byte[] bytes, string pattern)
        {
            if (bytes.Length < pattern.Length) return false;
            for (int i = 0; i < pattern.Length; i++)
            {
                byte b = bytes[i];
                char c = pattern[i];
                if (char.ToLowerInvariant((char)b) != char.ToLowerInvariant(c))
                    return false;
            }
            return true;
        }

        private static bool ContainsPattern(byte[] bytes, int start, int maxLen, string pattern)
        {
            int end = Math.Min(bytes.Length, start + maxLen);
            for (int i = start; i <= end - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if ((char)bytes[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }

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
