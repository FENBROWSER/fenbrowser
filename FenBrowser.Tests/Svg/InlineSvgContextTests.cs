using System.Collections.Generic;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class InlineSvgContextTests
    {
        [Fact]
        public void OpaqueForeground_IsEmittedAsHexColor()
        {
            string declarations = Build("", SKColors.Lime, null, out _);

            Assert.Equal("color: #00FF00;", declarations);
        }

        [Fact]
        public void TranslucentForeground_KeepsItsAlpha()
        {
            string declarations = Build("", new SKColor(255, 0, 0, 128), null, out _);

            Assert.Equal("color: rgba(255, 0, 0, 0.502);", declarations);
        }

        [Fact]
        public void OnlyReferencedCustomProperties_AreEmitted()
        {
            var custom = new Dictionary<string, string>
            {
                ["--brand"] = "#123456",
                ["--unused"] = "red"
            };

            string declarations = Build("fill: var(--brand)", SKColors.Black, custom, out int withheld);

            Assert.Contains("--brand: #123456;", declarations);
            Assert.DoesNotContain("--unused", declarations);
            Assert.Equal(0, withheld);
        }

        [Fact]
        public void TransitivelyReferencedCustomProperties_AreEmitted()
        {
            var custom = new Dictionary<string, string>
            {
                ["--icon"] = "var(--base)",
                ["--base"] = "green"
            };

            string declarations = Build("var( --icon)", SKColors.Black, custom, out _);

            Assert.Contains("--icon: var(--base);", declarations);
            Assert.Contains("--base: green;", declarations);
        }

        [Fact]
        public void CyclicCustomProperties_TerminateAndEmitEachOnce()
        {
            var custom = new Dictionary<string, string>
            {
                ["--a"] = "var(--b)",
                ["--b"] = "var(--a)"
            };

            string declarations = Build("var(--a)", SKColors.Black, custom, out _);

            Assert.Equal(1, Count(declarations, "--a:"));
            Assert.Equal(1, Count(declarations, "--b:"));
        }

        [Theory]
        [InlineData("red; fill: url(#evil)")]
        [InlineData("red } rect { fill: blue")]
        [InlineData("red !important")]
        [InlineData("red /* x")]
        [InlineData("rgb(1, 2, 3")]
        [InlineData("url(a))")]
        [InlineData("'unterminated")]
        [InlineData("a\\3b b")]
        [InlineData("line\nbreak")]
        public void ValuesThatCouldLeaveTheirDeclaration_AreWithheld(string value)
        {
            var custom = new Dictionary<string, string> { ["--x"] = value };

            string declarations = Build("var(--x)", SKColors.Black, custom, out int withheld);

            Assert.DoesNotContain("--x", declarations);
            Assert.Equal(1, withheld);
        }

        [Theory]
        [InlineData("rgb(1 2 3 / 50%)")]
        [InlineData("'Segoe UI', sans-serif")]
        [InlineData("calc(1px + 2em)")]
        [InlineData("url(\"icon.svg#a\")")]
        public void OrdinaryValues_AreEmitted(string value)
        {
            var custom = new Dictionary<string, string> { ["--x"] = value };

            string declarations = Build("var(--x)", SKColors.Black, custom, out int withheld);

            Assert.Contains("--x: " + value + ";", declarations);
            Assert.Equal(0, withheld);
        }

        [Fact]
        public void CustomPropertyCount_IsBounded()
        {
            var custom = new Dictionary<string, string>();
            var references = new System.Text.StringBuilder();
            for (int i = 0; i < InlineSvgContext.MaxCustomProperties + 10; i++)
            {
                custom["--p" + i] = "red";
                references.Append("var(--p").Append(i).Append(") ");
            }

            string declarations = Build(references.ToString(), SKColors.Black, custom, out int withheld);

            Assert.Equal(InlineSvgContext.MaxCustomProperties, Count(declarations, ": red;"));
            Assert.True(withheld > 0);
        }

        [Fact]
        public void OversizedValue_IsWithheld()
        {
            var custom = new Dictionary<string, string>
            {
                ["--big"] = new string('a', InlineSvgContext.MaxCustomPropertyValueChars + 1)
            };

            string declarations = Build("var(--big)", SKColors.Black, custom, out int withheld);

            Assert.DoesNotContain("--big", declarations);
            Assert.Equal(1, withheld);
        }

        private static string Build(
            string references,
            SKColor foreground,
            IReadOnlyDictionary<string, string>? custom,
            out int withheld) =>
            InlineSvgContext.BuildRootDeclarations(references, foreground, custom!, out withheld);

        private static int Count(string text, string fragment)
        {
            int count = 0;
            for (int i = text.IndexOf(fragment, System.StringComparison.Ordinal);
                 i >= 0;
                 i = text.IndexOf(fragment, i + fragment.Length, System.StringComparison.Ordinal))
            {
                count++;
            }
            return count;
        }
    }
}
