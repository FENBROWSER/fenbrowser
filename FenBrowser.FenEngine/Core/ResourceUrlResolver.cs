using System;
using System.Text.RegularExpressions;

namespace FenBrowser.FenEngine.Core
{
    public static class ResourceUrlResolver
    {
        public static string Resolve(string url, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            url = url.Trim();

            if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
                return url;

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return url;

            if (string.IsNullOrWhiteSpace(baseUrl))
                return url;

            try
            {
                if (url.StartsWith("//", StringComparison.Ordinal))
                {
                    var baseUri = new Uri(baseUrl, UriKind.Absolute);
                    var scheme = baseUri.Scheme;
                    if (string.IsNullOrWhiteSpace(scheme))
                        scheme = "https";
                    return scheme + ":" + url;
                }

                if (url.StartsWith("/", StringComparison.Ordinal))
                {
                    var baseUri = new Uri(baseUrl, UriKind.Absolute);
                    var builder = new UriBuilder(baseUri)
                    {
                        Path = url,
                        Query = null,
                        Fragment = null
                    };
                    return builder.Uri.ToString();
                }

                var baseUri2 = new Uri(baseUrl, UriKind.Absolute);
                if (Uri.TryCreate(baseUri2, url, out var result))
                    return result.ToString();
            }
            catch { }

            return url;
        }

        public static string ExtractUrlFromCss(string cssValue)
        {
            if (string.IsNullOrWhiteSpace(cssValue))
                return null;

            var match = Regex.Match(cssValue, @"url\(['""']?(?<u>[^)'""]+)['""']?\)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["u"].Value.Trim();

            return null;
        }

        public static bool IsAbsoluteUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            return Uri.IsWellFormedUriString(url.Trim(), UriKind.Absolute);
        }

        public static bool IsDataUri(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            return url.Trim().StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        }
    }
}
