using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FenBrowser.Core.Network;

/// <summary>
/// Byte-oriented parser for data: URLs. Parsing and percent/base64 decoding are
/// centralized here so text and binary resource paths observe identical bytes.
/// </summary>
internal static class DataUrlParser
{
    internal sealed class Result
    {
        public required byte[] Bytes { get; init; }
        public required string ContentType { get; init; }
        public bool IsBase64 { get; init; }

        public string DecodeText() => EncodingSniffer.DecodeToUtf8(Bytes, ContentType);
    }

    public static bool TryParse(Uri uri, int maxDecodedBytes, out Result result, out string error)
    {
        result = null;
        error = null;

        if (uri == null || !uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
        {
            error = "Not a data URL.";
            return false;
        }

        if (maxDecodedBytes <= 0)
        {
            error = "Decoded byte limit must be positive.";
            return false;
        }

        var source = uri.OriginalString;
        if (source.Length < 5 || !source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            error = "Malformed data URL scheme.";
            return false;
        }

        var comma = source.IndexOf(',', 5);
        if (comma < 0)
        {
            error = "Data URL is missing the metadata/data separator.";
            return false;
        }

        var metadata = source.Substring(5, comma - 5);
        var payload = source.AsSpan(comma + 1);

        if (!TryParseMetadata(metadata, out var contentType, out var isBase64, out error))
        {
            return false;
        }

        // Percent encoding can expand source text roughly 3:1 relative to bytes.
        // Bound encoded input before allocating/decoding so a pathological URL cannot
        // bypass the caller's resource-body limit merely by using %HH sequences.
        var maxEncodedBytes = checked((long)maxDecodedBytes * 4L + 4096L);
        if (payload.Length > maxEncodedBytes)
        {
            error = "Encoded data URL payload exceeds the configured body limit.";
            return false;
        }

        var percentDecodeLimit = isBase64
            ? (int)Math.Min(int.MaxValue, maxEncodedBytes)
            : maxDecodedBytes;
        if (!TryPercentDecode(payload, percentDecodeLimit, out var rawBytes, out error))
        {
            return false;
        }

        byte[] decodedBytes;
        if (isBase64)
        {
            for (var i = 0; i < rawBytes.Length; i++)
            {
                if (rawBytes[i] > 0x7F)
                {
                    error = "Base64 data URL payload contains non-ASCII bytes.";
                    return false;
                }
            }

            try
            {
                decodedBytes = Convert.FromBase64String(Encoding.ASCII.GetString(rawBytes));
            }
            catch (FormatException)
            {
                error = "Invalid Base64 data URL payload.";
                return false;
            }

            if (decodedBytes.Length > maxDecodedBytes)
            {
                error = "Decoded data URL payload exceeds the configured body limit.";
                return false;
            }
        }
        else
        {
            decodedBytes = rawBytes;
        }

        result = new Result
        {
            Bytes = decodedBytes,
            ContentType = contentType,
            IsBase64 = isBase64
        };
        return true;
    }

    private static bool TryParseMetadata(
        string metadata,
        out string contentType,
        out bool isBase64,
        out string error)
    {
        contentType = "text/plain;charset=US-ASCII";
        isBase64 = false;
        error = null;

        var parts = SplitMetadata(metadata ?? string.Empty);
        var base64Index = -1;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(parts[i]))
            {
                continue;
            }

            if (string.Equals(parts[i].Trim(), "base64", StringComparison.OrdinalIgnoreCase))
            {
                base64Index = i;
            }
            break;
        }

        if (base64Index >= 0)
        {
            isBase64 = true;
            parts.RemoveAt(base64Index);
        }

        string mediaType = null;
        var parameters = new List<string>();
        if (parts.Count > 0)
        {
            var first = parts[0].Trim();
            if (!string.IsNullOrEmpty(first))
            {
                mediaType = first;
            }

            for (var i = 1; i < parts.Count; i++)
            {
                var parameter = parts[i].Trim();
                if (!string.IsNullOrEmpty(parameter))
                {
                    parameters.Add(parameter);
                }
            }
        }

        if (string.IsNullOrEmpty(mediaType))
        {
            mediaType = "text/plain";
            if (!ContainsCharsetParameter(parameters))
            {
                parameters.Insert(0, "charset=US-ASCII");
            }
        }

        contentType = parameters.Count == 0
            ? mediaType
            : mediaType + ";" + string.Join(";", parameters);
        return true;
    }

    private static List<string> SplitMetadata(string metadata)
    {
        var parts = new List<string>();
        var start = 0;
        char quote = '\0';
        for (var i = 0; i < metadata.Length; i++)
        {
            var c = metadata[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            if (c == ';')
            {
                parts.Add(metadata.Substring(start, i - start));
                start = i + 1;
            }
        }

        parts.Add(metadata.Substring(start));
        return parts;
    }

    private static bool ContainsCharsetParameter(IReadOnlyList<string> parameters)
    {
        foreach (var parameter in parameters)
        {
            var equals = parameter.IndexOf('=');
            var name = equals >= 0 ? parameter.Substring(0, equals) : parameter;
            if (string.Equals(name.Trim(), "charset", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryPercentDecode(
        ReadOnlySpan<char> input,
        int maxBytes,
        out byte[] bytes,
        out string error)
    {
        bytes = null;
        error = null;

        using var output = new MemoryStream(Math.Min(input.Length, Math.Min(maxBytes, 64 * 1024)));
        var surrogateBuffer = new char[2];
        var utf8Buffer = new byte[4];
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '%')
            {
                if (i + 2 >= input.Length || !TryHex(input[i + 1], out var hi) || !TryHex(input[i + 2], out var lo))
                {
                    error = "Malformed percent escape in data URL payload.";
                    return false;
                }

                if (output.Length >= maxBytes)
                {
                    error = "Decoded data URL payload exceeds the configured body limit.";
                    return false;
                }

                output.WriteByte((byte)((hi << 4) | lo));
                i += 2;
                continue;
            }

            if (c <= 0x7F)
            {
                if (output.Length >= maxBytes)
                {
                    error = "Decoded data URL payload exceeds the configured body limit.";
                    return false;
                }

                output.WriteByte((byte)c);
                continue;
            }

            var charCount = 1;
            surrogateBuffer[0] = c;
            if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                surrogateBuffer[1] = input[++i];
                charCount = 2;
            }

            var written = Encoding.UTF8.GetBytes(
                surrogateBuffer.AsSpan(0, charCount),
                utf8Buffer.AsSpan());
            if (output.Length > maxBytes - written)
            {
                error = "Decoded data URL payload exceeds the configured body limit.";
                return false;
            }

            output.Write(utf8Buffer, 0, written);
        }

        bytes = output.ToArray();
        return true;
    }

    private static bool TryHex(char c, out int value)
    {
        if (c is >= '0' and <= '9')
        {
            value = c - '0';
            return true;
        }
        if (c is >= 'a' and <= 'f')
        {
            value = c - 'a' + 10;
            return true;
        }
        if (c is >= 'A' and <= 'F')
        {
            value = c - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }
}
