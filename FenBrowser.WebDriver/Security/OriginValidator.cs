// =============================================================================
// OriginValidator.cs
// WebDriver Security - Origin Validation
// 
// PURPOSE: Validates request origins to prevent CSRF and unauthorized access.
// SECURITY: Whitelist-based origin checking, localhost enforcement.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Net;

namespace FenBrowser.WebDriver.Security
{
    /// <summary>
    /// Validates request origins for security.
    /// </summary>
    public class OriginValidator
    {
        private readonly HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _allowLocalhostOnly;
        
        public OriginValidator(bool allowLocalhostOnly = true)
        {
            _allowLocalhostOnly = allowLocalhostOnly;
            
            // Default allowed origins (localhost variants)
            _allowedOrigins.Add("localhost");
            _allowedOrigins.Add("127.0.0.1");
            _allowedOrigins.Add("::1");
        }
        
        /// <summary>
        /// Add an allowed origin.
        /// </summary>
        public void AllowOrigin(string origin)
        {
            if (!string.IsNullOrEmpty(origin))
            {
                _allowedOrigins.Add(origin);
            }
        }
        
        /// <summary>
        /// Validate that a request origin is allowed.
        /// </summary>
        public bool ValidateOrigin(IPEndPoint remoteEndpoint)
        {
            if (remoteEndpoint == null)
                return false;
            
            var address = remoteEndpoint.Address;
            
            // Allow loopback
            if (IPAddress.IsLoopback(address))
                return true;
            
            if (_allowLocalhostOnly)
            {
                return false;
            }
            
            // Check against whitelist
            return _allowedOrigins.Contains(address.ToString());
        }
        
        /// <summary>
        /// Validate Origin header if present.
        /// </summary>
        public bool ValidateOriginHeader(string originHeader)
        {
            if (string.IsNullOrEmpty(originHeader))
                return true; // No origin header is ok for non-browser clients
            
            try
            {
                var uri = new Uri(originHeader);
                var host = uri.Host;
                
                return _allowedOrigins.Contains(host) || 
                       host == "localhost" ||
                       host == "127.0.0.1";
            }
            catch
            {
                return false;
            }
        }
    }
}
