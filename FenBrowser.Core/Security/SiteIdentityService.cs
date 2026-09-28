using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FenBrowser.Core.Network;

namespace FenBrowser.Core.Security
{
    public readonly record struct SchemefulSite(
        string Scheme,
        string RegistrableDomainOrHost,
        string OpaqueIdentity)
    {
        public bool IsOpaque => !string.IsNullOrEmpty(OpaqueIdentity);

        public string SerializedPartitionKey =>
            IsOpaque
                ? "opaque:" + OpaqueIdentity
                : (Scheme ?? string.Empty).ToLowerInvariant() + "://" +
                  (RegistrableDomainOrHost ?? string.Empty).ToLowerInvariant();
    }

    /// <summary>
    /// The single authority for browser host canonicalization, PSL lookup,
    /// registrable-domain computation and schemeful-site identity.
    /// </summary>
    public sealed class SiteIdentityService
    {
        private const string PublicSuffixResourceName =
            "FenBrowser.Core.Security.PublicSuffix.public_suffix_list.dat";

        private readonly HashSet<string> _exactRules = new(StringComparer.Ordinal);
        private readonly HashSet<string> _wildcardRules = new(StringComparer.Ordinal);
        private readonly HashSet<string> _exceptionRules = new(StringComparer.Ordinal);
        private readonly IdnMapping _idn = new() { UseStd3AsciiRules = true };

        public static SiteIdentityService Default { get; } = LoadDefault();

        private SiteIdentityService(IEnumerable<string> rules)
        {
            foreach (var rawLine in rules)
            {
                var line = rawLine?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                if (line[0] == '!')
                {
                    var rule = CanonicalizeRule(line.Substring(1));
                    if (!string.IsNullOrEmpty(rule)) _exceptionRules.Add(rule);
                    continue;
                }

                if (line.StartsWith("*.", StringComparison.Ordinal))
                {
                    var rule = CanonicalizeRule(line.Substring(2));
                    if (!string.IsNullOrEmpty(rule)) _wildcardRules.Add(rule);
                    continue;
                }

                var exact = CanonicalizeRule(line);
                if (!string.IsNullOrEmpty(exact)) _exactRules.Add(exact);
            }

            // Never silently degrade a security boundary to a mini-list/last-two-label heuristic.
            if (_exactRules.Count < 1000)
                throw new InvalidOperationException("Bundled Public Suffix List is missing or incomplete.");
        }

        public string CanonicalizeHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return string.Empty;

            var candidate = host.Trim().TrimEnd('.');
            if (candidate.Length >= 2 && candidate[0] == '[' && candidate[^1] == ']')
                candidate = candidate.Substring(1, candidate.Length - 2);

            if (IPAddress.TryParse(candidate, out var address))
                return address.ToString().ToLowerInvariant();

            try
            {
                return _idn.GetAscii(candidate).TrimEnd('.').ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        public bool IsPublicSuffix(string host)
        {
            var canonical = CanonicalizeHost(host);
            if (string.IsNullOrEmpty(canonical) || IsIpOrLocalhost(canonical)) return false;
            return string.Equals(canonical, ComputePublicSuffix(canonical), StringComparison.Ordinal);
        }

        public string ComputePublicSuffix(string host)
        {
            var canonical = CanonicalizeHost(host);
            if (string.IsNullOrEmpty(canonical)) return string.Empty;
            if (IsIpOrLocalhost(canonical)) return canonical;

            var labels = canonical.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length == 0) return string.Empty;

            var prevailingLabelCount = 1; // implicit "*" rule
            for (var i = 0; i < labels.Length; i++)
            {
                var candidate = string.Join(".", labels, i, labels.Length - i);

                // Exception rule wins; public suffix is exception without its left-most label.
                if (_exceptionRules.Contains(candidate))
                {
                    return i + 1 < labels.Length
                        ? string.Join(".", labels, i + 1, labels.Length - i - 1)
                        : string.Empty;
                }

                if (_exactRules.Contains(candidate))
                    prevailingLabelCount = Math.Max(prevailingLabelCount, labels.Length - i);

                if (i + 1 < labels.Length)
                {
                    var wildcardSuffix = string.Join(".", labels, i + 1, labels.Length - i - 1);
                    if (_wildcardRules.Contains(wildcardSuffix))
                        prevailingLabelCount = Math.Max(prevailingLabelCount, labels.Length - i);
                }
            }

            return string.Join(".", labels, labels.Length - prevailingLabelCount, prevailingLabelCount);
        }

