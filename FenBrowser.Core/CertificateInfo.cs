using System;
using System.Collections.Generic;
using System.Net.Security;

namespace FenBrowser.Core
{
    public class CertificateInfo
    {
        private string _subject = string.Empty;
        private string _issuer = string.Empty;
        private string _thumbprint = string.Empty;
        private IReadOnlyList<string> _subjectAlternativeNames = Array.Empty<string>();

        public string Subject
        {
            get => _subject;
            set => _subject = NormalizeText(value);
        }

        public string Issuer
        {
            get => _issuer;
            set => _issuer = NormalizeText(value);
        }

        public DateTime NotBefore { get; set; }
        public DateTime NotAfter { get; set; }
        
        public string Thumbprint
        {
            get => _thumbprint;
            set => _thumbprint = NormalizeThumbprint(value);
        }

        public bool IsValid { get; set; }

        /// <summary>Subject Alternative Names (DNS names the cert is valid for).</summary>
        public IReadOnlyList<string> SubjectAlternativeNames
        {
            get => _subjectAlternativeNames;
            set => _subjectAlternativeNames = NormalizeSubjectAlternativeNames(value);
        }

        /// <summary>The SSL policy errors reported by the TLS handshake (None = fully trusted).</summary>
        public SslPolicyErrors PolicyErrors { get; set; } = SslPolicyErrors.None;

        public bool HasPolicyErrors => PolicyErrors != SslPolicyErrors.None;

        public bool IsDateRangeValid => NotAfter >= NotBefore;

        public bool IsCurrentlyValid
        {
            get
            {
                var now = DateTime.Now;
                return IsDateRangeValid && !HasPolicyErrors && now >= NotBefore && now <= NotAfter;
            }
        }

        public string ExpiryStatus
        {
            get
            {
                if (!IsDateRangeValid) return "Invalid date range";
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

        public override string ToString()
        {
            return $"{Subject} | {Issuer} | {ExpiryStatus}";
        }

        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string NormalizeThumbprint(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Replace(" ", string.Empty)
                .Replace(":", string.Empty)
                .Trim()
                .ToUpperInvariant();
        }

        private static IReadOnlyList<string> NormalizeSubjectAlternativeNames(IReadOnlyList<string> value)
        {
            if (value == null || value.Count == 0)
            {
                return Array.Empty<string>();
            }

            var results = new List<string>(value.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in value)
            {
                var normalized = NormalizeText(entry);
                if (normalized.Length == 0 || !seen.Add(normalized))
                {
                    continue;
                }

                results.Add(normalized);
            }

            return results.Count == 0 ? Array.Empty<string>() : results.ToArray();
        }
    }
}
