using System;

namespace FenBrowser.FenEngine.Configuration
{
    /// <summary>
    /// Production-grade configuration options for the FenEngine rendering and scripting pipeline.
    /// This centralizes settings for security, performance, compatibility, and debugging.
    /// </summary>
    public sealed class FenEngineOptions
    {
        // Singleton defaults
        public static FenEngineOptions Default { get; } = new FenEngineOptions();
        public static FenEngineOptions Production { get; } = CreateProduction();
        public static FenEngineOptions Development { get; } = CreateDevelopment();

        // ========================================
        // 1. JavaScript VM Configuration
        // ========================================
        
        /// <summary>
        /// When true, property access on null/undefined returns undefined instead of throwing TypeError.
        /// This improves compatibility with legacy code and certain minified libraries.
        /// Default: false (strict mode)
        /// </summary>
        public bool VmLenientPropertyAccess { get; set; } = false;
        
        /// <summary>
        /// When VmLenientPropertyAccess is true, also log warnings about null/undefined property access.
        /// Useful for debugging without breaking execution.
        /// Default: false
        /// </summary>
        public bool VmLogLenientAccessWarnings { get; set; } = false;

        /// <summary>
        /// Maximum depth for property chain resolution before circuit-breaking.
        /// Prevents infinite loops in circular prototype chains.
        /// Default: 100
        /// </summary>
        public int VmMaxPropertyChainDepth { get; set; } = 100;

        // ========================================
        // 2. Security Configuration
        // ========================================
        
        /// <summary>
        /// Allow XHR/fetch requests to relative URLs (e.g., /api/data, //cdn.example.com).
        /// When false, only absolute http/https URLs are permitted.
        /// Default: true
        /// </summary>
        public bool AllowRelativeUrls { get; set; } = true;
        
        /// <summary>
        /// Automatically resolve relative URLs against the document base URI.
        /// Default: true
        /// </summary>
        public bool AutoResolveRelativeUrls { get; set; } = true;
        
        /// <summary>
        /// Allowed URL schemes for network requests beyond http/https.
        /// Default: none (only http/https)
        /// </summary>
        public string[] AdditionalAllowedSchemes { get; set; } = Array.Empty<string>();

        /// <summary>
        /// When true, blocks requests to private/reserved IP ranges (10.0.0.0/8, 192.168.0.0/16, etc.)
        /// Default: true (production security)
        /// </summary>
        public bool BlockPrivateNetworkAccess { get; set; } = true;

        // ========================================
        // 3. CSS Configuration
        // ========================================
        
        /// <summary>
        /// Maximum recursion depth for CSS var() resolution.
        /// Prevents infinite loops in circular variable references.
        /// Default: 10
        /// </summary>
        public int CssMaxVarRecursionDepth { get; set; } = 10;
        
        /// <summary>
        /// When true, unresolved CSS variables return their fallback value or empty string
        /// instead of the raw var() expression.
        /// Default: true
        /// </summary>
        public bool CssUseFallbackForUnresolvedVars { get; set; } = true;
        
        /// <summary>
        /// When true, log CSS variable resolution misses at Debug level.
        /// Useful for debugging missing custom properties.
        /// Default: false
        /// </summary>
        public bool CssLogVariableMisses { get; set; } = false;
        
        /// <summary>
        /// Delay in milliseconds before resolving CSS custom properties to allow
        /// all stylesheets to load. Set to 0 for immediate resolution.
        /// Default: 0
        /// </summary>
        public int CssVariableResolutionDelayMs { get; set; } = 0;

        // ========================================
        // 4. Resource Loading Configuration
        // ========================================
        
        /// <summary>
        /// Timeout in seconds for external resource fetching (CSS, images, fonts).
        /// Default: 30
        /// </summary>
        public int ResourceFetchTimeoutSeconds { get; set; } = 30;
        
        /// <summary>
        /// Maximum concurrent resource fetches.
        /// Default: 6 (typical browser limit)
        /// </summary>
        public int MaxConcurrentResourceFetches { get; set; } = 6;
        
        /// <summary>
        /// When true, automatically fetch external stylesheets referenced in HTML.
        /// Default: true
        /// </summary>
        public bool AutoFetchExternalStylesheets { get; set; } = true;
        
        /// <summary>
        /// When true, automatically fetch images referenced in src/srcset attributes.
        /// Default: true
        /// </summary>
        public bool AutoFetchImages { get; set; } = true;
        
        /// <summary>
        /// When true, enable lazy loading for images outside initial viewport.
        /// Default: true
        /// </summary>
        public bool EnableLazyImageLoading { get; set; } = true;
        
