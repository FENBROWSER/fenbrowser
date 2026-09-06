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
        public SvgRenderResult Render(SvgRenderRequest request)
        {
            if (request == null)
                return new SvgRenderResult { Success = false, ErrorMessage = "SVG render request is null", Backend = SvgRendererBackend.LegacySvgSkia };
            if (request.DocumentTimeSeconds != 0d)
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = "Svg.Skia cannot sample a requested SVG document time",
                    Backend = SvgRendererBackend.LegacySvgSkia
                };
            return Render(request.Content, request.Limits);
        }

        private static readonly Lazy<bool> SvgBackendInitialized =
            new(WarmUpSvgBackend, isThreadSafe: true);
        private static readonly TimeSpan SanitizerRegexTimeout = TimeSpan.FromMilliseconds(250);

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

            limits = SvgRenderLimits.Normalize(limits);
            
            // Pre-validation: Check for complexity bombs before sending untrusted text
            // into the SVG parser/native renderer.
            if (!ValidateSvgComplexity(svgContent, limits, out string validationError))
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = validationError
                };
            }
            
            // Strip external references if not allowed. Svg.Skia must never become a
            // second network/file loader that bypasses the browser fetch policy.
            if (!limits.AllowExternalReferences)
            {
                svgContent = StripExternalReferences(svgContent);
            }

            // Svg.Skia performs one-time parser/native initialization on its
            // first FromSvg call. That process-wide cold-start cost is unrelated
            // to the complexity of untrusted input and must not consume the
            // per-document render budget.
            _ = SvgBackendInitialized.Value;
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                // CRITICAL FIX: Inject default fill for paths without explicit fill
                // Per SVG spec, the default fill is "black", but Svg.Skia renders unfilled paths as transparent
                svgContent = InjectDefaultFill(svgContent);
                // Ensure intrinsic viewport size exists when only viewBox is provided.
                // Some Svg.Skia code paths can produce empty output if width/height are missing.
                svgContent = NormalizeSvgViewport(svgContent);

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
                if (!float.IsFinite(cullRect.Width) || !float.IsFinite(cullRect.Height) ||
                    !float.IsFinite(cullRect.Left) || !float.IsFinite(cullRect.Top))
                {
                    return new SvgRenderResult
                    {
                        Success = false,
                        ErrorMessage = "SVG produced non-finite raster bounds"
                    };
                }

                FenBrowser.Core.EngineLogCompat.Debug(
                    $"[SvgSkiaRenderer] SVG parsed. CullRect={cullRect.Width}x{cullRect.Height}",
                    FenBrowser.Core.Logging.LogCategory.Rendering);
                
                // Render to bitmap INSIDE the using scope BEFORE SKSvg is disposed.
                // Validate dimensions before allocating native bitmap memory. A compact
                // SVG can otherwise declare a gigantic viewport/cull rect and request
                // hundreds of megabytes or more before ImageLoader cache limits run.
                double rasterWidth = Math.Max(1d, Math.Ceiling(cullRect.Width));
                double rasterHeight = Math.Max(1d, Math.Ceiling(cullRect.Height));
                if (rasterWidth > limits.MaxRasterWidth || rasterHeight > limits.MaxRasterHeight ||
                    rasterWidth * rasterHeight > limits.MaxRasterPixels)
                {
                    return new SvgRenderResult
                    {
                        Success = false,
                        ErrorMessage =
                            $"SVG raster bounds {rasterWidth:0}x{rasterHeight:0} exceed browser limits " +
                            $"({limits.MaxRasterWidth}x{limits.MaxRasterHeight}, {limits.MaxRasterPixels} pixels)"
                    };
                }

                int bitmapWidth = checked((int)rasterWidth);
                int bitmapHeight = checked((int)rasterHeight);
                var bitmap = new SKBitmap(bitmapWidth, bitmapHeight);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    // CullRect can have a non-zero origin (for example viewBox="0 -960 960 960").
                    // Shift into bitmap-local coordinates so geometry is not clipped away.
                    canvas.Translate(-cullRect.Left, -cullRect.Top);
                    canvas.DrawPicture(picture);
                }
                
                return new SvgRenderResult
                {
                    // SKSvg owns the picture lifetime. Returning it after the
                    // using scope would expose an invalid native handle.
                    Picture = null,
                    Bitmap = bitmap,
                    Width = cullRect.Width,
                    Height = cullRect.Height,
                    Success = true,
                    Backend = SvgRendererBackend.LegacySvgSkia
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

        private static bool WarmUpSvgBackend()
        {
            try
            {
                using var svg = new SKSvg();
                using var picture = svg.FromSvg(
                    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1\" height=\"1\"><path d=\"M0 0h1v1H0z\"/></svg>");
                return picture != null;
            }
            catch
            {
                // The real render returns the actionable backend error.
                return false;
            }
        }
        
        /// <summary>
        /// Pre-validate SVG complexity before parsing.
        /// This catches complexity bombs early without full parsing.
        /// </summary>
        private static bool ValidateSvgComplexity(string svgContent, SvgRenderLimits limits, out string error)
        {
            error = null;

            if (svgContent.Length > limits.MaxSourceChars)
            {
                error = $"SVG source length ({svgContent.Length}) exceeds limit ({limits.MaxSourceChars})";
                return false;
            }
            
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
            
            // Check for deep nesting (rough estimate using grouping elements).
            int maxDepth = EstimateMaxDepth(svgContent);
            if (maxDepth > limits.MaxRecursionDepth)
            {
                error = $"SVG nesting depth ({maxDepth}) exceeds limit ({limits.MaxRecursionDepth})";
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Remove references that could cause Svg.Skia to perform its own external
        /// resource access. Fragment-local references and inline data: payloads remain.
        /// </summary>
        private static string StripExternalReferences(string svgContent)
        {
            try
            {
                svgContent = Regex.Replace(
                    svgContent,
                    @"\b(?:xlink:href|href)\s*=\s*(?<q>[""'])(?<value>.*?)\k<q>",
                    match => IsInlineSvgReference(match.Groups["value"].Value) ? match.Value : string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.Singleline,
                    SanitizerRegexTimeout);

                svgContent = Regex.Replace(
                    svgContent,
                    @"url\s*\(\s*(?<value>(?:[""'][^""']*[""']|[^)]*))\s*\)",
                    match =>
                    {
                        var value = match.Groups["value"].Value.Trim().Trim('"', '\'');
                        return IsInlineSvgReference(value) ? match.Value : "url()";
                    },
                    RegexOptions.IgnoreCase | RegexOptions.Singleline,
                    SanitizerRegexTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                // Sanitization must fail closed. Returning an empty document prevents a
                // pathological attribute string from escaping into Svg.Skia unsanitized.
                return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1\" height=\"1\"/>";
            }

            return svgContent;
        }

        private static bool IsInlineSvgReference(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var trimmed = value.Trim();
            return trimmed.StartsWith("#", StringComparison.Ordinal) ||
                   trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        }
        
        private static int CountOccurrences(string text, string pattern)
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
        
        private static int EstimateMaxDepth(string svgContent)
        {
            // Simple heuristic: count nested <g> tags. This remains a cheap prescan;
            // the source/element/raster limits provide independent safety boundaries.
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
            string inheritedFill = ResolveSvgRootFill(svgContent);
            if (string.Equals(inheritedFill, "none", StringComparison.OrdinalIgnoreCase))
            {
                return svgContent;
            }

            string fallbackFill = string.IsNullOrWhiteSpace(inheritedFill) ||
                string.Equals(inheritedFill, "currentColor", StringComparison.OrdinalIgnoreCase)
                    ? "black"
                    : inheritedFill;
            var shapes = new[] { "path", "circle", "rect", "ellipse", "polygon", "polyline", "line" };

            foreach (var shape in shapes)
            {
                try
                {
                    svgContent = Regex.Replace(
                        svgContent,
                        $@"<{shape}(?=[\s/>])((?:(?!fill\s*=)[^>])*?)(/?>)",
                        $@"<{shape} fill=""{fallbackFill}""$1$2",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline,
                        TimeSpan.FromMilliseconds(500));
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }
            }
            return svgContent;
        }

        private static string ResolveSvgRootFill(string svgContent)
        {
            if (string.IsNullOrWhiteSpace(svgContent))
            {
                return null;
            }

            var svgTagMatch = Regex.Match(
                svgContent,
                @"<svg\b(?<attrs>[^>]*)>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(500));

            if (!svgTagMatch.Success)
            {
                return null;
            }

            string attrs = svgTagMatch.Groups["attrs"].Value;
            var fillMatch = Regex.Match(
                attrs,
                @"\bfill\s*=\s*([""'])(?<value>.*?)\1",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(100));

            if (fillMatch.Success)
            {
                return fillMatch.Groups["value"].Value.Trim();
            }

            var styleMatch = Regex.Match(
                attrs,
                @"\bstyle\s*=\s*([""'])(?<value>.*?)\1",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(100));

            if (styleMatch.Success)
            {
                var styleFillMatch = Regex.Match(
                    styleMatch.Groups["value"].Value,
                    @"(?:^|;)\s*fill\s*:\s*(?<value>[^;]+)",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(100));

                if (styleFillMatch.Success)
                {
                    return styleFillMatch.Groups["value"].Value.Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// Normalize root <svg> sizing by deriving width/height from viewBox when absent.
        /// This keeps rendering deterministic for SVGs that rely on intrinsic viewBox dimensions.
        /// </summary>
        private static string NormalizeSvgViewport(string svgContent)
        {
            if (string.IsNullOrWhiteSpace(svgContent))
            {
                return svgContent;
            }

            var svgTagMatch = Regex.Match(svgContent, @"<svg\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(500));
            if (!svgTagMatch.Success)
            {
                return svgContent;
            }

            string attrs = svgTagMatch.Groups["attrs"].Value;
            string widthValue = GetSvgRootAttribute(attrs, "width");
            string heightValue = GetSvgRootAttribute(attrs, "height");
            bool hasConcreteWidth = IsConcreteViewportLength(widthValue);
            bool hasConcreteHeight = IsConcreteViewportLength(heightValue);

            var viewBoxMatch = Regex.Match(attrs,
                @"\bviewBox\s*=\s*[""']\s*(?<minx>[-+]?\d*\.?\d+)\s*[, ]\s*(?<miny>[-+]?\d*\.?\d+)\s*[, ]\s*(?<w>[-+]?\d*\.?\d+)\s*[, ]\s*(?<h>[-+]?\d*\.?\d+)\s*[""']",
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(500));

            if (!viewBoxMatch.Success)
            {
                return svgContent;
            }

            string width = viewBoxMatch.Groups["w"].Value;
            string height = viewBoxMatch.Groups["h"].Value;

            if (!hasConcreteWidth)
            {
                attrs = SetSvgRootAttribute(attrs, "width", width);
            }

            if (!hasConcreteHeight)
            {
                attrs = SetSvgRootAttribute(attrs, "height", height);
            }

            string normalizedSvgTag = "<svg" + attrs + ">";
            return svgContent.Remove(svgTagMatch.Index, svgTagMatch.Length)
                             .Insert(svgTagMatch.Index, normalizedSvgTag);
        }

        private static string GetSvgRootAttribute(string attrs, string name)
        {
            if (string.IsNullOrWhiteSpace(attrs) || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var match = Regex.Match(
                attrs,
                $@"\b{Regex.Escape(name)}\s*=\s*([""'])(?<value>.*?)\1",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(100));

            return match.Success ? match.Groups["value"].Value.Trim() : null;
        }

        private static bool IsConcreteViewportLength(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 2).Trim();
            }
            else if (trimmed.EndsWith("em", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.EndsWith("rem", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.EndsWith("%", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return float.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
                   parsed > 0f;
        }

        private static string SetSvgRootAttribute(string attrs, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return attrs;
            }

            string replacement = $@"{name}=""{value}""";
            string pattern = $@"\b{Regex.Escape(name)}\s*=\s*([""']).*?\1";
            if (Regex.IsMatch(attrs ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100)))
            {
                return Regex.Replace(
                    attrs,
                    pattern,
                    replacement,
                    RegexOptions.IgnoreCase | RegexOptions.Singleline,
                    TimeSpan.FromMilliseconds(100));
            }

            return (attrs ?? string.Empty) + " " + replacement;
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
                return Regex.Replace(
                    svgContent,
                    @"<([a-zA-Z0-9_\-]+)\s+([^>]*?)(/?)>",
                    m =>
                    {
                        string tagName = m.Groups[1].Value;
                        string attrsArea = m.Groups[2].Value;
                        string selfClose = m.Groups[3].Value;

                        var attrMatches = Regex.Matches(
                            attrsArea,
                            @"(?<name>[a-zA-Z0-9_\-:]+)\s*=\s*(?:""(?<val>[^""]*)""|'(?<val>[^']*)'|(?<val>[^>\s]+))",
                            RegexOptions.None,
                            SanitizerRegexTimeout);

                        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var uniqueAttrs = new System.Collections.Generic.List<string>();

                        foreach (Match attr in attrMatches)
                        {
                            string name = attr.Groups["name"].Value;
                            if (seen.Add(name))
                            {
                                uniqueAttrs.Add(attr.Value);
                            }
                        }

                        if (uniqueAttrs.Count > 0)
                        {
                            var closeSuffix = string.IsNullOrEmpty(selfClose) ? ">" : "/>";
                            return $"<{tagName} {string.Join(" ", uniqueAttrs)}{closeSuffix}";
                        }

                        return m.Value;
                    },
                    RegexOptions.Singleline,
                    SanitizerRegexTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                // Keep the already-sanitized source rather than retrying without limits.
                return svgContent;
            }
        }
    }
}
