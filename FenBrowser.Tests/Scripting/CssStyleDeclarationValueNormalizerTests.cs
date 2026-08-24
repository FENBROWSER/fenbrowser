using System;
using System.Linq;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class CssStyleDeclarationValueNormalizerTests
{
    [Theory]
    [InlineData("cx", "0", "0px")]
    [InlineData("cy", "-1px", "-1px")]
    [InlineData("x", "calc(2em + 3ex)", "calc(2em + 3ex)")]
    [InlineData("y", "4%", "4%")]
    [InlineData("r", "5vmin", "5vmin")]
    [InlineData("rx", "auto", "auto")]
    [InlineData("ry", "var(--radius)", "var(--radius)")]
    public void Normalize_AcceptsSvgGeometryValues(string property, string value, string expected)
    {
        var result = CssStyleDeclarationValueNormalizer.Normalize(property, value, out var serialized);

        Assert.Equal(CssPropertyNormalizationResult.Valid, result);
        Assert.Equal(expected, serialized);
    }

    [Theory]
    [InlineData("cx", "10")]
    [InlineData("cx", "auto")]
    [InlineData("cy", "10px 20px")]
    [InlineData("r", "-1px")]
    [InlineData("rx", "-1px")]
    [InlineData("ry", "none")]
    [InlineData("x", "1unknown")]
    [InlineData("y", "calc()")]
    [InlineData("y", "calc(1 + 2)")]
    [InlineData("y", "calc(1px * 2px)")]
    [InlineData("y", "calc(1px / 2px)")]
    public void Normalize_RejectsInvalidSvgGeometryValues(string property, string value)
    {
        var result = CssStyleDeclarationValueNormalizer.Normalize(property, value, out _);

        Assert.Equal(CssPropertyNormalizationResult.Invalid, result);
    }

    [Fact]
    public void Normalize_RejectsExcessiveMathNesting()
    {
        var nested = string.Concat(Enumerable.Repeat("calc(", 65)) +
                     "1px" +
                     new string(')', 65);

        var result = CssStyleDeclarationValueNormalizer.Normalize("cx", nested, out _);

        Assert.Equal(CssPropertyNormalizationResult.Invalid, result);
    }

    [Fact]
    public void Normalize_RejectsOversizedValuesBeforeTokenization()
    {
        var oversized = new string('1', 64 * 1024 + 1) + "px";

        var result = CssStyleDeclarationValueNormalizer.Normalize("cx", oversized, out _);

        Assert.Equal(CssPropertyNormalizationResult.Invalid, result);
    }
}
