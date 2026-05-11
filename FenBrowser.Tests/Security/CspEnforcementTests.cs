using System;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Security
{
    public class CspEnforcementTests
    {
        [Fact]
        public void CspEnforcement_BlocksJavaScriptUrls()
        {
            const string csp = "default-src 'self'";
            
            bool allowed = CspEnforcement.IsRequestAllowed("javascript:alert(1)", "https://example.com", csp);
            
            Assert.False(allowed);
        }
        
        [Fact]
        public void CspEnforcement_AllowsSelfUrls()
        {
            const string csp = "default-src 'self'";
            
            bool allowed = CspEnforcement.IsRequestAllowed("https://example.com/api", "https://example.com", csp);
            
            Assert.True(allowed);
        }
        
        [Fact]
        public void CspEnforcement_BlocksCrossOriginWhenNotSpecified()
        {
            const string csp = "default-src 'self'";
            
            bool allowed = CspEnforcement.IsRequestAllowed("https://evil.com/script.js", "https://example.com", csp);
            
            Assert.False(allowed);
        }
        
        [Fact]
        public void CspEnforcement_AllowsWildcard()
        {
            const string csp = "default-src *";
            
            bool allowed = CspEnforcement.IsRequestAllowed("https://any.com/resource", "https://example.com", csp);
            
            Assert.True(allowed);
        }
        
        [Fact]
        public void CspEnforcement_AllowsExplicitHost()
        {
            const string csp = "default-src 'self' https://cdn.example.com";
            
            bool allowed = CspEnforcement.IsRequestAllowed("https://cdn.example.com/script.js", "https://example.com", csp);
            
            Assert.True(allowed);
        }
    }
}
