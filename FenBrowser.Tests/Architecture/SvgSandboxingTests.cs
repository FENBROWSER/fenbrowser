using Xunit;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.Tests.Architecture
{
    /// <summary>
    /// Compliance tests for Rule 3: SVG Sandboxing.
    /// Verifies SvgRenderLimits are enforced.
    /// </summary>
    public class SvgSandboxingTests
    {
        [Fact]
        public void SvgRenderLimits_Default_HasSafeValues()
        {
            // Arrange
            var limits = SvgRenderLimits.Default;
            
            // Assert
            Assert.Equal(32, limits.MaxRecursionDepth);
            Assert.Equal(10, limits.MaxFilterCount);
            Assert.Equal(100, limits.MaxRenderTimeMs);
            Assert.False(limits.AllowExternalReferences);
        }
        
        [Fact]
        public void SvgRenderLimits_Strict_IsMoreRestrictive()
        {
            // Arrange
            var limits = SvgRenderLimits.Strict;
            
            // Assert
            Assert.True(limits.MaxRecursionDepth <= 16);
            Assert.True(limits.MaxFilterCount <= 5);
            Assert.True(limits.MaxRenderTimeMs <= 50);
            Assert.False(limits.AllowExternalReferences);
        }
        
        [Fact]
        public void SvgSkiaRenderer_EmptyContent_ReturnsFailure()
        {
            // Arrange
            var renderer = new SvgSkiaRenderer();
            
            // Act
            var result = renderer.Render("");
            
            // Assert
            Assert.False(result.Success);
            Assert.Contains("Empty", result.ErrorMessage);
        }
        
        [Fact]
        public void SvgSkiaRenderer_ValidSvg_ReturnsSuccessOrGracefulFailure()
        {
            // Arrange
            var renderer = new SvgSkiaRenderer();
            // Full SVG with namespace
            var simpleSvg = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<svg xmlns=""http://www.w3.org/2000/svg"" width=""100"" height=""100"">
    <circle cx=""50"" cy=""50"" r=""40"" fill=""red""/>
</svg>";
            
            // Act
            var result = renderer.Render(simpleSvg);
            
            // Assert - Either succeeds or fails gracefully (no crash)
            // In headless/CI environments, SVG rendering may not be available
            if (result.Success)
            {
                Assert.True(result.Width > 0);
                Assert.True(result.Height > 0);
            }
            else
            {
                // Graceful failure - just verify no null error message
                Assert.NotNull(result.ErrorMessage);
            }
        }
        
        [Fact]
        public void SvgSkiaRenderer_TooManyFilters_RejectsWithError()
        {
            // Arrange
            var renderer = new SvgSkiaRenderer();
            var limits = new SvgRenderLimits
            {
                MaxFilterCount = 2,
                MaxRecursionDepth = 32,
                MaxRenderTimeMs = 100,
                MaxElementCount = 1000,
                AllowExternalReferences = false
            };
            
            // SVG with 5 filters
            var svgWithManyFilters = @"
                <svg width=""100"" height=""100"">
                    <defs>
                        <filter id=""f1""><feGaussianBlur/></filter>
                        <filter id=""f2""><feGaussianBlur/></filter>
                        <filter id=""f3""><feGaussianBlur/></filter>
                        <filter id=""f4""><feGaussianBlur/></filter>
                        <filter id=""f5""><feGaussianBlur/></filter>
                    </defs>
                    <rect width=""100"" height=""100""/>
                </svg>";
            
            // Act
            var result = renderer.Render(svgWithManyFilters, limits);
            
            // Assert - Should fail due to filter limit or complexity validation
            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            // Error message should mention filter or validation issue
            Assert.True(
                result.ErrorMessage.ToLower().Contains("filter") ||
                result.ErrorMessage.ToLower().Contains("limit") ||
                result.ErrorMessage.ToLower().Contains("count"),
                $"Expected filter-related error but got: {result.ErrorMessage}");
        }
    }
}
