// =============================================================================
// Capabilities.cs
// W3C WebDriver Capabilities (Spec-Compliant)
// 
// SPEC REFERENCE: W3C WebDriver §7.2 - Capabilities
//                 https://www.w3.org/TR/webdriver2/#capabilities
// =============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FenBrowser.WebDriver.Protocol
{
    /// <summary>
    /// W3C WebDriver capabilities.
    /// </summary>
    public class Capabilities
    {
        // Standard capabilities
        [JsonPropertyName("browserName")]
        public string BrowserName { get; set; } = "FenBrowser";
        
        [JsonPropertyName("browserVersion")]
        public string BrowserVersion { get; set; } = "1.0.0";
        
        [JsonPropertyName("platformName")]
        public string PlatformName { get; set; } = "windows";
        
        [JsonPropertyName("acceptInsecureCerts")]
        public bool AcceptInsecureCerts { get; set; } = false;
        
        [JsonPropertyName("pageLoadStrategy")]
        public string PageLoadStrategy { get; set; } = "normal";
        
        [JsonPropertyName("proxy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ProxyConfig Proxy { get; set; }
        
        [JsonPropertyName("setWindowRect")]
        public bool SetWindowRect { get; set; } = true;
        
        [JsonPropertyName("timeouts")]
        public Timeouts Timeouts { get; set; } = new Timeouts();
        
        [JsonPropertyName("strictFileInteractability")]
        public bool StrictFileInteractability { get; set; } = false;
        
        [JsonPropertyName("unhandledPromptBehavior")]
        public string UnhandledPromptBehavior { get; set; } = "dismiss and notify";
        
        // FenBrowser-specific capabilities
        [JsonPropertyName("fen:options")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public FenOptions FenOptions { get; set; }
        
        /// <summary>
        /// Merge requested capabilities.
        /// </summary>
        public static Capabilities Merge(Capabilities requested)
        {
            var caps = new Capabilities();
            
            if (requested != null)
            {
                if (requested.AcceptInsecureCerts)
                    caps.AcceptInsecureCerts = true;
                    
                if (!string.IsNullOrEmpty(requested.PageLoadStrategy))
                    caps.PageLoadStrategy = requested.PageLoadStrategy;
                    
                if (requested.Timeouts != null)
                    caps.Timeouts = requested.Timeouts;
                    
                if (requested.Proxy != null)
                    caps.Proxy = requested.Proxy;
                    
                if (requested.FenOptions != null)
                    caps.FenOptions = requested.FenOptions;
            }
            
            return caps;
        }
    }
    
    /// <summary>
    /// Session timeouts.
    /// </summary>
    public class Timeouts
    {
        [JsonPropertyName("script")]
        public int? Script { get; set; } = 30000;
        
        [JsonPropertyName("pageLoad")]
        public int? PageLoad { get; set; } = 300000;
        
        [JsonPropertyName("implicit")]
        public int? Implicit { get; set; } = 0;
    }
    
    /// <summary>
    /// Proxy configuration.
    /// </summary>
    public class ProxyConfig
    {
        [JsonPropertyName("proxyType")]
        public string ProxyType { get; set; }
        
        [JsonPropertyName("httpProxy")]
        public string HttpProxy { get; set; }
        
        [JsonPropertyName("sslProxy")]
        public string SslProxy { get; set; }
        
        [JsonPropertyName("noProxy")]
        public List<string> NoProxy { get; set; }
    }
    
    /// <summary>
    /// FenBrowser-specific options.
    /// </summary>
    public class FenOptions
    {
        [JsonPropertyName("headless")]
        public bool Headless { get; set; } = false;
        
        [JsonPropertyName("debuggerAddress")]
        public string DebuggerAddress { get; set; }
        
        [JsonPropertyName("args")]
        public List<string> Args { get; set; }
        
        [JsonPropertyName("binary")]
        public string Binary { get; set; }
    }
    
    /// <summary>
    /// Capability request for new session.
    /// </summary>
    public class CapabilityRequest
    {
        [JsonPropertyName("capabilities")]
        public CapabilitiesWrapper Capabilities { get; set; }
    }
    
    public class CapabilitiesWrapper
    {
        [JsonPropertyName("alwaysMatch")]
        public Capabilities AlwaysMatch { get; set; }
        
        [JsonPropertyName("firstMatch")]
        public List<Capabilities> FirstMatch { get; set; }
    }
}
