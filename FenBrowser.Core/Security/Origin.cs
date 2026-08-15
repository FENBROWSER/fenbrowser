using System;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// Represents a web origin per HTML origin semantics.
    /// An origin is a (scheme, host, port) tuple, or an opaque origin with identity.
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
            Scheme = NormalizeScheme(scheme);
            Host = NormalizeHost(host);
            Port = port;
            IsOpaque = false;
        }

        /// <summary>Create a fresh opaque origin (for data: URLs, sandboxed documents, etc.).</summary>
        public static Origin Opaque() => new();

        /// <summary>
        /// Derive an origin from a URI when the URI itself carries enough origin
        /// information. URLs whose origin depends on creator/browsing-context state
        /// deliberately become fresh opaque origins here; callers that possess creator
        /// state must propagate that Origin object instead of reconstructing it from URL text.
        /// </summary>
        public static Origin FromUri(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return Opaque();
            }

            var scheme = NormalizeScheme(uri.Scheme);

            // A blob URL inherits the origin encoded by its inner URL. Treating every
            // blob: URL as opaque breaks same-origin blob fetches, workers, images and
            // object URLs created by the current document. An inner opaque origin (for
            // example blob:null/...) remains opaque because it cannot be reconstructed
            // from URL text alone.
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

            // data: and javascript: are opaque. about: URLs such as about:blank and
            // about:srcdoc may inherit a creator origin, but this method intentionally
            // has no creator context and therefore must not manufacture one.
            //
            // file: origin comparison is implementation-defined. Treating all local
            // files as the tuple (file, "", -1) made every file URL same-origin with
            // every other file URL. FenBrowser chooses the safer unique-origin model;
            // explicit file navigation permissions are handled separately.
            if (scheme is "data" or "javascript" or "about" or "file")
            {
                return Opaque();
            }

            var host = NormalizeHost(uri.IdnHost);
            if (string.IsNullOrEmpty(host) && RequiresHostTuple(scheme))
            {
                return Opaque();
            }

            var port = uri.IsDefaultPort ? GetDefaultPort(scheme) : uri.Port;
            return new Origin(scheme, host, port);
        }

        /// <summary>
        /// Same-origin check. Tuple origins compare by canonical scheme/host/port.
        /// Opaque origins carry identity: an opaque origin is same-origin only with
        /// the exact same Origin object, never with a separately-created opaque origin.
        /// </summary>
        public bool IsSameOrigin(Origin other)
        {
            if (other == null)
            {
                return false;
            }

            if (IsOpaque || other.IsOpaque)
            {
                return IsOpaque && other.IsOpaque && ReferenceEquals(this, other);
            }

            return string.Equals(Scheme, other.Scheme, StringComparison.Ordinal) &&
                   string.Equals(Host, other.Host, StringComparison.Ordinal) &&
                   Port == other.Port;
        }

        /// <summary>
        /// Same-origin-domain check. document.domain relaxation is intentionally not
        /// implemented; until it is, this cannot be broader than same-origin.
        /// </summary>
        public bool IsSameOriginDomain(Origin other) => IsSameOrigin(other);

        public bool Equals(Origin other) => IsSameOrigin(other);
        public override bool Equals(object obj) => obj is Origin o && IsSameOrigin(o);

        public override int GetHashCode()
        {
            if (IsOpaque)
            {
                return RuntimeHelpers.GetHashCode(this);
            }

            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(Scheme ?? string.Empty),
                StringComparer.Ordinal.GetHashCode(Host ?? string.Empty),
                Port);
        }

        public override string ToString()
        {
            if (IsOpaque)
            {
                return "null";
            }

            // Canonical host storage keeps IPv6 literals bracketless. Origin
            // serialization adds brackets so an explicit port remains unambiguous.
            var serializedHost = Host ?? string.Empty;
            if (serializedHost.IndexOf(':') >= 0 &&
                !serializedHost.StartsWith("[", StringComparison.Ordinal) &&
                !serializedHost.EndsWith("]", StringComparison.Ordinal))
            {
                serializedHost = $"[{serializedHost}]";
            }

            var defaultPort = GetDefaultPort(Scheme);
            return Port == defaultPort || Port < 0
                ? $"{Scheme}://{serializedHost}"
                : $"{Scheme}://{serializedHost}:{Port}";
        }

        public static bool operator ==(Origin a, Origin b) => a?.IsSameOrigin(b) ?? b is null;
        public static bool operator !=(Origin a, Origin b) => !(a == b);

        private static string NormalizeScheme(string scheme)
            => (scheme ?? string.Empty).Trim().ToLowerInvariant();

        private static string NormalizeHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return string.Empty;
            }

            return host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();
        }

        private static bool RequiresHostTuple(string scheme)
            => scheme is "http" or "https" or "ws" or "wss" or "ftp";

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
