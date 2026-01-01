using System;
using System.Diagnostics;
using SkiaSharp;
using Svg.Skia;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// Svg.Skia-based implementation of ISvgRenderer.
    /// 
    /// RULE 3: Enforces safety limits for SVG sandboxing.
    /// RULE 5: If Svg.Skia disappears, replace this class only.
    /// </summary>
    public class SvgSkiaRenderer : ISvgRenderer
    {
        public SvgRenderResult Render(string svgContent)
        {
            return Render(svgContent, SvgRenderLimits.Default);
        }
        
        public SvgRenderResult Render(string svgContent, SvgRenderLimits limits)
        {
            if (string.IsNullOrWhiteSpace(svgContent))
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = "Empty SVG content"
                };
            }
            
            // Pre-validation: Check for complexity bombs
            if (!ValidateSvgComplexity(svgContent, limits, out string validationError))
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = validationError
                };
            }
            
            // Strip external references if not allowed
            if (!limits.AllowExternalReferences)
            {
                svgContent = StripExternalReferences(svgContent);
            }
            
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                using var svg = new SKSvg();
                var picture = svg.FromSvg(svgContent);
                
                // Check render time limit
                if (stopwatch.ElapsedMilliseconds > limits.MaxRenderTimeMs)
                {
                    return new SvgRenderResult
                    {
                        Success = false,
                        ErrorMessage = $"SVG render exceeded time limit ({limits.MaxRenderTimeMs}ms)"
                    };
                }
                
                if (picture == null)
                {
                    return new SvgRenderResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to parse SVG"
                    };
                }
                
                var cullRect = picture.CullRect;
                
                return new SvgRenderResult
                {
                    Picture = picture,
                    Width = cullRect.Width,
                    Height = cullRect.Height,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = $"SVG render error: {ex.Message}"
                };
            }
        }
        
        /// <summary>
        /// Pre-validate SVG complexity before parsing.
        /// This catches complexity bombs early without full parsing.
        /// </summary>
        private bool ValidateSvgComplexity(string svgContent, SvgRenderLimits limits, out string error)
        {
            error = null;
            
            // Count elements (rough estimate)
            int elementCount = 0;
            int index = 0;
            while ((index = svgContent.IndexOf('<', index)) >= 0)
            {
                // Skip comments and processing instructions
                if (index + 1 < svgContent.Length && svgContent[index + 1] != '!' && svgContent[index + 1] != '?')
                {
                    elementCount++;
                }
                index++;
            }
            
            if (elementCount > limits.MaxElementCount)
            {
                error = $"SVG element count ({elementCount}) exceeds limit ({limits.MaxElementCount})";
                return false;
            }
            
            // Count filter elements
            int filterCount = CountOccurrences(svgContent, "<filter");
            if (filterCount > limits.MaxFilterCount)
            {
                error = $"SVG filter count ({filterCount}) exceeds limit ({limits.MaxFilterCount})";
                return false;
            }
            
            // Check for deep nesting (rough estimate using <g> tags)
            int maxDepth = EstimateMaxDepth(svgContent);
            if (maxDepth > limits.MaxRecursionDepth)
            {
                error = $"SVG nesting depth ({maxDepth}) exceeds limit ({limits.MaxRecursionDepth})";
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Strip external references (xlink:href, url()) pointing outside document.
        /// </summary>
        private string StripExternalReferences(string svgContent)
        {
            // Remove xlink:href to external URLs
            // Pattern: xlink:href="http..." or xlink:href="https..."
            svgContent = System.Text.RegularExpressions.Regex.Replace(
                svgContent,
                @"xlink:href\s*=\s*[""']https?://[^""']*[""']",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            // Remove href to external URLs
            svgContent = System.Text.RegularExpressions.Regex.Replace(
                svgContent,
                @"href\s*=\s*[""']https?://[^""']*[""']",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            // Remove url() references to external URLs
            svgContent = System.Text.RegularExpressions.Regex.Replace(
                svgContent,
                @"url\s*\(\s*[""']?https?://[^)""']*[""']?\s*\)",
                "url()",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            return svgContent;
        }
        
        private int CountOccurrences(string text, string pattern)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += pattern.Length;
            }
            return count;
        }
        
        private int EstimateMaxDepth(string svgContent)
        {
            // Simple heuristic: count nested <g> or <svg> tags
            int depth = 0;
            int maxDepth = 0;
            
            for (int i = 0; i < svgContent.Length - 3; i++)
            {
                if (svgContent[i] == '<')
                {
                    if (i + 2 < svgContent.Length && 
                        (svgContent[i + 1] == 'g' || svgContent[i + 1] == 'G') &&
                        (svgContent[i + 2] == ' ' || svgContent[i + 2] == '>' || svgContent[i + 2] == '/'))
                    {
                        depth++;
                        maxDepth = Math.Max(maxDepth, depth);
                    }
                    else if (i + 3 < svgContent.Length && svgContent[i + 1] == '/' &&
                             (svgContent[i + 2] == 'g' || svgContent[i + 2] == 'G'))
                    {
                        depth--;
                    }
                }
            }
            
            return maxDepth;
        }
    }
}
