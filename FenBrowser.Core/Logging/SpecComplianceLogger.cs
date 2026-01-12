using System;
using FenBrowser.Core.Dom;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Spec Compliance Logger - Compares computed values against CSS spec defaults.
    /// Logs [SPEC-OK], [SPEC-MISMATCH], or [SPEC-WARN] based on deviation.
    /// </summary>
    public static class SpecComplianceLogger
    {
        // Tolerance for floating point comparison (1px difference is acceptable)
        private const float Tolerance = 1.0f;
        
        /// <summary>
        /// Log font size comparison against spec
        /// </summary>
        public static void LogFontSize(string tagName, float computedPx, float? inheritedBasePx = null)
        {
            if (string.IsNullOrEmpty(tagName)) return;
            
            float basePx = inheritedBasePx ?? CssSpecDefaults.BaseFontSizePx;
            
            if (CssSpecDefaults.Typography.TryGetValue(tagName, out var spec) && spec.FontSizePx.HasValue)
            {
                float expectedPx = spec.FontSizePx.Value;
                bool matches = Math.Abs(computedPx - expectedPx) <= Tolerance;
                
                string status = matches ? "SPEC-OK" : "SPEC-MISMATCH";
                FenLogger.Debug($"[{status}] <{tagName}> font-size: {computedPx:F1}px " +
                    $"(spec: {spec.FontSizeSpec} = {expectedPx:F1}px @ {basePx}px base)", 
                    LogCategory.Layout);
            }
        }
        
        /// <summary>
        /// Log font weight comparison against spec
        /// </summary>
        public static void LogFontWeight(string tagName, int computedWeight)
        {
            if (string.IsNullOrEmpty(tagName)) return;
            
            if (CssSpecDefaults.Typography.TryGetValue(tagName, out var spec) && spec.FontWeight.HasValue)
            {
                int expectedWeight = spec.FontWeight.Value;
                bool matches = computedWeight == expectedWeight;
                
                string status = matches ? "SPEC-OK" : "SPEC-MISMATCH";
                FenLogger.Debug($"[{status}] <{tagName}> font-weight: {computedWeight} " +
                    $"(expected: {expectedWeight})", LogCategory.Layout);
            }
        }
        
        /// <summary>
        /// Log margin comparison against spec
        /// </summary>
        public static void LogMargin(string tagName, float top, float right, float bottom, float left)
        {
            if (string.IsNullOrEmpty(tagName)) return;
            
            if (CssSpecDefaults.BoxModel.TryGetValue(tagName, out var spec))
            {
                bool topOk = Math.Abs(top - spec.MarginTop) <= Tolerance;
                bool rightOk = Math.Abs(right - spec.MarginRight) <= Tolerance;
                bool bottomOk = Math.Abs(bottom - spec.MarginBottom) <= Tolerance;
                bool leftOk = Math.Abs(left - spec.MarginLeft) <= Tolerance;
                
                bool allOk = topOk && rightOk && bottomOk && leftOk;
                string status = allOk ? "SPEC-OK" : "SPEC-MISMATCH";
                
                FenLogger.Debug($"[{status}] <{tagName}> margin: {top:F0} {right:F0} {bottom:F0} {left:F0} " +
                    $"(expected: {spec.MarginTop:F0} {spec.MarginRight:F0} {spec.MarginBottom:F0} {spec.MarginLeft:F0})", 
                    LogCategory.Layout);
            }
        }
        
        /// <summary>
        /// Log padding comparison against spec
        /// </summary>
        public static void LogPadding(string tagName, float top, float right, float bottom, float left)
        {
            if (string.IsNullOrEmpty(tagName)) return;
            
            if (CssSpecDefaults.BoxModel.TryGetValue(tagName, out var spec))
            {
                bool topOk = Math.Abs(top - spec.PaddingTop) <= Tolerance;
                bool rightOk = Math.Abs(right - spec.PaddingRight) <= Tolerance;
                bool bottomOk = Math.Abs(bottom - spec.PaddingBottom) <= Tolerance;
                bool leftOk = Math.Abs(left - spec.PaddingLeft) <= Tolerance;
                
                bool allOk = topOk && rightOk && bottomOk && leftOk;
                
                // Only log if there's a mismatch for padding (less noise)
                if (!allOk)
                {
                    FenLogger.Debug($"[SPEC-MISMATCH] <{tagName}> padding: {top:F0} {right:F0} {bottom:F0} {left:F0} " +
                        $"(expected: {spec.PaddingTop:F0} {spec.PaddingRight:F0} {spec.PaddingBottom:F0} {spec.PaddingLeft:F0})", 
                        LogCategory.Layout);
                }
            }
        }
        
        /// <summary>
        /// Log contentBox validity - flags negative dimensions
        /// </summary>
        public static void LogContentBox(string tagName, string identifier, float width, float height)
        {
            if (width < 0 || height < 0)
            {
                FenLogger.Warn($"[SPEC-WARN] <{tagName}#{identifier}> contentBox: {width:F0}x{height:F0} " +
                    $"(NEGATIVE dimension - box model calculation error)", LogCategory.Layout);
            }
        }
        
        /// <summary>
        /// Log box size calculation showing input → output
        /// </summary>
        public static void LogBoxCalculation(string tagName, string identifier, 
            float borderBoxWidth, float borderBoxHeight,
            float paddingLeft, float paddingRight, float paddingTop, float paddingBottom,
            float contentWidth, float contentHeight)
        {
            // Only log if there's a discrepancy
            float expectedContentWidth = borderBoxWidth - paddingLeft - paddingRight;
            float expectedContentHeight = borderBoxHeight - paddingTop - paddingBottom;
            
            if (Math.Abs(contentWidth - expectedContentWidth) > Tolerance ||
                Math.Abs(contentHeight - expectedContentHeight) > Tolerance)
            {
                FenLogger.Debug($"[SPEC-CALC] <{tagName}#{identifier}> " +
                    $"border-box={borderBoxWidth:F0}x{borderBoxHeight:F0} " +
                    $"padding=({paddingLeft:F0},{paddingTop:F0},{paddingRight:F0},{paddingBottom:F0}) " +
                    $"→ content={contentWidth:F0}x{contentHeight:F0} " +
                    $"(expected: {expectedContentWidth:F0}x{expectedContentHeight:F0})", 
                    LogCategory.Layout);
            }
        }
        
        /// <summary>
        /// Log flex item dimension issues
        /// </summary>
        public static void LogFlexItem(string tagName, string className, float width, float height)
        {
            // Many flex items legitimately have 0 size (hidden elements, spacers, items with flex-grow)
            // Log as DEBUG instead of WARN to reduce noise in logs
            if (width <= 0 || height <= 0)
            {
                FenLogger.Debug($"[FLEX-ZERO] <{tagName}.{className}> flex-item: {width:F0}x{height:F0} " +
                    $"(zero/negative size - may be intentional for hidden/growable items)", LogCategory.Layout);
            }
        }
        
        /// <summary>
        /// Log unit conversion for debugging
        /// </summary>
        public static void LogUnitConversion(string property, string rawValue, float computedPx, float basePx)
        {
            FenLogger.Debug($"[UNIT] {property}: {rawValue} → {computedPx:F1}px (base: {basePx}px)", 
                LogCategory.Layout);
        }
    }
}
