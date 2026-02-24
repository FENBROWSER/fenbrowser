using System;
using System.Collections.Generic;
using System.Net.Security;

namespace FenBrowser.Core
{
    public class CertificateInfo
    {
        public string Subject { get; set; }
        public string Issuer { get; set; }
        public DateTime NotBefore { get; set; }
        public DateTime NotAfter { get; set; }
        public string Thumbprint { get; set; }
        public bool IsValid { get; set; }

        /// <summary>Subject Alternative Names (DNS names the cert is valid for).</summary>
        public IReadOnlyList<string> SubjectAlternativeNames { get; set; } = Array.Empty<string>();

        /// <summary>The SSL policy errors reported by the TLS handshake (None = fully trusted).</summary>
        public SslPolicyErrors PolicyErrors { get; set; } = SslPolicyErrors.None;

        public string ExpiryStatus
        {
            get
            {
                if (DateTime.Now < NotBefore) return "Not yet valid";
                if (DateTime.Now > NotAfter) return "Expired";
                return "Valid";
            }
        }

        /// <summary>Human-readable description of the certificate error, or null if valid.</summary>
        public string ErrorDescription
        {
            get
            {
                if (PolicyErrors == SslPolicyErrors.None) return null;

                var parts = new List<string>();
                if ((PolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                    parts.Add("Certificate name does not match the site address");
                if ((PolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
                    parts.Add("Certificate was not issued by a trusted authority");
                if ((PolicyErrors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
                    parts.Add("Site did not provide a certificate");
                return parts.Count > 0 ? string.Join("; ", parts) : PolicyErrors.ToString();
            }
        }
    }
}
