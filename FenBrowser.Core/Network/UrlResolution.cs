using System;

namespace FenBrowser.Core.Network
{
    /// <summary>
    /// Resolution of page-supplied URLs where code works in <see cref="Uri"/> rather than
    /// <see cref="WhatwgUrl"/>. <c>Uri.TryCreate(input, UriKind.Absolute)</c> is not the
    /// WHATWG URL parser's notion of "has a scheme": on Windows it accepts
    /// <c>//host/path</c> and <c>\\host\path</c> as UNC paths and on Unix it accepts
    /// <c>/path</c>, all as <c>file:</c> URLs. A protocol-relative URL on a web page,
    /// <c>//fonts.googleapis.com/css2?family=Roboto</c>, therefore became
    /// <c>file://fonts.googleapis.com/css2%3Ffamily=Roboto</c> instead of taking the
    /// page's scheme (WHATWG URL 4.4, "scheme-relative-special-URL" / "relative slash").
    /// </summary>
    public static class UrlResolution
    {
        /// <summary>
        /// A drop-in for <c>Uri.TryCreate(input, UriKind.Absolute, out uri)</c> that only
        /// succeeds for input that carries its own scheme. Scheme-relative input and an
        /// implicit file path are left for resolution against a base.
        /// </summary>
        public static bool TryParseAbsolute(string input, out Uri uri)
        {
            uri = null;
            var trimmed = TrimControlAndSpace(input);
            if (trimmed.Length == 0 || StartsWithTwoSlashes(trimmed))
            {
                return false;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
            {
                return false;
            }

            if (parsed.IsFile && !trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                // "C:\x", "\\host\share" or (on Unix) "/x": a path, not a URL with a scheme.
                return false;
            }

            uri = parsed;
            return true;
        }

        /// <summary>
        /// Parses <paramref name="input"/> with <paramref name="baseUri"/> as its base URL:
        /// absolute input as is, anything else relative to the base. For a special base
        /// scheme a backslash in the leading slashes counts as a slash, as the URL parser
        /// treats it.
        /// </summary>
        public static bool TryResolve(string input, Uri baseUri, out Uri uri)
        {
            if (TryParseAbsolute(input, out uri))
            {
                return true;
            }

            uri = null;
            if (input == null || baseUri == null || !baseUri.IsAbsoluteUri)
            {
                return false;
            }

            var relative = TrimControlAndSpace(input);
            if (IsSpecialScheme(baseUri.Scheme))
            {
                relative = NormalizeLeadingSlashes(relative);
            }

            if (!Uri.TryCreate(baseUri, relative, out var resolved))
            {
                return false;
            }

            if (resolved.IsFile && !baseUri.IsFile)
            {
                // Uri still read the input as a path of its own; a web base never yields one.
                return false;
            }

            uri = resolved;
            return true;
        }

        private static string TrimControlAndSpace(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            var start = 0;
            var end = input.Length;
            while (start < end && input[start] <= ' ')
            {
                start++;
            }

            while (end > start && input[end - 1] <= ' ')
            {
                end--;
            }

            return start == 0 && end == input.Length ? input : input.Substring(start, end - start);
        }

        private static bool StartsWithTwoSlashes(string input) =>
            input.Length >= 2 && IsSlash(input[0]) && IsSlash(input[1]);

        private static bool IsSlash(char c) => c == '/' || c == '\\';

        private static string NormalizeLeadingSlashes(string input)
        {
            var count = 0;
            while (count < input.Length && IsSlash(input[count]))
            {
                count++;
            }

            return count == 0 || input.IndexOf('\\', 0, count) < 0
                ? input
                : new string('/', count) + input.Substring(count);
        }

        private static bool IsSpecialScheme(string scheme) =>
            scheme is "http" or "https" or "ws" or "wss" or "ftp" or "file";
    }
}
