using Xunit;
using SkiaSharp;
using FenBrowser.FenEngine.Typography;

namespace FenBrowser.Tests.Architecture
{
    /// <summary>
    /// Compliance tests for Rule 2: Metrics Separation.
    /// Verifies FenEngine controls line-height, not Skia.
    /// </summary>
    public class NormalizedFontMetricsTests
    {
        [Fact]
        public void FromSkia_WithNullLineHeight_UsesDefaultMultiplier()
        {
            // Arrange
            var typeface = SKTypeface.Default;
            using var font = new SKFont(typeface, 16f);
            var skMetrics = font.Metrics;
            
            // Act
            var metrics = NormalizedFontMetrics.FromSkia(skMetrics, 16f, null);
            
            // Assert
            // Default is 1.4x font size for comfortable reading
            Assert.True(metrics.LineHeight >= 16f);
            Assert.True(metrics.LineHeight <= 32f); // Sane upper bound
        }
        
        [Fact]
        public void FromSkia_WithExplicitLineHeight_UsesExactValue()
        {
            // Arrange
            var typeface = SKTypeface.Default;
            using var font = new SKFont(typeface, 16f);
            var skMetrics = font.Metrics;
            float explicitLineHeight = 24f;
            
            // Act
            var metrics = NormalizedFontMetrics.FromSkia(skMetrics, 16f, explicitLineHeight);
            
            // Assert - FenEngine decided line height, not Skia
            Assert.Equal(24f, metrics.LineHeight);
        }
        
        [Fact]
        public void FromSkia_LineHeightMultiplier_AppliesCorrectly()
        {
            // Arrange
            var typeface = SKTypeface.Default;
            using var font = new SKFont(typeface, 20f);
            var skMetrics = font.Metrics;
            float multiplier = 1.5f; // Line height as multiplier < 3
            
            // Act
            var metrics = NormalizedFontMetrics.FromSkia(skMetrics, 20f, multiplier);
            
            // Assert
            Assert.Equal(30f, metrics.LineHeight); // 20 * 1.5 = 30
        }
        
        [Fact]
        public void GetBaselineOffset_ReturnsPositiveValue()
        {
            // Arrange
            var typeface = SKTypeface.Default;
            using var font = new SKFont(typeface, 16f);
            var skMetrics = font.Metrics;
            
            // Act
            var metrics = NormalizedFontMetrics.FromSkia(skMetrics, 16f, null);
            float baselineOffset = metrics.GetBaselineOffset();
            
            // Assert
            Assert.True(baselineOffset > 0);
        }
        
        [Fact]
        public void EmSize_EqualsFontSize()
        {
            // Arrange
            var typeface = SKTypeface.Default;
            using var font = new SKFont(typeface, 18f);
            var skMetrics = font.Metrics;
            
            // Act
            var metrics = NormalizedFontMetrics.FromSkia(skMetrics, 18f, null);
            
            // Assert
            Assert.Equal(18f, metrics.EmSize);
        }

        [Fact]
        public void NormalizeContentMetrics_WithPathologicalMetrics_ClampsContentHeightToSaneRange()
        {
            var (ascent, descent) = NormalizedFontMetrics.NormalizeContentMetrics(16f, 96f, 20f);
            var contentHeight = ascent + descent;

            Assert.InRange(contentHeight, 9.6f, 21.6f);
            Assert.True(ascent > descent);
        }
    }
}
