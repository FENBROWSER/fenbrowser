using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Security;

namespace FenBrowser.FenEngine.WebAPIs.WebAuthn
{
    /// <summary>
    /// An authenticator the browser can hand Web Authentication ceremonies to (Windows Hello
    /// through webauthn.dll, or the broker that owns it). The engine builds the ceremony
    /// from a page's options; the authenticator side validates the relying party against
    /// the origin, produces the client data and talks to the platform.
    /// </summary>
    public interface IWebAuthnAuthenticator
    {
        Task<bool> IsUserVerifyingPlatformAuthenticatorAvailableAsync();

        Task<WebAuthnResult> GetAssertionAsync(WebAuthnGetRequest request, CancellationToken cancellationToken);

        Task<WebAuthnResult> MakeCredentialAsync(WebAuthnCreateRequest request, CancellationToken cancellationToken);
    }

    /// <summary>The authenticator this process uses; null when there is none (WebAuthn then reports no authenticator).</summary>
    public static class WebAuthnPlatform
    {
        public static IWebAuthnAuthenticator Authenticator { get; set; }
    }

    public sealed class WebAuthnCredentialDescriptor
    {
        public string Id { get; set; }                    // base64url
        public List<string> Transports { get; set; } = new();
    }

    /// <summary>navigator.credentials.get({ publicKey }) (WebAuthn L3 §5.5).</summary>
    public sealed class WebAuthnGetRequest
    {
        public string Origin { get; set; }
        public string RpId { get; set; }
        public string Challenge { get; set; }             // base64url
        public int TimeoutMs { get; set; }
        public string UserVerification { get; set; } = "preferred";
        public List<WebAuthnCredentialDescriptor> AllowCredentials { get; set; } = new();
    }

    /// <summary>navigator.credentials.create({ publicKey }) (WebAuthn L3 §5.4).</summary>
    public sealed class WebAuthnCreateRequest
    {
        public string Origin { get; set; }
        public string RpId { get; set; }
        public string RpName { get; set; }
        public string UserId { get; set; }                // base64url
        public string UserName { get; set; }
        public string UserDisplayName { get; set; }
        public string Challenge { get; set; }             // base64url
        public List<int> Algorithms { get; set; } = new();
        public List<WebAuthnCredentialDescriptor> ExcludeCredentials { get; set; } = new();
        public string AuthenticatorAttachment { get; set; }
        public string ResidentKey { get; set; }
        public string UserVerification { get; set; } = "preferred";
        public string Attestation { get; set; } = "none";
        public int TimeoutMs { get; set; }
    }

    /// <summary>A ceremony's outcome: a credential, or the DOMException name it failed with.</summary>
    public sealed class WebAuthnResult
    {
        public string ErrorName { get; set; }
        public string ErrorMessage { get; set; }
        public string ClientDataJson { get; set; }        // base64url of the exact bytes signed over
        public string CredentialId { get; set; }          // base64url
        public string AuthenticatorData { get; set; }     // base64url
        public string Signature { get; set; }             // base64url (get)
        public string UserHandle { get; set; }            // base64url (get, may be null)
        public string AttestationObject { get; set; }     // base64url (create)
        public List<string> Transports { get; set; } = new();
        public string AuthenticatorAttachment { get; set; }

        public static WebAuthnResult Failure(string name, string message) => new() { ErrorName = name, ErrorMessage = message };

        public static WebAuthnResult NotAllowed() => Failure(
            "NotAllowedError",
            "The operation either timed out or was not allowed. See: https://www.w3.org/TR/webauthn-2/#sctn-privacy-considerations-client.");
    }

    /// <summary>
    /// The client-side checks and data a WebAuthn client must produce itself, never trusting
    /// the page for them (WebAuthn L3 §5.1.3 / §5.1.4.1 and §5.8.1).
    /// </summary>
    public static class WebAuthnClient
    {
        /// <summary>
        /// The origin must be potentially trustworthy (https, or http on localhost) and not
        /// opaque; the RP ID, when given, must be the origin's host or a registrable domain
        /// suffix of it that is not itself a public suffix. Returns the effective RP ID.
        /// </summary>
        public static bool TryResolveRpId(string origin, string requestedRpId, out string rpId, out string errorName)
        {
            rpId = null;
            errorName = "SecurityError";
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || string.IsNullOrEmpty(originUri.Host))
            {
                return false;
            }

            bool localhost = string.Equals(originUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                             originUri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(originUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                !(localhost && string.Equals(originUri.Scheme, "http", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (System.Net.IPAddress.TryParse(originUri.Host.Trim('[', ']'), out _))
            {
                return false;
            }

            string host = originUri.IdnHost.ToLowerInvariant();
            string candidate = string.IsNullOrWhiteSpace(requestedRpId) ? host : requestedRpId.Trim().ToLowerInvariant();
            if (candidate.Length == 0 || candidate.Contains('/') || candidate.Contains(':'))
            {
                return false;
            }

            bool sameHost = string.Equals(candidate, host, StringComparison.Ordinal);
            bool registrableSuffix = host.EndsWith("." + candidate, StringComparison.Ordinal) &&
                                     !SiteIdentityService.Default.IsPublicSuffix(candidate);
            if (!sameHost && !registrableSuffix)
            {
                return false;
            }

            rpId = candidate;
            errorName = null;
            return true;
        }

        /// <summary>
        /// §5.8.1.1 CollectedClientData, serialised in the specified member order with the
        /// limited JSON encoding: type, challenge, origin, crossOrigin.
        /// </summary>
        public static byte[] BuildClientDataJson(string type, string challengeBase64Url, string origin, bool crossOrigin = false)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                writer.WriteString("type", type);
                writer.WriteString("challenge", challengeBase64Url);
                writer.WriteString("origin", SerializeOrigin(origin));
                writer.WriteBoolean("crossOrigin", crossOrigin);
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }

        /// <summary>The ASCII serialisation of an origin: scheme://host[:non-default port].</summary>
        public static string SerializeOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return origin ?? string.Empty;
            }

            string serialized = uri.Scheme.ToLowerInvariant() + "://" + uri.IdnHost.ToLowerInvariant();
            return uri.IsDefaultPort ? serialized : serialized + ":" + uri.Port;
        }

        public static string ToBase64Url(byte[] bytes) =>
            bytes == null ? null : Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Array.Empty<byte>();
            }

            string s = value.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '=');
            return Convert.FromBase64String(s);
        }
    }
}
