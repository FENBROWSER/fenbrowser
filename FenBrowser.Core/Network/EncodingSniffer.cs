using System;
using System.Collections.Generic;
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
        private static readonly IReadOnlyDictionary<string, int> WebEncodingCodePages = BuildWebEncodingCodePages();
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

                // CSS Syntax defines UTF-8 as the fallback encoding for a stylesheet.
                // The legacy Windows-1252 fallback below is for HTML documents and
                // corrupts non-ASCII generated content when text/css omits charset.
                if (contentTypeHeader.TrimStart().StartsWith("text/css", StringComparison.OrdinalIgnoreCase))
                    return Encoding.UTF8;
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
        /// Maps a WHATWG web-encoding label to a known decoder. Labels are closed over
        /// the browser registry: attacker-controlled names never reach the host's
        /// Encoding.GetEncoding(string) resolver, so behavior cannot vary with OS or
        /// installed code-page aliases.
        /// </summary>
        private static Encoding GetEncodingByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var normalized = name.Trim().ToLowerInvariant();
            if (normalized is "utf-8" or "utf8" or "unicode-1-1-utf-8")
                return Encoding.UTF8;
            if (normalized is "utf-16" or "utf-16le")
                return Encoding.Unicode;
            if (normalized == "utf-16be")
                return Encoding.BigEndianUnicode;
            if (normalized == "x-user-defined")
                return XUserDefinedEncoding.Instance;

            // The Encoding Standard's replacement labels intentionally do not expose
            // their historical stateful decoders to the web. Treat them as unusable
            // declarations and continue normal sniffing/fallback.
            if (normalized is "csiso2022kr" or "hz-gb-2312" or "iso-2022-cn" or
                "iso-2022-cn-ext" or "iso-2022-kr" or "replacement")
            {
                return null;
            }

            if (!WebEncodingCodePages.TryGetValue(normalized, out var codePage))
                return null;

            try
            {
                return Encoding.GetEncoding(codePage);
            }
            catch (ArgumentException)
            {
                // CodePagesEncodingProvider is registered in the type initializer, but
                // fail closed if a deployment cannot provide one of our explicit pages.
                return null;
            }
        }

        private static IReadOnlyDictionary<string, int> BuildWebEncodingCodePages()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);

            static void Add(Dictionary<string, int> target, int codePage, params string[] labels)
            {
                foreach (var label in labels)
                    target[label] = codePage;
            }

            Add(map, 866, "866", "cp866", "csibm866", "ibm866");
            Add(map, 28592, "csisolatin2", "iso-8859-2", "iso-ir-101", "iso8859-2", "iso88592", "iso_8859-2", "iso_8859-2:1987", "l2", "latin2");
            Add(map, 28593, "csisolatin3", "iso-8859-3", "iso-ir-109", "iso8859-3", "iso88593", "iso_8859-3", "iso_8859-3:1988", "l3", "latin3");
            Add(map, 28594, "csisolatin4", "iso-8859-4", "iso-ir-110", "iso8859-4", "iso88594", "iso_8859-4", "iso_8859-4:1988", "l4", "latin4");
            Add(map, 28595, "csisolatincyrillic", "cyrillic", "iso-8859-5", "iso-ir-144", "iso8859-5", "iso88595", "iso_8859-5", "iso_8859-5:1988");
            Add(map, 28596, "arabic", "asmo-708", "csiso88596e", "csiso88596i", "csisolatinarabic", "ecma-114", "iso-8859-6", "iso-8859-6-e", "iso-8859-6-i", "iso-ir-127", "iso8859-6", "iso88596", "iso_8859-6", "iso_8859-6:1987");
            Add(map, 28597, "csisolatingreek", "ecma-118", "elot_928", "greek", "greek8", "iso-8859-7", "iso-ir-126", "iso8859-7", "iso88597", "iso_8859-7", "iso_8859-7:1987", "sun_eu_greek");
            Add(map, 28598, "csiso88598e", "csisolatinhebrew", "hebrew", "iso-8859-8", "iso-8859-8-e", "iso-ir-138", "iso8859-8", "iso88598", "iso_8859-8", "iso_8859-8:1988", "visual");
            Add(map, 38598, "csiso88598i", "iso-8859-8-i", "logical");
            Add(map, 28590, "csisolatin6", "iso-8859-10", "iso-ir-157", "iso8859-10", "iso885910", "l6", "latin6");
            Add(map, 28603, "iso-8859-13", "iso8859-13", "iso885913");
            Add(map, 28604, "iso-8859-14", "iso8859-14", "iso885914");
            Add(map, 28605, "csisolatin9", "iso-8859-15", "iso8859-15", "iso885915", "iso_8859-15", "l9", "latin9");
            Add(map, 28606, "iso-8859-16");
            Add(map, 20866, "cskoi8r", "koi", "koi8", "koi8-r", "koi8_r");
            Add(map, 21866, "koi8-ru", "koi8-u");
            Add(map, 10000, "csmacintosh", "mac", "macintosh", "x-mac-roman");
            Add(map, 874, "dos-874", "iso-8859-11", "iso8859-11", "iso885911", "tis-620", "windows-874");
            Add(map, 1250, "cp1250", "windows-1250", "x-cp1250");
            Add(map, 1251, "cp1251", "windows-1251", "x-cp1251");
            Add(map, 1252, "ansi_x3.4-1968", "ascii", "cp1252", "cp819", "csisolatin1", "ibm819", "iso-8859-1", "iso-ir-100", "iso8859-1", "iso8859-1:1987", "iso88591", "iso_8859-1", "iso_8859-1:1987", "l1", "latin1", "us-ascii", "windows-1252", "x-cp1252");
            Add(map, 1253, "cp1253", "windows-1253", "x-cp1253");
            Add(map, 1254, "cp1254", "csisolatin5", "iso-8859-9", "iso-ir-148", "iso8859-9", "iso88599", "iso_8859-9", "iso_8859-9:1989", "l5", "latin5", "windows-1254", "x-cp1254");
            Add(map, 1255, "cp1255", "windows-1255", "x-cp1255");
            Add(map, 1256, "cp1256", "windows-1256", "x-cp1256");
            Add(map, 1257, "cp1257", "windows-1257", "x-cp1257");
            Add(map, 1258, "cp1258", "windows-1258", "x-cp1258");
            Add(map, 10007, "x-mac-cyrillic", "x-mac-ukrainian");
            Add(map, 936, "chinese", "csgb2312", "csiso58gb231280", "gb2312", "gb_2312", "gb_2312-80", "gbk", "iso-ir-58", "x-gbk");
            Add(map, 54936, "gb18030");
            Add(map, 950, "big5", "big5-hkscs", "cn-big5", "csbig5", "x-x-big5");
            Add(map, 51932, "cseucpkdfmtjapanese", "euc-jp", "x-euc-jp");
            Add(map, 50220, "csiso2022jp", "iso-2022-jp");
            Add(map, 932, "csshiftjis", "ms932", "ms_kanji", "shift-jis", "shift_jis", "sjis", "windows-31j", "x-sjis");
            Add(map, 51949, "cseuckr", "csksc56011987", "euc-kr", "iso-ir-149", "korean", "ks_c_5601-1987", "ks_c_5601-1989", "ksc5601", "ksc_5601", "windows-949");

            return map;
        }

        private sealed class XUserDefinedEncoding : Encoding
        {
            public static readonly XUserDefinedEncoding Instance = new XUserDefinedEncoding();

            public override int GetByteCount(char[] chars, int index, int count) => count;

            public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
            {
                if (chars == null) throw new ArgumentNullException(nameof(chars));
                if (bytes == null) throw new ArgumentNullException(nameof(bytes));
                for (var i = 0; i < charCount; i++)
                {
                    var c = chars[charIndex + i];
                    bytes[byteIndex + i] = c < 0x80
                        ? (byte)c
                        : c >= 0xF780 && c <= 0xF7FF
                            ? (byte)(0x80 + c - 0xF780)
                            : (byte)'?';
                }
                return charCount;
            }

            public override int GetCharCount(byte[] bytes, int index, int count) => count;

            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            {
                if (bytes == null) throw new ArgumentNullException(nameof(bytes));
                if (chars == null) throw new ArgumentNullException(nameof(chars));
                for (var i = 0; i < byteCount; i++)
                {
                    var b = bytes[byteIndex + i];
                    chars[charIndex + i] = b < 0x80 ? (char)b : (char)(0xF780 + b - 0x80);
                }
                return byteCount;
            }

            public override int GetMaxByteCount(int charCount) => charCount;
            public override int GetMaxCharCount(int byteCount) => byteCount;
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
