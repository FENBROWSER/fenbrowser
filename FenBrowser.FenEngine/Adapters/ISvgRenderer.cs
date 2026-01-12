using SkiaSharp;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// SVG rendering interface. Wraps Svg.Skia or any other SVG library.
    /// 
    /// RULE 3: SVG must be sandboxed. This adapter enforces limits.
    /// RULE 5: If Svg.Skia disappears, we only replace this implementation.
    /// </summary>
    public interface ISvgRenderer
    {
        /// <summary>
        /// Render SVG to an SKPicture with safety limits.
        /// </summary>
        /// <param name="svgContent">SVG XML content</param>
        /// <param name="limits">Rendering limits for sandboxing</param>
        /// <returns>Rendered picture, or null if failed</returns>
        SvgRenderResult Render(string svgContent, SvgRenderLimits limits);
        
        /// <summary>
        /// Render SVG with default (safe) limits.
        /// </summary>
        SvgRenderResult Render(string svgContent);
    }
    
    /// <summary>
    /// Result of SVG rendering.
    /// </summary>
    public class SvgRenderResult
    {
        /// <summary>
        /// The rendered picture (null if failed).
        /// WARNING: May be invalid after SKSvg disposal - use Bitmap property instead.
        /// </summary>
        public SKPicture Picture { get; set; }
        
        /// <summary>
        /// Pre-rendered bitmap (safe to use after SKSvg disposal).
        /// This is the preferred way to access the rendered SVG.
        /// </summary>
        public SKBitmap Bitmap { get; set; }
        
        /// <summary>
        /// Natural width of the SVG.
        /// </summary>
        public float Width { get; set; }
        
        /// <summary>
        /// Natural height of the SVG.
        /// </summary>
        public float Height { get; set; }
        
        /// <summary>
        /// Whether rendering succeeded.
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// Error message if failed.
        /// </summary>
        public string ErrorMessage { get; set; }
    }
    
    /// <summary>
    /// Safety limits for SVG rendering.
    /// 
    /// RULE 3: SVG must be sandboxed deliberately.
    /// These limits prevent DoS attacks and stability issues.
    /// </summary>
    public struct SvgRenderLimits
    {
        /// <summary>
        /// Maximum recursion depth for nested elements.
        /// Default: 32
        /// </summary>
        public int MaxRecursionDepth { get; set; }
        
        /// <summary>
        /// Maximum number of filter effects.
        /// Default: 10
        /// </summary>
        public int MaxFilterCount { get; set; }
        
        /// <summary>
        /// Maximum render time in milliseconds.
        /// Default: 100ms
        /// </summary>
        public int MaxRenderTimeMs { get; set; }
        
        /// <summary>
        /// Maximum total element count.
        /// Default: 10000
        /// </summary>
        public int MaxElementCount { get; set; }
        
        /// <summary>
        /// Whether to allow external references (xlink:href to external URLs).
        /// Default: false (DISABLED for security)
        /// </summary>
        public bool AllowExternalReferences { get; set; }
        
        /// <summary>
        /// Get default safe limits.
        /// </summary>
        public static SvgRenderLimits Default => new SvgRenderLimits
        {
            MaxRecursionDepth = 64,
            MaxFilterCount = 20,
            MaxRenderTimeMs = 2000,
            MaxElementCount = 50000,
            AllowExternalReferences = false
        };
        
        /// <summary>
        /// Get strict limits for untrusted content.
        /// </summary>
        public static SvgRenderLimits Strict => new SvgRenderLimits
        {
            MaxRecursionDepth = 16,
            MaxFilterCount = 5,
            MaxRenderTimeMs = 50,
            MaxElementCount = 5000,
            AllowExternalReferences = false
        };
    }
}