        public string ComputeRegistrableDomain(string host)
        {
            var canonical = CanonicalizeHost(host);
            if (string.IsNullOrEmpty(canonical)) return string.Empty;
            if (IsIpOrLocalhost(canonical)) return canonical;

            var suffix = ComputePublicSuffix(canonical);
            if (string.IsNullOrEmpty(suffix) ||
                string.Equals(canonical, suffix, StringComparison.Ordinal))
                return string.Empty;

            var labels = canonical.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var suffixLabels = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
            if (labels.Length <= suffixLabels) return string.Empty;

            return string.Join(
                ".",
                labels,
                labels.Length - suffixLabels - 1,
                suffixLabels + 1);
        }

        public SchemefulSite CreateSchemefulSite(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return CreateOpaqueSite(string.Empty);

            var parsed = WhatwgUrl.Parse(url);
            if (parsed == null) return CreateOpaqueSite(url);

            var origin = parsed.ComputeOrigin();
            if (origin.Kind == UrlOriginKind.Opaque) return CreateOpaqueSite(url);

            return CreateNetworkSite(parsed.Scheme, parsed.Hostname, url);
        }

        public SchemefulSite CreateSchemefulSite(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
                return CreateOpaqueSite(uri?.ToString() ?? string.Empty);

            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
            {
                return CreateNetworkSite(uri.Scheme, uri.IdnHost, uri.AbsoluteUri);
            }

            return CreateOpaqueSite(uri.AbsoluteUri);
        }

        public bool CompareSameSite(Uri left, Uri right)
        {
            if (left == null || right == null) return false;
            var a = CreateSchemefulSite(left);
            var b = CreateSchemefulSite(right);
            if (a.IsOpaque || b.IsOpaque) return false;

            return string.Equals(
                a.SerializedPartitionKey,
                b.SerializedPartitionKey,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Generates renderer assignment keys without broad opaque/file buckets.
        /// Tuple origins use the serialized origin in strict mode; opaque/non-network
        /// URLs use a full-URL fingerprint, which deliberately over-isolates rather
        /// than co-locating unrelated content.
        /// </summary>
        public bool TryCreateRendererAssignmentKey(string url, bool strictOrigin, out string key)
        {
            key = string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return false;

            var parsed = WhatwgUrl.Parse(url);
            if (parsed == null) return false;

            var origin = parsed.ComputeOrigin();
            if (strictOrigin && origin.Kind != UrlOriginKind.Opaque)
            {
                key = "origin:" + origin.Serialize().ToLowerInvariant();
                return true;
            }

            // HTML "obtain a site" works from the origin, not the URL: a blob: URL's origin
            // is its creator's (URL 6.2), so a page's own blob URL belongs to the page's site.
            // Deriving the site from the raw URL made it opaque, and site-per-process moved it
            // into a renderer of its own.
            var site = origin.Kind == UrlOriginKind.Opaque
                ? CreateSchemefulSite(url)
                : CreateSchemefulSite(origin.Serialize());
            key = strictOrigin && site.IsOpaque
                ? "origin-opaque:" + Fingerprint(url)
                : "site:" + site.SerializedPartitionKey;
            return true;
        }

        private SchemefulSite CreateNetworkSite(string scheme, string host, string originalUrl)
        {
            var canonical = CanonicalizeHost(host);
            if (string.IsNullOrEmpty(canonical)) return CreateOpaqueSite(originalUrl);

            var registrable = ComputeRegistrableDomain(canonical);
            var siteHost = string.IsNullOrEmpty(registrable) ? canonical : registrable;
            return new SchemefulSite(
                (scheme ?? string.Empty).ToLowerInvariant(),
                siteHost,
                string.Empty);
        }

        private SchemefulSite CreateOpaqueSite(string serializedUrl) =>
            new(string.Empty, string.Empty, Fingerprint(serializedUrl ?? string.Empty));

        private string CanonicalizeRule(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule)) return string.Empty;
            try
            {
                return _idn.GetAscii(rule.Trim().TrimEnd('.')).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private static bool IsIpOrLocalhost(string host)
        {
            if (IPAddress.TryParse(host, out _)) return true;
            return string.Equals(host, "localhost", StringComparison.Ordinal) ||
                   host.EndsWith(".localhost", StringComparison.Ordinal);
        }

        private static string Fingerprint(string value)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        private static SiteIdentityService LoadDefault()
        {
            var assembly = typeof(SiteIdentityService).Assembly;
            using var stream = assembly.GetManifestResourceStream(PublicSuffixResourceName)
                ?? throw new InvalidOperationException(
                    $"Required embedded Public Suffix List '{PublicSuffixResourceName}' was not found.");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return new SiteIdentityService(lines);
        }
    }
}
