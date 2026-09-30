using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

public sealed class TextLayoutHelperTypefaceTests
{
    // CSS Fonts 4 §5: a family that is not installed is skipped, and the next family is
    // matched at the requested weight. github.com's stack starts with "Mona Sans VF",
    // which pages only get from a web font; the painter took it as Segoe UI Regular.
    [Theory]
    [InlineData(600)]
    [InlineData(700)]
    public void UninstalledFirstFamily_FallsThroughAtTheRequestedWeight(int weight)
    {
        var expected = TextLayoutHelper.ResolveTypeface("Arial", "Sign in", weight);
        var resolved = TextLayoutHelper.ResolveTypeface("\"No Such Family Fen\", -apple-system, Arial", "Sign in", weight);

        Assert.Equal(expected.FamilyName, resolved.FamilyName);
        Assert.Equal(expected.FontStyle.Weight, resolved.FontStyle.Weight);
        Assert.True(resolved.FontStyle.Weight > (int)SKFontStyleWeight.Normal, $"{resolved.FamilyName} {resolved.FontStyle.Weight}");
    }
}
