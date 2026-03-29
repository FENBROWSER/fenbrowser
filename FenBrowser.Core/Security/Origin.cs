using System;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// Represents a web origin per HTML spec §7.5.
    /// An origin is (scheme, host, port) tuple, or "opaque" for sandboxed/data URLs.
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

        /// <summary>Create an opaque origin (for data: URLs, sandboxed iframes, etc.)</summary>
        public static Origin Opaque() => new Origin();

        /// <summary>Derive origin from a URI.</summary>
        public static Origin FromUri(Uri uri)
        {
            if (uri == null) return Opaque();
            var scheme = uri.Scheme?.ToLowerInvariant();
            if (scheme == "data" || scheme == "blob" || scheme == "javascript" || scheme == "about")
                return Opaque();
            int port = uri.Port;
            if (port == -1) port = GetDefaultPort(scheme);
            return new Origin(scheme, uri.Host, port);
        }

        /// <summary>
        /// Same-origin check per HTML spec §7.5.
        /// Two origins are same-origin if they have the same scheme, host, and port.
        /// Opaque origins are never same-origin with anything (including themselves).
        /// </summary>
        public bool IsSameOrigin(Origin other)
        {
            if (other == null) return false;
            if (IsOpaque || other.IsOpaque) return false;
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
            if (IsOpaque) return 0;
            return HashCode.Combine(
                Scheme?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0,
                Host?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0,
                Port);
        }

        public override string ToString()
        {
            if (IsOpaque) return "null";
            int defaultPort = GetDefaultPort(Scheme);
            return Port == defaultPort || Port <= 0
                ? $"{Scheme}://{Host}"
                : $"{Scheme}://{Host}:{Port}";
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
