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
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        static EncodingSniffer()
        {
            // Register code pages for Windows-1252 and other legacy encodings.
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

            // 1. Check BOM.
            var bomEncoding = DetectBom(bytes);
            if (bomEncoding != null)
                return bomEncoding;

            // 2. Check HTTP Content-Type charset.
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

            // 3. Prescan HTML <meta> declarations in the first 1024 bytes. The scan
            // is lexical rather than regex-based so attribute order, comments and
            // quoted '>' characters cannot create false declarations.
            var metaEncoding = DetectMetaEncoding(bytes);
            if (metaEncoding != null)
                return metaEncoding;

            // 4. Fallback: Windows-1252 (per HTML legacy behavior).
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
            if (string.IsNullOrWhiteSpace(contentType))
                return null;

            try
            {
                var match = CharsetRegex.Match(contentType);
                return match.Success ? match.Groups[2].Value.Trim() : null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        /// <summary>
        /// Scans the first 1024 bytes for HTML meta encoding declarations without
        /// treating comment text or quoted tag text as markup.
        /// </summary>
        private static Encoding DetectMetaEncoding(byte[] bytes)
        {
            var scanLength = Math.Min(bytes.Length, 1024);
            if (scanLength <= 0)
                return null;

            // Markup relevant to the prescan is ASCII. High bytes becoming '?' does
            // not change the positions/meaning of ASCII tag and attribute delimiters.
            var snippet = Encoding.ASCII.GetString(bytes, 0, scanLength);
            var index = 0;

            while (index < snippet.Length)
            {
                var tagStart = snippet.IndexOf('<', index);
                if (tagStart < 0)
                    break;

                if (StartsWithAsciiIgnoreCase(snippet, tagStart, "<!--"))
                {
                    var commentEnd = snippet.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                    if (commentEnd < 0)
                        break;

                    index = commentEnd + 3;
                    continue;
                }

                if (StartsWithTagName(snippet, tagStart, "meta"))
                {
                    var tagEnd = FindTagEnd(snippet, tagStart + 5);
                    if (tagEnd < 0)
                        break;

                    ParseMetaAttributes(
                        snippet,
                        tagStart + 5,
                        tagEnd,
                        out var charset,
                        out var httpEquiv,
                        out var content);

                    if (!string.IsNullOrWhiteSpace(charset))
                    {
                        var encoding = GetMetaEncodingByName(charset);
                        if (encoding != null)
                            return encoding;
                    }

                    if (string.Equals(httpEquiv, "content-type", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(content))
                    {
                        var contentCharset = ExtractCharsetFromContentType(content);
                        var encoding = GetMetaEncodingByName(contentCharset);
                        if (encoding != null)
                            return encoding;
                    }

                    index = tagEnd + 1;
                    continue;
                }

                // Do not allow markup-looking strings inside raw-text script/style
                // contents to become encoding declarations.
                if (StartsWithTagName(snippet, tagStart, "script") ||
                    StartsWithTagName(snippet, tagStart, "style"))
                {
                    var openEnd = FindTagEnd(snippet, tagStart + 2);
                    if (openEnd < 0)
                        break;

                    var rawName = StartsWithTagName(snippet, tagStart, "script") ? "script" : "style";
                    var closeMarker = "</" + rawName;
                    var closeStart = IndexOfAsciiIgnoreCase(snippet, closeMarker, openEnd + 1);
                    if (closeStart < 0)
                        break;

                    var closeEnd = FindTagEnd(snippet, closeStart + closeMarker.Length);
                    index = closeEnd >= 0 ? closeEnd + 1 : snippet.Length;
                    continue;
                }

                var genericTagEnd = FindTagEnd(snippet, tagStart + 1);
                index = genericTagEnd >= 0 ? genericTagEnd + 1 : tagStart + 1;
            }

            return null;
        }

        private static void ParseMetaAttributes(
            string source,
            int start,
            int end,
            out string charset,
            out string httpEquiv,
            out string content)
        {
            charset = null;
            httpEquiv = null;
            content = null;

            var index = start;
            while (index < end)
            {
                SkipAsciiWhitespace(source, ref index, end);
                if (index >= end)
                    break;

                if (source[index] == '/')
                {
                    index++;
                    continue;
                }

                var nameStart = index;
                while (index < end &&
                       !IsAsciiWhitespace(source[index]) &&
                       source[index] != '=' &&
                       source[index] != '/' &&
                       source[index] != '>')
                {
                    index++;
                }

                if (index == nameStart)
                {
                    index++;
                    continue;
                }

                var name = source.Substring(nameStart, index - nameStart);
                SkipAsciiWhitespace(source, ref index, end);

                string value = string.Empty;
                if (index < end && source[index] == '=')
                {
                    index++;
                    SkipAsciiWhitespace(source, ref index, end);
                    value = ReadAttributeValue(source, ref index, end);
                }

                // HTML keeps the first occurrence of a duplicate attribute.
                if (charset == null && name.Equals("charset", StringComparison.OrdinalIgnoreCase))
                {
                    charset = value;
                }
                else if (httpEquiv == null && name.Equals("http-equiv", StringComparison.OrdinalIgnoreCase))
                {
                    httpEquiv = value;
                }
                else if (content == null && name.Equals("content", StringComparison.OrdinalIgnoreCase))
                {
                    content = value;
                }
            }
        }

        private static string ReadAttributeValue(string source, ref int index, int end)
        {
            if (index >= end)
                return string.Empty;

            if (source[index] == '"' || source[index] == '\'')
            {
                var quote = source[index++];
                var valueStart = index;
                while (index < end && source[index] != quote)
                    index++;

                var value = source.Substring(valueStart, index - valueStart);
                if (index < end && source[index] == quote)
                    index++;
                return value;
            }

            var start = index;
            while (index < end && !IsAsciiWhitespace(source[index]) && source[index] != '>')
                index++;
            return source.Substring(start, index - start);
        }

        private static int FindTagEnd(string source, int index)
        {
            char quote = '\0';
            for (var i = Math.Max(0, index); i < source.Length; i++)
            {
                var c = source[i];
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    quote = c;
                    continue;
                }

                if (c == '>')
                    return i;
            }

            return -1;
        }

        private static bool StartsWithTagName(string source, int tagStart, string tagName)
        {
            var nameStart = tagStart + 1;
            if (!StartsWithAsciiIgnoreCase(source, nameStart, tagName))
                return false;

            var end = nameStart + tagName.Length;
            if (end >= source.Length)
                return true;

            var next = source[end];
            return IsAsciiWhitespace(next) || next == '/' || next == '>';
        }

        private static bool StartsWithAsciiIgnoreCase(string source, int start, string value)
        {
            if (start < 0 || value == null || source.Length - start < value.Length)
                return false;

            for (var i = 0; i < value.Length; i++)
            {
                var left = source[start + i];
                var right = value[i];
                if (left == right)
                    continue;

                if (left >= 'A' && left <= 'Z') left = (char)(left + 0x20);
                if (right >= 'A' && right <= 'Z') right = (char)(right + 0x20);
                if (left != right)
                    return false;
            }

            return true;
        }

        private static int IndexOfAsciiIgnoreCase(string source, string value, int start)
        {
            if (string.IsNullOrEmpty(value))
                return Math.Clamp(start, 0, source.Length);

            for (var i = Math.Max(0, start); i <= source.Length - value.Length; i++)
            {
                if (StartsWithAsciiIgnoreCase(source, i, value))
                    return i;
            }

            return -1;
        }

        private static void SkipAsciiWhitespace(string source, ref int index, int end)
        {
            while (index < end && IsAsciiWhitespace(source[index]))
                index++;
        }

        private static bool IsAsciiWhitespace(char c) =>
            c is ' ' or '\t' or '\r' or '\n' or '\f';

        /// <summary>
        /// HTML's meta prescan has two mandatory remappings after label lookup:
        /// UTF-16 declarations become UTF-8 and x-user-defined becomes windows-1252.
        /// These remappings apply to in-band meta declarations, not to BOM handling.
        /// </summary>
        private static Encoding GetMetaEncodingByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var normalized = name.Trim().ToLowerInvariant();
            if (normalized is "utf-16" or "utf-16le" or "utf-16be")
                return Encoding.UTF8;
            if (normalized == "x-user-defined")
                return Windows1252;

            return GetEncodingByName(name);
        }

        /// <summary>
        /// Maps charset name to Encoding, normalizing common aliases.
        /// </summary>
        private static Encoding GetEncodingByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var normalized = name.Trim().ToLowerInvariant();

            // Common aliases per the web encoding model.
            switch (normalized)
            {
                case "utf-8":
                case "utf8":
                case "unicode-1-1-utf-8":
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
                case "ascii":
                case "us-ascii":
                    // On the web, historical ASCII/Latin1 labels are aliases for
                    // windows-1252. Encoding.ASCII would corrupt bytes above 0x7F.
                    return Windows1252;

                default:
                    try
                    {
                        var encoding = Encoding.GetEncoding(name);
                        // UTF-7 and UTF-32 are .NET encodings but not web encodings.
                        // Accepting them from attacker-controlled HTTP/meta labels can
                        // produce decoding behavior no conforming browser exposes.
                        if (encoding.CodePage is 65000 or 12000 or 12001)
                            return null;
                        return encoding;
                    }
                    catch
                    {
                        return null;
                    }
            }
        }

        /// <summary>
        /// Decodes bytes to a managed string using the detected encoding.
        /// </summary>
        public static string DecodeToUtf8(byte[] bytes, string contentTypeHeader)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));

            var encoding = DetermineEncoding(bytes, contentTypeHeader);
            return encoding.GetString(bytes);
        }
    }
}
