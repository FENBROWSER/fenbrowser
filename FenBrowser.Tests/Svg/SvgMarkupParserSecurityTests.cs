using System.Collections.Generic;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// Adversarial coverage for the sandboxed SVG markup parser.
    /// Every case here is a class of real-world XML/SVG attack or a malformed
    /// input that must degrade gracefully instead of crashing or hanging.
    /// </summary>
    public class SvgMarkupParserSecurityTests
    {
        private static SvgRenderLimits FullLimits(
            int? elements = null,
            int? filters = null,
            int? depth = null)
        {
            return new SvgRenderLimits
            {
                MaxRecursionDepth = depth ?? 32,
                MaxFilterCount = filters ?? 10,
                MaxRenderTimeMs = 250,
                MaxElementCount = elements ?? 50000
            };
        }

        private static SvgParsedDocument Parse(string source, out bool ok, out string fatal)
        {
            ok = SvgMarkupParser.TryParse(source, FullLimits(), out var doc, out fatal);
            return doc;
        }

        [Fact]
        public void Doctype_IsRejected_FailClosed()
        {
            // Billion-laughs style payload: structurally impossible because the
            // parser has no entity machinery and rejects DOCTYPE outright.
            var billionLaughs = @"<?xml version=""1.0""?>
<!DOCTYPE lolz [
  <!ENTITY lol ""lol"">
  <!ENTITY lol2 ""&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"">
  <!ENTITY lol9 ""&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;"">
]>
<svg xmlns=""http://www.w3.org/2000/svg"" width=""10"" height=""10"">&lol9;</svg>";

            Assert.False(SvgMarkupParser.TryParse(billionLaughs, FullLimits(), out _, out var fatal));
            Assert.Contains("DOCTYPE", fatal);
        }

        [Fact]
        public void Doctype_AfterRootStart_AlsoRejected()
        {
            const string s = "<svg width=\"1\" height=\"1\"><!DOCTYPE svg></svg>";
            Assert.False(SvgMarkupParser.TryParse(s, FullLimits(), out _, out var fatal));
            Assert.Contains("DOCTYPE", fatal);
        }

        [Fact]
        public void NumericCharRef_InAttribute_IsExpanded()
        {
            var doc = Parse("<svg width=\"&#53;&#48;\" height=\"10\"/>", out var ok, out _);
            Assert.True(ok);
            Assert.NotNull(doc);
            Assert.Equal("50", doc.Root.GetAttribute("width"));
        }

        [Fact]
        public void PredefinedEntities_AreExpanded()
        {
            var doc = Parse(
                "<svg width=\"10\" height=\"10\"><desc>a&amp;b&lt;c&gt;d&quot;e&apos;f</desc></svg>",
                out var ok, out _);
            Assert.True(ok);
        }

        [Fact]
        public void TextContent_IsCapturedAndEntitiesAreBoundedToItsMarkupSegment()
        {
            var doc = Parse(
                "<svg><text>A&amp;B &unterminated</text><rect data-x=';'/></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal("A&B &unterminated", doc.Root.Children[0].TextContent);
            Assert.Equal(";", doc.Root.Children[1].GetAttribute("data-x"));
        }

        [Fact]
        public void OversizedTextContent_IsBoundedDuringMaterialization()
        {
            string source = "<svg><text>" +
                            new string('x', SvgMarkupParser.MaxTextContentChars + 1024) +
                            "</text></svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal(SvgMarkupParser.MaxTextContentChars, doc.Root.Children[0].TextContent.Length);
            Assert.Contains("text content truncated over length budget", doc.Report.Warnings);
        }

        [Fact]
        public void UnknownEntity_DegradesToLiteral_NeverFatal()
        {
            var doc = Parse("<svg width=\"10\" height=\"10\"><circle r=\"&nope;\"/></svg>", out var ok, out _);
            Assert.True(ok);
        }

        [Fact]
        public void HugeNumericRef_ClampsToReplacement_NoCrash()
        {
            var doc = Parse("<svg width=\"&#99999999999999;\" height=\"10\"/>", out var ok, out _);
            Assert.True(ok); // Malformed ref degrades; parse continues.
        }

        [Fact]
        public void SurrogateRangeNumericRef_Rejected()
        {
            var doc = Parse("<svg width=\"&#xD800;\" height=\"10\"/>", out var ok, out _);
            Assert.True(ok);
        }

        [Fact]
        public void DuplicateAttributes_FirstWins()
        {
            var doc = Parse(
                "<svg width=\"10\" height=\"10\"><rect width=\"5\" fill=\"red\" fill=\"blue\"/></svg>",
                out var ok, out _);
            Assert.True(ok);
            Assert.Equal("red", doc.Root.Children[0].GetAttribute("fill"));
            Assert.True(doc.Report.SawDuplicateAttribute);
        }

        [Fact]
        public void DuplicateIds_FirstWins()
        {
            var doc = Parse(
                "<svg width=\"10\" height=\"10\"><g id=\"x\"/><g id=\"x\"/></svg>",
                out var ok, out _);
            Assert.True(ok);
            Assert.Same(doc.Root.Children[0], doc.ElementsById["x"]);
            Assert.True(doc.Report.SawDuplicateId);
        }

        [Fact]
        public void ElementCount_Budget_TriggersSandboxViolation()
        {
            const string s = "<svg><g/><g/><g/><g/><g/></svg>";
            var ex = Record.Exception(() =>
                SvgMarkupParser.TryParse(s, FullLimits(elements: 3), out _, out _));
            Assert.IsType<SvgSandboxViolationException>(ex);
            Assert.Contains("element count", ex.Message);
        }

        [Fact]
        public void FilterCount_Budget_TriggersSandboxViolation()
        {
            const string s =
                "<svg><filter id=\"a\"/><filter id=\"b\"/><filter id=\"c\"/></svg>";
            var ex = Record.Exception(() =>
                SvgMarkupParser.TryParse(s, FullLimits(filters: 2), out _, out _));
            Assert.IsType<SvgSandboxViolationException>(ex);
            Assert.Contains("filter count", ex.Message);
        }

        [Fact]
        public void DepthBudget_TriggersSandboxViolation_BeforeStackOverflow()
        {
            var sb = new System.Text.StringBuilder("<svg>");
            for (int i = 0; i < 64; i++) sb.Append("<g>");
            for (int i = 0; i < 64; i++) sb.Append("</g>");
            sb.Append("</svg>");

            // Depth is enforced as a validation failure (false + reason), while
            // count budgets throw - both paths must be stack-safe.
            bool ok = SvgMarkupParser.TryParse(sb.ToString(), FullLimits(depth: 8), out _, out var fatal);
            Assert.False(ok);
            Assert.Contains("nesting depth", fatal);
        }

        [Fact]
        public void UnclosedTag_AtEof_IsDropped_Gracefully()
        {
            var doc = Parse("<svg width=\"10\" height=\"10\"><rect width=\"5\"", out var ok, out _);
            Assert.True(ok);
            Assert.Empty(doc.Root.Children);
        }

        [Fact]
        public void UnterminatedComment_ConsumesRest_NoHang()
        {
            const string s = "<svg width='10' height='10'><!-- never closed";
            Parse(s, out var ok, out _);
            Assert.True(ok);
        }

        [Fact]
        public void UnmatchedCloseTags_AreIgnored()
        {
            Parse("</weird></svg><svg width=\"1\" height=\"1\"></nothing></svg>", out var ok, out _);
            Assert.True(ok);
        }

        [Fact]
        public void Cdata_And_PI_AreSkipped()
        {
            Parse(
                "<?proc instr?><svg width=\"1\" height=\"1\"><![CDATA[ <notatag/> ]]></svg>",
                out var ok, out _);
            Assert.True(ok);
        }

        [Fact]
        public void NonSvgRoot_IsRejected()
        {
            Parse("<html><body/></html>", out var ok, out var fatal);
            Assert.False(ok);
            Assert.Contains("root element", fatal);
        }

        [Fact]
        public void OversizedQuotedAttribute_IsBoundedDuringMaterialization()
        {
            string source = "<svg data-x='" +
                            new string('a', SvgMarkupParser.MaxAttributeValueChars + 1024) +
                            "'></svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal(
                SvgMarkupParser.MaxAttributeValueChars,
                doc.Root.GetAttribute("data-x").Length);
            Assert.Contains(
                "attribute value truncated over length budget",
                doc.Report.Warnings);
        }

        [Fact]
        public void OversizedUnterminatedAttribute_IsBoundedDuringMaterialization()
        {
            string source = "<svg data-x='" +
                            new string('b', SvgMarkupParser.MaxAttributeValueChars + 1024);

            Parse(source, out var ok, out _);

            // An incomplete root is rejected, but the scanner must reach that
            // decision without allocating the full attacker-controlled suffix.
            Assert.False(ok);
        }
    }
}