        /// <summary>
        /// Base URL for resolving relative resource URLs when document base URI is unavailable.
        /// Default: null (uses document URL)
        /// </summary>
        public string ResourceBaseUrlFallback { get; set; } = null;

        // ========================================
        // 5. Performance Configuration
        // ========================================
        
        /// <summary>
        /// Size of the image cache in MB.
        /// Default: 100
        /// </summary>
        public int ImageCacheSizeMB { get; set; } = 100;
        
        /// <summary>
        /// Size of the CSS stylesheet cache in MB.
        /// Default: 50
        /// </summary>
        public int CssCacheSizeMB { get; set; } = 50;
        
        /// <summary>
        /// When true, enable speculative resource prefetching.
        /// Default: false
        /// </summary>
        public bool EnableSpeculativePrefetching { get; set; } = false;

        // ========================================
        // Factory Methods
        // ========================================
        
        private static FenEngineOptions CreateProduction()
        {
            return new FenEngineOptions
            {
                // Production: strict security, high performance
                VmLenientPropertyAccess = false,
                VmLogLenientAccessWarnings = false,
                AllowRelativeUrls = true,
                AutoResolveRelativeUrls = true,
                BlockPrivateNetworkAccess = true,
                CssMaxVarRecursionDepth = 10,
                CssUseFallbackForUnresolvedVars = true,
                CssLogVariableMisses = false,
                ResourceFetchTimeoutSeconds = 30,
                MaxConcurrentResourceFetches = 6,
                AutoFetchExternalStylesheets = true,
                AutoFetchImages = true,
                EnableLazyImageLoading = true,
                ImageCacheSizeMB = 100,
                CssCacheSizeMB = 50,
                EnableSpeculativePrefetching = false
            };
        }

        /// <summary>
        /// Creates a locked-down profile with maximum security settings.
        /// </summary>
        public static FenEngineOptions CreateLockedDown()
        {
            return new FenEngineOptions
            {
                // Locked down: maximum security
                VmLenientPropertyAccess = false,
                VmLogLenientAccessWarnings = false,
                VmMaxPropertyChainDepth = 50,
                AllowRelativeUrls = true,
                AutoResolveRelativeUrls = true,
                BlockPrivateNetworkAccess = true,
                CssMaxVarRecursionDepth = 10,
                CssUseFallbackForUnresolvedVars = true,
                CssLogVariableMisses = false,
                ResourceFetchTimeoutSeconds = 10,
                MaxConcurrentResourceFetches = 4,
                AutoFetchExternalStylesheets = true,
                AutoFetchImages = true,
                EnableLazyImageLoading = true,
                ImageCacheSizeMB = 50,
                CssCacheSizeMB = 25,
                EnableSpeculativePrefetching = false
            };
        }

        private static FenEngineOptions CreateDevelopment()
        {
            return new FenEngineOptions
            {
                // Development: lenient for debugging, full logging
                VmLenientPropertyAccess = true,
                VmLogLenientAccessWarnings = true,
                AllowRelativeUrls = true,
                AutoResolveRelativeUrls = true,
                BlockPrivateNetworkAccess = false, // Allow local dev servers
                CssMaxVarRecursionDepth = 10,
                CssUseFallbackForUnresolvedVars = true,
                CssLogVariableMisses = true,
                ResourceFetchTimeoutSeconds = 60, // More patience for debugging
                MaxConcurrentResourceFetches = 6,
                AutoFetchExternalStylesheets = true,
                AutoFetchImages = true,
                EnableLazyImageLoading = false, // Load everything for debugging
                ImageCacheSizeMB = 50,
                CssCacheSizeMB = 25,
                EnableSpeculativePrefetching = false
            };
        }

        /// <summary>
        /// Validates the configuration and throws if invalid.
        /// </summary>
        public void Validate()
        {
            if (VmMaxPropertyChainDepth < 1 || VmMaxPropertyChainDepth > 10000)
                throw new ArgumentOutOfRangeException(nameof(VmMaxPropertyChainDepth), "Must be between 1 and 10000");
            
            if (CssMaxVarRecursionDepth < 1 || CssMaxVarRecursionDepth > 100)
                throw new ArgumentOutOfRangeException(nameof(CssMaxVarRecursionDepth), "Must be between 1 and 100");
            
            if (ResourceFetchTimeoutSeconds < 1 || ResourceFetchTimeoutSeconds > 300)
                throw new ArgumentOutOfRangeException(nameof(ResourceFetchTimeoutSeconds), "Must be between 1 and 300");
            
            if (MaxConcurrentResourceFetches < 1 || MaxConcurrentResourceFetches > 100)
                throw new ArgumentOutOfRangeException(nameof(MaxConcurrentResourceFetches), "Must be between 1 and 100");
        }
    }
}
