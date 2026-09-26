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
            // Billion-laughs style payload: the bounded internal subset only
            // honours general entities that resolve to declared names, so the
            // forward reference to lol8 is refused while lol9 is declared.
            var billionLaughs = @"<?xml version=""1.0""?>
<!DOCTYPE lolz [
  <!ENTITY lol ""lol"">
  <!ENTITY lol2 ""&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"">
  <!ENTITY lol9 ""&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;"">
]>
<svg xmlns=""http://www.w3.org/2000/svg"" width=""10"" height=""10"">&lol9;</svg>";

            Assert.False(SvgMarkupParser.TryParse(billionLaughs, FullLimits(), out _, out var fatal));
            Assert.Contains("DOCTYPE", fatal);
            Assert.Contains("undeclared entity reference", fatal);
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

        /// <summary>
        /// WPT svg/text/reftests/textpath-path-attr*.svg, textpath-side-*.svg,
        /// textpath-shape-001.svg and path/distance/pathLength-*.svg all write the
        /// glyph run as direct character data inside the textPath rather than in a
        /// tspan, so the same entity decoding and budget apply there.
        /// </summary>
        [Fact]
        public void TextPathCharacterData_IsRetainedAndEntitiesAreDecoded()
        {
            var doc = Parse(
                "<svg><text><textPath href='#p'>A&amp;B&#65; &unterminated</textPath></text></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var textPath = doc.Root.Children[0].Children[0];
            Assert.Equal("textPath", textPath.Name);
            Assert.Equal("A&BA &unterminated", textPath.TextContent);
            Assert.Equal("A&BA &unterminated", Assert.Single(textPath.Content).Text);
        }

        [Fact]
        public void LinkCharacterData_IsRetainedAndEntitiesAreDecoded()
        {
            var doc = Parse(
                "<svg><text><a href='#x'>A&amp;B</a></text></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var link = doc.Root.Children[0].Children[0];
            Assert.Equal("a", link.Name);
            Assert.Equal("A&B", link.TextContent);
            Assert.Equal("A&B", Assert.Single(link.Content).Text);
        }

        [Fact]
        public void CharacterDataContainers_KeepTextAndChildrenInSourceOrder()
        {
            var doc = Parse(
                "<svg><text><textPath href='#p'>one<tspan>two</tspan>three</textPath></text></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var textPath = doc.Root.Children[0].Children[0];
            Assert.Equal("onethree", textPath.TextContent);
            Assert.Equal(3, textPath.Content.Count);
            Assert.Equal("one", textPath.Content[0].Text);
            Assert.Equal("tspan", textPath.Content[1].Element.Name);
            Assert.Equal("three", textPath.Content[2].Text);
            Assert.Equal("two", textPath.Content[1].Element.TextContent);
        }

        [Fact]
        public void OversizedTextPathCharacterData_IsBoundedDuringMaterialization()
        {
            string source = "<svg><text><textPath href='#p'>" +
                            new string('x', SvgMarkupParser.MaxTextContentChars + 1024) +
                            "</textPath></text></svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            var textPath = doc.Root.Children[0].Children[0];
            Assert.Equal(SvgMarkupParser.MaxTextContentChars, textPath.TextContent.Length);
            Assert.Contains("text content truncated over length budget", doc.Report.Warnings);
        }

        [Fact]
        public void OversizedLinkCharacterData_IsBoundedDuringMaterialization()
        {
            string source = "<svg><text><a href='#x'>" +
                            new string('y', SvgMarkupParser.MaxTextContentChars + 1024) +
                            "</a></text></svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            var link = doc.Root.Children[0].Children[0];
            Assert.Equal(SvgMarkupParser.MaxTextContentChars, link.TextContent.Length);
            Assert.Contains("text content truncated over length budget", doc.Report.Warnings);
        }

        [Fact]
        public void Cdata_InsideACharacterDataContainer_FailsClosed()
        {
            var doc = Parse(
                "<svg><text><textPath href='#p'><![CDATA[ABCD]]></textPath></text></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            Assert.True(doc.Report.UnsupportedFeatureIgnored);
            Assert.Contains(
                "CDATA text requires compatibility fallback", doc.Report.Warnings);
            Assert.Null(doc.Root.Children[0].Children[0].TextContent);
        }

        [Fact]
        public void Doctype_InsideACharacterDataContainer_IsStillRejected()
        {
            const string s = "<svg><text><textPath href='#p'><!DOCTYPE x>ABCD</textPath></text></svg>";

            Assert.False(SvgMarkupParser.TryParse(s, FullLimits(), out _, out var fatal));
            Assert.Contains("DOCTYPE", fatal);
        }

        [Fact]
        public void ForeignNamespaceTextContainers_DoNotRetainCharacterData()
        {
            var doc = Parse(
                "<svg xmlns='http://www.w3.org/2000/svg' " +
                "xmlns:h='http://www.w3.org/1999/xhtml'>" +
                "<h:text><h:textPath href='#p'>ABCD</h:textPath><h:a href='#x'>EFGH</h:a></h:text>" +
                "</svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var foreignText = doc.Root.Children[0];
            Assert.NotEqual("text", foreignText.Name);
            Assert.Null(foreignText.TextContent);
            Assert.Empty(foreignText.Content);
            Assert.Null(foreignText.Children[0].TextContent);
            Assert.Empty(foreignText.Children[0].Content);
            Assert.Null(foreignText.Children[1].TextContent);
            Assert.Empty(foreignText.Children[1].Content);
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
        public void DefaultFilterCount_AdmitsTwelveIndependentDefinitions()
        {
            var source = new System.Text.StringBuilder("<svg><defs>");
            for (int i = 0; i < 12; i++)
                source.Append("<filter id='f").Append(i).Append("'><feOffset/></filter>");
            source.Append("</defs></svg>");

            bool ok = SvgMarkupParser.TryParse(
                source.ToString(), SvgRenderLimits.Default, out _, out string fatal);

            Assert.True(ok, fatal);
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

        private const string WptCoordsViewattrDoctype =
            "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1 Basic//EN\" " +
            "\"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11-basic.dtd\" [";

        private const string WptCoordsViewportEntities =
            "  <!ENTITY Viewport1 \"<rect x='.5' y='.5' width='49' height='29' fill='none' stroke='blue'/>\">" +
            "  <!ENTITY Viewport2 \"<rect x='.5' y='.5' width='29' height='59' fill='none' stroke='blue'/>\">" +
            "]>";

        private static void AssertRejected(string source, string expectedFragment)
        {
            Assert.False(
                SvgMarkupParser.TryParse(source, FullLimits(), out _, out var fatal),
                "expected the DOCTYPE to be rejected");
            Assert.NotNull(fatal);
            Assert.Contains("DOCTYPE", fatal);
            Assert.Contains(expectedFragment, fatal);
        }

        [Fact]
        public void WptImport_CoordsViewattr02_ExternalIdPlusInternalSubset_EntitiesExpandAsMarkup()
        {
            string source = WptCoordsViewattrDoctype + WptCoordsViewportEntities +
                            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 480 360'>" +
                            "<g transform='translate(10,120)'>&Viewport1;</g>" +
                            "<g transform='translate(20,190)'>&Viewport2;</g>" +
                            "</svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.True(doc.Report.SawDoctype);
            Assert.Equal(2, doc.Root.Children.Count);
            foreach (var group in doc.Root.Children)
            {
                var rect = Assert.Single(group.Children);
                Assert.Equal("rect", rect.Name);
                Assert.Equal("blue", rect.GetAttribute("stroke"));
            }
            Assert.Equal("49", doc.Root.Children[0].Children[0].GetAttribute("width"));
            Assert.Equal("29", doc.Root.Children[0].Children[0].GetAttribute("height"));
            Assert.Equal("29", doc.Root.Children[1].Children[0].GetAttribute("width"));
            Assert.Equal("59", doc.Root.Children[1].Children[0].GetAttribute("height"));
        }

        [Fact]
        public void WptImport_RenderElems03_SingleEntityExpansionInContentPosition()
        {
            const string entities =
                "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1 Tiny//EN\" " +
                "\"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11-tiny.dtd\" [" +
                "  <!ENTITY shape \"<path d='M60,0 l60,0 l60,60 z'/>\">" +
                "]>";
            string source = entities +
                            "<svg xmlns='http://www.w3.org/2000/svg'>" +
                            "<g fill='yellow'>&shape;</g>" +
                            "<g stroke='black'>&shape;</g>" +
                            "</svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal(2, doc.Root.Children.Count);
            Assert.Equal("M60,0 l60,0 l60,60 z", doc.Root.Children[0].Children[0].GetAttribute("d"));
            Assert.Equal("M60,0 l60,0 l60,60 z", doc.Root.Children[1].Children[0].GetAttribute("d"));
        }

        [Fact]
        public void WptImport_TextTref02_AttributeListOnlyInternalSubset_IsAccepted()
        {
            const string doctype = "<!DOCTYPE svg [\n<!ATTLIST bar id ID #REQUIRED>\n]>\n";
            string source = doctype +
                            "<svg xmlns='http://www.w3.org/2000/svg'>" +
                            "<g><bar id='world'>World</bar></g>" +
                            "</svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal("bar", doc.Root.Children[0].Children[0].Name);
            Assert.Same(doc.Root.Children[0].Children[0], doc.ElementsById["world"]);
        }

        [Fact]
        public void Doctype_ExternalIdentifier_IsNeverResolved()
        {
            string source = WptCoordsViewattrDoctype + WptCoordsViewportEntities +
                            "<svg xmlns='http://www.w3.org/2000/svg'>&Viewport1;</svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Contains(
                "DOCTYPE external identifier ignored; external subsets are never resolved",
                doc.Report.Warnings);
            Assert.DoesNotContain(
                doc.Report.Warnings,
                warning => warning.IndexOf("svg11-basic.dtd", System.StringComparison.Ordinal) >= 0);
        }

        [Fact]
        public void Doctype_EntityUsedInsideAttributeValue_IsDecoded()
        {
            const string source =
                "<!DOCTYPE svg [<!ENTITY red \"#ff0000\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'><rect fill='&red;' width='4'/></svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal("#ff0000", doc.Root.Children[0].GetAttribute("fill"));
        }

        [Fact]
        public void Doctype_EntityUsedInsideTextContent_IsDecoded()
        {
            const string source =
                "<!DOCTYPE svg [<!ENTITY a \"A\"><!ENTITY b \"&a;B\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'><text>&b;</text></svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal("AB", doc.Root.Children[0].TextContent);
        }

        [Fact]
        public void Doctype_EntityValueCannotEscapeAttributeContext()
        {
            const string source =
                "<!DOCTYPE svg [<!ENTITY q '\"><script>injected</script>'>]>" +
                "<svg xmlns='http://www.w3.org/2000/svg' data-x='&q;'/>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal("\"><script>injected</script>", doc.Root.GetAttribute("data-x"));
            Assert.Empty(doc.Root.Children);
        }

        [Fact]
        public void Doctype_MarkupEntityInsideTextContainer_StaysCharacterData()
        {
            const string source =
                "<!DOCTYPE svg [<!ENTITY e \"<rect/>\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'><text>&e;</text></svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            var text = doc.Root.Children[0];
            Assert.Empty(text.Children);
            Assert.Equal("<rect/>", text.TextContent);
        }

        [Fact]
        public void Doctype_EntityInsideIgnoredSubtree_DoesNotTerminateIt()
        {
            const string source =
                "<!DOCTYPE svg [<!ENTITY e \"</desc><script>alert(1)</script>\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'>" +
                "<desc>&e;</desc><rect width='1' height='1'/></svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.DoesNotContain(doc.Root.Children, child => child.Name == "script");
            Assert.Equal("rect", doc.Root.Children[1].Name);
        }

        [Fact]
        public void Doctype_ExternalSubset_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg SYSTEM \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "external subset");
        }

        [Fact]
        public void Doctype_PublicExternalSubset_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"x.dtd\">" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "external subset");
        }

        [Fact]
        public void Doctype_ExternalGeneralEntity_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'>&xxe;</svg>",
                "external entity declaration");
        }

        [Fact]
        public void Doctype_ExternalPublicEntity_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY logo PUBLIC \"-//X//DTD//EN\" \"logo.dtd\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "external entity declaration");
        }

        [Fact]
        public void Doctype_ParameterEntityDeclaration_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY % pe \"<!ENTITY x 'y'>\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "parameter entity declaration");
        }

        [Fact]
        public void Doctype_ParameterEntityReferenceInAttributeList_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ATTLIST bar a CDATA %pe;>]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "parameter entity reference");
        }

        [Fact]
        public void Doctype_SelfRecursiveEntity_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY a \"&a;\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'>&a;</svg>",
                "recursive entity expansion");
        }

        [Fact]
        public void Doctype_MutuallyRecursiveEntities_AreRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY a \"&b;\"><!ENTITY b \"&a;\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'>&a;</svg>",
                "undeclared entity reference");
        }

        [Fact]
        public void Doctype_ForwardReferenceToUndeclaredEntity_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY a \"&b;\"><!ENTITY b \"y\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'>&a;</svg>",
                "undeclared entity reference");
        }

        [Fact]
        public void Doctype_BillionLaughs_IsRejected()
        {
            var sb = new System.Text.StringBuilder("<!DOCTYPE svg [\n<!ENTITY e0 \"lol\">\n");
            for (int i = 1; i <= 12; i++)
            {
                sb.Append("<!ENTITY e").Append(i).Append(" \"");
                for (int r = 0; r < 10; r++) sb.Append("&e").Append(i - 1).Append(";");
                sb.Append("\">\n");
            }
            sb.Append("]>\n<svg xmlns='http://www.w3.org/2000/svg'>&e12;</svg>");

            AssertRejected(sb.ToString(), "DOCTYPE");
        }

        [Fact]
        public void Doctype_EntityExpansionDepthBudget_IsEnforced()
        {
            var sb = new System.Text.StringBuilder("<!DOCTYPE svg [\n<!ENTITY e0 \"x\">\n");
            for (int i = 1; i <= SvgMarkupParser.MaxEntityExpansionDepth + 1; i++)
            {
                sb.Append("<!ENTITY e").Append(i).Append(" \"&e").Append(i - 1).Append(";\">\n");
            }
            sb.Append("]>\n<svg xmlns='http://www.w3.org/2000/svg'/>");

            AssertRejected(sb.ToString(), "entity expansion depth");
        }

        [Fact]
        public void Doctype_EntityExpansionAtDepthBudget_IsAccepted()
        {
            var sb = new System.Text.StringBuilder("<!DOCTYPE svg [\n<!ENTITY e0 \"<rect/>\">\n");
            for (int i = 1; i <= SvgMarkupParser.MaxEntityExpansionDepth; i++)
            {
                sb.Append("<!ENTITY e").Append(i).Append(" \"&e").Append(i - 1).Append(";\">\n");
            }
            sb.Append("]>\n<svg xmlns='http://www.w3.org/2000/svg'>&e")
              .Append(SvgMarkupParser.MaxEntityExpansionDepth).Append(";</svg>");

            var doc = Parse(sb.ToString(), out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal("rect", Assert.Single(doc.Root.Children).Name);
        }

        [Fact]
        public void Doctype_ConditionalSection_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<![INCLUDE[<!ENTITY x \"y\">]]>]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "DOCTYPE");
        }

        [Fact]
        public void Doctype_ElementDeclaration_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ELEMENT svg ANY>]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "DOCTYPE");
        }

        [Fact]
        public void Doctype_NotationDeclaration_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!NOTATION gif SYSTEM \"image/gif\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "DOCTYPE");
        }

        [Fact]
        public void Doctype_UnterminatedInternalSubset_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY a \"x\">" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "internal subset");
        }

        [Fact]
        public void Doctype_UnterminatedEntityValue_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY a \"unterminated>]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "entity value");
        }

        [Fact]
        public void Doctype_OversizedSubset_IsRejected()
        {
            var sb = new System.Text.StringBuilder("<!DOCTYPE svg [<!-- ");
            while (sb.Length < SvgMarkupParser.MaxDoctypeChars) sb.Append("padding ");
            sb.Append("-->]><svg xmlns='http://www.w3.org/2000/svg'/>");

            AssertRejected(sb.ToString(), "DOCTYPE");
        }

        [Fact]
        public void Doctype_TooManyEntities_AreRejected()
        {
            var sb = new System.Text.StringBuilder("<!DOCTYPE svg [");
            for (int i = 0; i <= SvgMarkupParser.MaxDoctypeEntities; i++)
            {
                sb.Append("<!ENTITY e").Append(i).Append(" \"v\">");
            }
            sb.Append("]><svg xmlns='http://www.w3.org/2000/svg'/>");

            AssertRejected(sb.ToString(), "entity count over budget");
        }

        [Fact]
        public void Doctype_OversizedEntityValue_IsRejected()
        {
            var value = new string('v', SvgMarkupParser.MaxDoctypeEntityValueChars + 1);
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY a \"" + value + "\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "entity value");
        }

        [Fact]
        public void Doctype_OversizedEntityName_IsRejected()
        {
            var name = new string('e', SvgMarkupParser.MaxDoctypeEntityNameChars + 1);
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY " + name + " \"v\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "entity name");
        }

        [Fact]
        public void Doctype_EntityInjectionSpliceBudget_FailsClosed()
        {
            string source =
                "<!DOCTYPE svg [<!ENTITY a \"" + new string('x', 64) + "\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'>" +
                new string('&', 1) +
                string.Concat(System.Linq.Enumerable.Repeat("&a;", SvgMarkupParser.MaxEntitySplices + 8)) +
                "</svg>";

            AssertRejected(source, "splice budget");
        }

        [Fact]
        public void Doctype_EntityInjectionCharacterBudget_FailsClosed()
        {
            string source =
                "<!DOCTYPE svg [<!ENTITY big \"" +
                new string('x', SvgMarkupParser.MaxDoctypeEntityValueChars) +
                "\">]><svg xmlns='http://www.w3.org/2000/svg'>" +
                string.Concat(System.Linq.Enumerable.Repeat(
                    "&big;", SvgMarkupParser.MaxEntityInjectionChars / SvgMarkupParser.MaxDoctypeEntityValueChars + 4)) +
                "</svg>";

            AssertRejected(source, "character budget");
        }

        [Fact]
        public void Doctype_SecondDeclarationInProlog_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ENTITY a \"x\">]>" +
                "<!DOCTYPE svg [<!ENTITY b \"y\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "DOCTYPE");
        }

        [Fact]
        public void Doctype_LowercaseDoctypeKeyword_UsesTheSameBoundedPath()
        {
            const string source =
                "<!doctype svg [<!ENTITY a \"<rect/>\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'>&a;</svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal("rect", Assert.Single(doc.Root.Children).Name);
        }

        [Fact]
        public void Doctype_NonXmlDeclarationKeyword_IsRejected()
        {
            AssertRejected(
                "<!doctype svg [<!entity a \"<rect/>\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'/>",
                "declaration");
        }

        [Fact]
        public void UnknownEntityReferenceInContentPosition_RemainsNonFatal()
        {
            var doc = Parse(
                "<svg xmlns='http://www.w3.org/2000/svg'><g>&nope;</g><rect/></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal(2, doc.Root.Children.Count);
        }

        [Fact]
        public void Doctype_InternalSubsetComment_IsSkipped()
        {
            const string source =
                "<!DOCTYPE svg [<!-- a comment --> <!ENTITY a \"<rect/>\"> ]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'>&a;</svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal("rect", Assert.Single(doc.Root.Children).Name);
        }

        /// <summary>
        /// A quoted literal in an ATTLIST declaration is a declared attribute
        /// default value. A browser applies it to every element that omits the
        /// attribute, so honouring the parse while discarding the literal would
        /// paint a different frame than the browser and still report success.
        /// The engine cannot apply defaults, so the declaration is refused.
        /// </summary>
        [Fact]
        public void Doctype_AttributeListDefaultValue_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ATTLIST rect fill CDATA \"lime\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'><rect/></svg>",
                "attribute list default value");
        }

        [Fact]
        public void Doctype_AttributeListDefaultValueSingleQuoted_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ATTLIST rect fill CDATA 'lime'>]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'><rect/></svg>",
                "attribute list default value");
        }

        [Fact]
        public void Doctype_AttributeListDefaultValue_IsRejectedEvenWhenAttributeIsPresent()
        {
            // The default is unobservable only if every element overrides it, and
            // the engine cannot know that while scanning the declaration, so the
            // whole declaration is refused rather than conditionally admitted.
            AssertRejected(
                "<!DOCTYPE svg [<!ATTLIST rect fill CDATA \"lime\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'><rect fill='blue'/></svg>",
                "attribute list default value");
        }

        [Fact]
        public void Doctype_AttributeListFixedDefaultValue_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ATTLIST rect fill CDATA #FIXED \"lime\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'><rect/></svg>",
                "attribute list default value");
        }

        [Fact]
        public void Doctype_AttributeListDefaultValueInMultiAttributeDeclaration_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ATTLIST bar id ID #REQUIRED label CDATA #IMPLIED " +
                "fill CDATA \"lime\">]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'><bar id='world'/></svg>",
                "attribute list default value");
        }

        [Fact]
        public void Doctype_AttributeListUnterminatedDefaultValue_IsRejected()
        {
            AssertRejected(
                "<!DOCTYPE svg [<!ATTLIST rect fill CDATA \"lime\"" +
                "<svg xmlns='http://www.w3.org/2000/svg'><rect/></svg>",
                "attribute list default value");
        }

        [Fact]
        public void Doctype_AttributeListRequiredAndImpliedForms_AreStillAccepted()
        {
            const string source =
                "<!DOCTYPE svg [<!ATTLIST bar id ID #REQUIRED label CDATA #IMPLIED " +
                "x CDATA #IMPLIED y CDATA #IMPLIED>]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'><bar id='world'>World</bar></svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            var bar = Assert.Single(doc.Root.Children);
            Assert.Equal("bar", bar.Name);
            Assert.Same(bar, doc.ElementsById["world"]);
            Assert.Empty(bar.Children);
            Assert.Null(bar.GetAttribute("fill"));
            Assert.Contains(
                "DOCTYPE internal subset declarations honored (no external resolution)",
                doc.Report.Warnings);
        }

        [Fact]
        public void Doctype_AttributeListEntityDeclarationAndListShareTheSubset()
        {
            const string source =
                "<!DOCTYPE svg [<!ENTITY a \"<rect/>\"><!ATTLIST bar id ID #REQUIRED>]>" +
                "<svg xmlns='http://www.w3.org/2000/svg'><bar id='world'/>&a;</svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal(2, doc.Root.Children.Count);
            Assert.Equal("rect", doc.Root.Children[1].Name);
            Assert.Same(doc.Root.Children[0], doc.ElementsById["world"]);
        }
    }
}
