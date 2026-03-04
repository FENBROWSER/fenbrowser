using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
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
                // CRITICAL FIX: Inject default fill for paths without explicit fill
                // Per SVG spec, the default fill is "black", but Svg.Skia renders unfilled paths as transparent
                svgContent = InjectDefaultFill(svgContent);

                // Normalize duplicated attributes after all content transforms.
                svgContent = DeduplicateAttributes(svgContent);
                
                using var svg = new SKSvg();
                var picture = svg.FromSvg(svgContent);
                
                // Check render time limit
                if (stopwatch.ElapsedMilliseconds > limits.MaxRenderTimeMs)
                {
                    return new SvgRenderResult
                    {
                        Success = false,
                        ErrorMessage = $"SVG render exceeded time limit ({limits.MaxRenderTimeMs}ms, size={svgContent.Length/1024}KB)"
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
                
                // DEBUG: Log picture details to diagnose fill rendering issue
                FenBrowser.Core.FenLogger.Debug($"[SvgSkiaRenderer] SVG parsed. CullRect={cullRect.Width}x{cullRect.Height}", FenBrowser.Core.Logging.LogCategory.Rendering);
                
                // Check if CullRect is valid
                if (cullRect.Width <= 0 || cullRect.Height <= 0)
                {
                    FenBrowser.Core.FenLogger.Debug($"[SvgSkiaRenderer] WARNING: Invalid CullRect! SVG content first 200 chars: {svgContent.Substring(0, Math.Min(200, svgContent.Length))}", FenBrowser.Core.Logging.LogCategory.Rendering);
                }
                
                // CRITICAL FIX: Render to bitmap INSIDE the using scope BEFORE SKSvg is disposed
                // SKPicture's internal resources are tied to SKSvg and become invalid after disposal
                int bitmapWidth = (int)Math.Max(1, Math.Ceiling(cullRect.Width));
                int bitmapHeight = (int)Math.Max(1, Math.Ceiling(cullRect.Height));
                var bitmap = new SkiaSharp.SKBitmap(bitmapWidth, bitmapHeight);
                using (var canvas = new SkiaSharp.SKCanvas(bitmap))
                {
                    canvas.Clear(SkiaSharp.SKColors.Transparent);
                    // CullRect can have a non-zero origin (for example viewBox="0 -960 960 960").
                    // Shift into bitmap-local coordinates so geometry is not clipped away.
                    canvas.Translate(-cullRect.Left, -cullRect.Top);
                    canvas.DrawPicture(picture);
                }
                
                return new SvgRenderResult
                {
                    Picture = picture, // Keep for backward compat but may be invalid
                    Bitmap = bitmap,   // Pre-rendered bitmap that's safe to use
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
        
        /// <summary>
        /// Inject default fill="black" for SVG shape elements without explicit fill.
        /// Per SVG spec, default fill is black, but Svg.Skia renders unfilled paths as transparent.
        /// </summary>
        private static string InjectDefaultFill(string svgContent)
        {
            // Per-element fill injection: only add fill="currentColor" to shape elements
            // that don't already have a fill= attribute on that specific element.
            // Previous approach had two bugs:
            //  1) Early-returned if ANY element had fill=, skipping elements that didn't
            //  2) Only matched "<shape " with trailing space, missing newlines/self-closing
            var shapes = new[] { "path", "circle", "rect", "ellipse", "polygon", "polyline", "line" };

            foreach (var shape in shapes)
            {
                try
                {
                    // Regex matches: <shape followed by whitespace/>/> then attributes WITHOUT fill=, then closing
                    svgContent = Regex.Replace(
                        svgContent,
                        $@"<{shape}(?=[\s/>])((?:(?!fill\s*=)[^>])*?)(/?>)",
                        $@"<{shape} fill=""currentColor""$1$2",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline,
                        TimeSpan.FromMilliseconds(500));
                }
                catch (RegexMatchTimeoutException)
                {
                    // SVG too complex for regex — skip fill injection for this shape type
                    break;
                }
            }
            return svgContent;
        }

        /// <summary>
        /// Deduplicate attributes within SVG tags. 
        /// Strict XML/SVG parsers fail if they find two 'fill' attributes on the same element.
        /// </summary>
        private static string DeduplicateAttributes(string svgContent)
        {
            if (string.IsNullOrEmpty(svgContent)) return svgContent;

            try
            {
                // Regex to find tags and their interior content
                // <tag attr="val" attr2="val2">
                return System.Text.RegularExpressions.Regex.Replace(svgContent, @"<([a-zA-Z0-9_\-]+)\s+([^>]*?)(/?)>", m =>
                {
                    string tagName = m.Groups[1].Value;
                    string attrsArea = m.Groups[2].Value;
                    string selfClose = m.Groups[3].Value;

                    // Regex to match individual attributes: attr="val" or attr='val' or attr=val
                    var attrMatches = System.Text.RegularExpressions.Regex.Matches(attrsArea, 
                        @"(?<name>[a-zA-Z0-9_\-:]+)\s*=\s*(?:""(?<val>[^""]*)""|'(?<val>[^']*)'|(?<val>[^>\s]+))");

                    var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var uniqueAttrs = new System.Collections.Generic.List<string>();

                    foreach (System.Text.RegularExpressions.Match attr in attrMatches)
                    {
                        string name = attr.Groups["name"].Value;
                        if (seen.Add(name))
                        {
                            uniqueAttrs.Add(attr.Value);
                        }
                    }

                    // If we found attributes, rebuild the tag. 
                    // Note: This might strip non-attribute junk inside the tag, which is usually fine for SVGs.
                    if (uniqueAttrs.Count > 0)
                    {
                        var closeSuffix = string.IsNullOrEmpty(selfClose) ? ">" : "/>";
                        return $"<{tagName} {string.Join(" ", uniqueAttrs)}{closeSuffix}";
                    }

                    return m.Value; // No attributes found or parsing failure, return as is
                });
            }
            catch
            {
                return svgContent; // Fallback
            }
        }
    }
}


