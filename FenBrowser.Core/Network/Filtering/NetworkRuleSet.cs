using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace FenBrowser.Core.Network.Filtering
{
    public sealed record NetworkFilterRule(string Domain, IReadOnlyCollection<string> Destinations = null, string PathPrefix = null);

    public sealed class NetworkRuleSet
    {
        private readonly DomainNode _root;

        public NetworkRuleSet(long version, DateTimeOffset generatedAtUtc, DateTimeOffset? expiresAtUtc, IEnumerable<NetworkFilterRule> rules)
        {
            if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
            Version = version;
            GeneratedAtUtc = generatedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
            _root = Compile(rules ?? throw new ArgumentNullException(nameof(rules)));
        }

        public long Version { get; }
        public DateTimeOffset GeneratedAtUtc { get; }
        public DateTimeOffset? ExpiresAtUtc { get; }
        public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is { } expiry && expiry <= now;

        public bool Matches(Uri uri, string destination = null)
        {
            if (uri == null || !uri.IsAbsoluteUri) return false;

            var host = NormalizeDomain(uri.IdnHost);
            if (host.Length == 0) return false;

            var node = _root;
            var labelEnd = host.Length;
            while (labelEnd > 0)
            {
                var separator = host.LastIndexOf('.', labelEnd - 1);
                var label = host.Substring(separator + 1, labelEnd - separator - 1);
                if (!node.Children.TryGetValue(label, out node)) return false;
                if (MatchesRules(node.Rules, uri.AbsolutePath, destination)) return true;
                labelEnd = separator < 0 ? 0 : separator;
            }

            return false;
        }

        public static NetworkRuleSet Parse(ReadOnlySpan<byte> utf8Json)
        {
            var document = JsonSerializer.Deserialize<RuleSetDocument>(utf8Json, SerializerOptions)
                ?? throw new JsonException("Ruleset document is empty.");
            if (document.Rules == null) throw new JsonException("Ruleset does not contain rules.");
            if (document.Rules.Count > 250_000) throw new JsonException("Ruleset exceeds the rule limit.");

            var rules = new List<NetworkFilterRule>(document.Rules.Count);
            foreach (var rule in document.Rules)
            {
                rules.Add(new NetworkFilterRule(rule.Domain, rule.Destinations, rule.PathPrefix));
            }

            return new NetworkRuleSet(document.Version, document.GeneratedAtUtc, document.ExpiresAtUtc, rules);
        }

        private static DomainNode Compile(IEnumerable<NetworkFilterRule> rules)
        {
            var root = new MutableDomainNode();
            var count = 0;
            foreach (var rule in rules)
            {
                if (++count > 250_000) throw new ArgumentException("Ruleset exceeds the rule limit.", nameof(rules));
                var domain = NormalizeDomain(rule.Domain);
                if (!IsValidDomain(domain)) throw new ArgumentException($"Invalid rule domain: {rule.Domain}", nameof(rules));

                var node = root;
                var labels = domain.Split('.');
                for (var index = labels.Length - 1; index >= 0; index--)
                {
                    if (!node.Children.TryGetValue(labels[index], out var child))
                    {
                        child = new MutableDomainNode();
                        node.Children.Add(labels[index], child);
                    }
                    node = child;
                }

                var destinations = rule.Destinations == null
                    ? ImmutableHashSet<string>.Empty
                    : rule.Destinations.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                var pathPrefix = string.IsNullOrWhiteSpace(rule.PathPrefix) ? null : rule.PathPrefix.Trim();
                if (pathPrefix != null && !pathPrefix.StartsWith("/", StringComparison.Ordinal))
                    throw new ArgumentException("A rule path prefix must start with '/'.", nameof(rules));
                node.Rules.Add(new CompiledRule(destinations, pathPrefix));
            }

            return Freeze(root);
        }

        private static DomainNode Freeze(MutableDomainNode node)
        {
            var children = ImmutableDictionary.CreateBuilder<string, DomainNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in node.Children) children.Add(pair.Key, Freeze(pair.Value));
            return new DomainNode(children.ToImmutable(), node.Rules.ToImmutableArray());
        }

        private static bool MatchesRules(ImmutableArray<CompiledRule> rules, string path, string destination)
        {
            foreach (var rule in rules)
            {
                if (rule.Destinations.Count != 0 && (destination == null || !rule.Destinations.Contains(destination))) continue;
                if (rule.PathPrefix != null && !PathPrefixMatches(path, rule.PathPrefix)) continue;
                return true;
            }
            return false;
        }

        private static bool PathPrefixMatches(string path, string prefix)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return path.Length == prefix.Length || prefix.EndsWith("/", StringComparison.Ordinal) || path[prefix.Length] == '/';
        }

        private static string NormalizeDomain(string domain) => (domain ?? string.Empty).Trim().Trim('.').ToLowerInvariant();

        private static bool IsValidDomain(string domain)
        {
            if (domain.Length is 0 or > 253 || domain.Contains("..", StringComparison.Ordinal)) return false;
            foreach (var label in domain.Split('.'))
            {
                if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
                foreach (var character in label)
                {
                    if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
                }
            }
            return true;
        }

        private sealed record CompiledRule(ImmutableHashSet<string> Destinations, string PathPrefix);
        private sealed record DomainNode(ImmutableDictionary<string, DomainNode> Children, ImmutableArray<CompiledRule> Rules);
        private sealed class MutableDomainNode
        {
            public Dictionary<string, MutableDomainNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<CompiledRule> Rules { get; } = new();
        }

        private sealed class RuleSetDocument
        {
            public long Version { get; set; }
            public DateTimeOffset GeneratedAtUtc { get; set; }
            public DateTimeOffset? ExpiresAtUtc { get; set; }
            public List<RuleDocument> Rules { get; set; }
        }

        private sealed class RuleDocument
        {
            public string Domain { get; set; }
            public List<string> Destinations { get; set; }
            public string PathPrefix { get; set; }
        }

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            MaxDepth = 16
        };
    }

    public sealed class SignedNetworkRuleSetStore
    {
        private readonly object _updateLock = new();
        private NetworkRuleSet _current;
        private NetworkRuleSet _previous;

        public SignedNetworkRuleSetStore(NetworkRuleSet builtIn)
        {
            _current = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        }

        public NetworkRuleSet Current => Volatile.Read(ref _current);

        public bool TryInstall(ReadOnlySpan<byte> utf8Json, ReadOnlySpan<byte> signature, ECDsa verifier, DateTimeOffset now, out string error)
        {
            if (verifier == null) throw new ArgumentNullException(nameof(verifier));
            if (!verifier.VerifyData(utf8Json, signature, HashAlgorithmName.SHA256))
            {
                error = "Ruleset signature validation failed.";
                return false;
            }

            NetworkRuleSet candidate;
            try { candidate = NetworkRuleSet.Parse(utf8Json); }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                error = ex.Message;
                return false;
            }

            lock (_updateLock)
            {
                var current = _current;
                if (candidate.Version <= current.Version)
                {
                    error = "Ruleset version must increase monotonically.";
                    return false;
                }
                if (candidate.GeneratedAtUtc > now.AddMinutes(10) || candidate.IsExpired(now))
                {
                    error = "Ruleset validity window is not acceptable.";
                    return false;
                }
                _previous = current;
                Volatile.Write(ref _current, candidate);
            }

            error = null;
            return true;
        }

        public bool TryRollback()
        {
            lock (_updateLock)
            {
                if (_previous == null) return false;
                var replacement = _previous;
                _previous = _current;
                Volatile.Write(ref _current, replacement);
                return true;
            }
        }
    }
}
