using System;
using System.Text;
using System.Text.RegularExpressions;

namespace FenBrowser.Core.Network
{
    /// <summary>
    /// Encoding detection per WHATWG Encoding Standard.
    /// https://encoding.spec.whatwg.org/
    /// 
    /// Priority:
    /// 1. BOM (Byte Order Mark)
    /// 2. HTTP Content-Type charset parameter
    /// 3. HTML <meta> tag (first 1024 bytes)
    /// 4. Fallback: Windows-1252 (legacy web default)
    /// </summary>
    public static class EncodingSniffer
    {
        private static readonly Encoding Windows1252;
        private static readonly Regex CharsetRegex = new Regex(
            @"charset\s*=\s*([""']?)([^;""'\s]+)\1",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        
        private static readonly Regex MetaCharsetRegex = new Regex(
            @"<meta[^>]+charset\s*=\s*[""']?([^""'>\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        
        private static readonly Regex MetaContentTypeRegex = new Regex(
            @"<meta[^>]+http-equiv\s*=\s*[""']?Content-Type[""']?[^>]+content\s*=\s*[""']?[^""'>]*charset\s*=\s*([^""'>\s;]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static EncodingSniffer()
        {
            // Register code pages for Windows-1252 and other legacy encodings
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Windows1252 = Encoding.GetEncoding(1252);
        }

        /// <summary>
        /// Determines the encoding of the given bytes.
        /// </summary>
        /// <param name="bytes">Raw bytes from HTTP response body.</param>
        /// <param name="contentTypeHeader">Value of Content-Type HTTP header (may be null).</param>
        /// <returns>Detected encoding, never null.</returns>
        public static Encoding DetermineEncoding(byte[] bytes, string contentTypeHeader)
        {
            if (bytes == null || bytes.Length == 0)
                return Encoding.UTF8;

            // 1. Check BOM
            var bomEncoding = DetectBom(bytes);
            if (bomEncoding != null)
                return bomEncoding;

            // 2. Check HTTP Content-Type charset
            if (!string.IsNullOrEmpty(contentTypeHeader))
            {
                var httpCharset = ExtractCharsetFromContentType(contentTypeHeader);
                if (!string.IsNullOrEmpty(httpCharset))
                {
                    var enc = GetEncodingByName(httpCharset);
                    if (enc != null)
                        return enc;
                }
            }

            // 3. Check HTML <meta> tag (first 1024 bytes)
            var metaCharset = DetectMetaCharset(bytes);
            if (!string.IsNullOrEmpty(metaCharset))
            {
                var enc = GetEncodingByName(metaCharset);
                if (enc != null)
                    return enc;
            }

            // 4. Fallback: Windows-1252 (per HTML spec legacy behavior)
            return Windows1252;
        }

        /// <summary>
        /// Detects encoding from Byte Order Mark.
        /// </summary>
        private static Encoding DetectBom(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8;

            if (bytes.Length >= 2)
            {
                if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                    return Encoding.Unicode; // UTF-16LE

                if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                    return Encoding.BigEndianUnicode; // UTF-16BE
            }

            return null;
        }

        /// <summary>
        /// Extracts charset from Content-Type header value.
        /// </summary>
        private static string ExtractCharsetFromContentType(string contentType)
        {
            var match = CharsetRegex.Match(contentType);
            return match.Success ? match.Groups[2].Value.Trim() : null;
        }

        /// <summary>
        /// Scans first 1024 bytes for HTML meta charset declaration.
        /// </summary>
        private static string DetectMetaCharset(byte[] bytes)
        {
            // Use ASCII to interpret first 1024 bytes (safe for tag detection)
            var scanLength = Math.Min(bytes.Length, 1024);
            var snippet = Encoding.ASCII.GetString(bytes, 0, scanLength);

            // Try <meta charset="...">
            var match = MetaCharsetRegex.Match(snippet);
            if (match.Success)
                return match.Groups[1].Value.Trim();

            // Try <meta http-equiv="Content-Type" content="text/html; charset=...">
            match = MetaContentTypeRegex.Match(snippet);
            if (match.Success)
                return match.Groups[1].Value.Trim();

            return null;
        }

        /// <summary>
        /// Maps charset name to Encoding, normalizing common aliases.
        /// </summary>
        private static Encoding GetEncodingByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var normalized = name.Trim().ToLowerInvariant();

            // Common aliases per WHATWG Encoding Standard
            switch (normalized)
            {
                case "utf-8":
                case "utf8":
                    return Encoding.UTF8;

                case "utf-16":
                case "utf-16le":
                    return Encoding.Unicode;

                case "utf-16be":
                    return Encoding.BigEndianUnicode;

                case "iso-8859-1":
                case "latin1":
                case "latin-1":
                case "windows-1252":
                case "cp1252":
                    return Windows1252;

                case "ascii":
                case "us-ascii":
                    return Encoding.ASCII;

                default:
                    try
                    {
                        return Encoding.GetEncoding(name);
                    }
                    catch
                    {
                        return null;
                    }
            }
        }

        /// <summary>
        /// Transcodes bytes to UTF-8 string using the detected encoding.
        /// </summary>
        public static string DecodeToUtf8(byte[] bytes, string contentTypeHeader)
        {
            var encoding = DetermineEncoding(bytes, contentTypeHeader);
            return encoding.GetString(bytes);
        }
    }
}
