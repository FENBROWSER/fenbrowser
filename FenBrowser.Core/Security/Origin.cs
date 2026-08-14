using System;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// Represents a web origin per HTML spec §7.5.
    /// An origin is (scheme, host, port) tuple, or an opaque origin with identity.
    /// </summary>
    public sealed class Origin : IEquatable<Origin>
    {
        public string Scheme { get; }
        public string Host { get; }
        public int Port { get; }
        public bool IsOpaque { get; }

        private Origin()
        {
            IsOpaque = true;
        }

        public Origin(string scheme, string host, int port)
        {
            Scheme = scheme?.ToLowerInvariant() ?? "";
            Host = host?.ToLowerInvariant() ?? "";
            Port = port;
            IsOpaque = false;
        }

        /// <summary>Create a fresh opaque origin (for data: URLs, sandboxed documents, etc.).</summary>
        public static Origin Opaque() => new Origin();

        /// <summary>Derive an origin from a URI when the URI itself carries enough origin information.</summary>
        public static Origin FromUri(Uri uri)
        {
            if (uri == null) return Opaque();

            var scheme = uri.Scheme?.ToLowerInvariant();

            // A blob URL inherits the origin encoded by its inner URL. Treating every
            // blob: URL as opaque breaks same-origin blob fetches, workers, images and
            // object URLs created by the current document.
            if (scheme == "blob")
            {
                var serialized = uri.OriginalString ?? uri.AbsoluteUri;
                const string blobPrefix = "blob:";
                if (serialized.StartsWith(blobPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var inner = serialized.Substring(blobPrefix.Length);
                    if (Uri.TryCreate(inner, UriKind.Absolute, out var innerUri))
                    {
                        return FromUri(innerUri);
                    }
                }

                return Opaque();
            }

            // These URLs either have a unique opaque origin or require a creator/base
            // document to determine inheritance. FromUri has no creator context, so it
            // must not manufacture a tuple origin for them.
            if (scheme == "data" || scheme == "javascript" || scheme == "about")
                return Opaque();

            int port = uri.Port;
            if (port == -1) port = GetDefaultPort(scheme);
            return new Origin(scheme, uri.Host, port);
        }

        /// <summary>
        /// Same-origin check per HTML spec §7.5.
        /// Tuple origins compare by scheme/host/port. Opaque origins carry identity:
        /// the same opaque origin object is same-origin with itself, while a separately
        /// created opaque origin is distinct.
        /// </summary>
        public bool IsSameOrigin(Origin other)
        {
            if (other == null) return false;
            if (IsOpaque || other.IsOpaque)
                return IsOpaque && other.IsOpaque && ReferenceEquals(this, other);

            return string.Equals(Scheme, other.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase) &&
                   Port == other.Port;
        }

        /// <summary>
        /// Same-origin-domain check (considers document.domain relaxation).
        /// </summary>
        public bool IsSameOriginDomain(Origin other)
        {
            // For now, same as IsSameOrigin (document.domain is deprecated)
            return IsSameOrigin(other);
        }

        public bool Equals(Origin other) => IsSameOrigin(other);
        public override bool Equals(object obj) => obj is Origin o && IsSameOrigin(o);

        public override int GetHashCode()
        {
            if (IsOpaque)
                return RuntimeHelpers.GetHashCode(this);

            return HashCode.Combine(
                Scheme?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0,
                Host?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0,
                Port);
        }

        public override string ToString()
        {
            if (IsOpaque) return "null";

            // Uri.Host exposes IPv6 literals without brackets. Origin serialization
            // requires brackets so non-default ports remain unambiguous.
            var serializedHost = Host ?? string.Empty;
            if (serializedHost.IndexOf(':') >= 0 &&
                !serializedHost.StartsWith("[", StringComparison.Ordinal) &&
                !serializedHost.EndsWith("]", StringComparison.Ordinal))
            {
                serializedHost = $"[{serializedHost}]";
            }

            int defaultPort = GetDefaultPort(Scheme);
            return Port == defaultPort || Port <= 0
                ? $"{Scheme}://{serializedHost}"
                : $"{Scheme}://{serializedHost}:{Port}";
        }

        public static bool operator ==(Origin a, Origin b) => a?.IsSameOrigin(b) ?? b is null;
        public static bool operator !=(Origin a, Origin b) => !(a == b);

        private static int GetDefaultPort(string scheme) => scheme switch
        {
            "http" => 80,
            "https" => 443,
            "ftp" => 21,
            "ws" => 80,
            "wss" => 443,
            _ => -1
        };
    }
}
