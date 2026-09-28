using System;
using System.Security.Cryptography;
using System.Text;

namespace FenBrowser.Core.Logging;

/// <summary>
/// Privacy-safe rendering of URLs for log messages. Paths, query strings,
/// fragments and user info can carry session tokens and personal data, and a
/// data: URL carries the resource itself, so none of them reach a log line.
/// Network URLs keep their origin, which is what diagnosis usually needs, plus
/// a short hash of the full URL so repeated events about one resource still
/// correlate.
/// </summary>
public static class LogUrl
{
    internal const int HashHexChars = 8;
    private const int MaxMediaTypeChars = 64;

    public static string Describe(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "(none)";
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DescribeData(url);
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
            ? Describe(uri)
            : $"(relative #{Hash(url)})";
    }

    public static string Describe(Uri uri)
    {
        if (uri == null)
        {
            return "(none)";
        }

        if (!uri.IsAbsoluteUri)
        {
            return $"(relative #{Hash(uri.OriginalString)})";
        }

        string scheme = uri.Scheme.ToLowerInvariant();
        if (scheme == "data")
        {
            return DescribeData(uri.OriginalString);
        }

        if (scheme is "http" or "https" or "ws" or "wss" or "ftp")
        {
            string origin = uri.IsDefaultPort
                ? $"{scheme}://{uri.IdnHost.ToLowerInvariant()}"
                : $"{scheme}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";
            return $"{origin} #{Hash(uri.AbsoluteUri)}";
        }

        return $"{scheme}: #{Hash(uri.OriginalString)}";
    }

    private static string DescribeData(string url)
    {
        int comma = url.IndexOf(',');
        string header = comma > 5 ? url.Substring(5, comma - 5) : string.Empty;
        int semicolon = header.IndexOf(';');
        string mediaType = semicolon >= 0 ? header.Substring(0, semicolon) : header;

        var safe = new StringBuilder(Math.Min(mediaType.Length, MaxMediaTypeChars));
        foreach (char ch in mediaType)
        {
            if (safe.Length == MaxMediaTypeChars) break;
            if (char.IsAsciiLetterOrDigit(ch) || ch is '/' or '+' or '-' or '.')
            {
                safe.Append(char.ToLowerInvariant(ch));
            }
        }

        return $"data:{(safe.Length == 0 ? "unknown" : safe.ToString())} ({url.Length} chars)";
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, HashHexChars / 2)
            .ToLowerInvariant();
}
