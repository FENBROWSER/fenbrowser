using System;

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
        public string ExpiryStatus
        {
            get
            {
                if (DateTime.Now < NotBefore) return "Not yet valid";
                if (DateTime.Now > NotAfter) return "Expired";
                return "Valid";
            }
        }
    }
}
