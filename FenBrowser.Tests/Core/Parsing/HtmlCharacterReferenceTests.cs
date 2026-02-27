using System;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    public class HtmlCharacterReferenceTests
    {
        [Fact]
        public void AttributeValues_DecodeNamedAndNumericCharacterReferences()
        {
            const string html = "<html><body><div id=\"t\" data-a=\"A&#66;C&amp;&lt;&gt;&quot;&apos;&#x41;&#65;\"></div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("ABC&<>\"'AA", target.GetAttribute("data-a"));
        }

        [Fact]
        public void TextNodes_DecodeNamedAndNumericCharacterReferences()
        {
            const string html = "<html><body><div id='t'>&lt;A&amp;&#66;&#x43;&gt;</div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("<A&BC>", target.TextContent);
        }

        [Fact]
        public void UnknownNamedReference_IsPreservedAsLiteralText()
        {
            const string html = "<html><body><div id='t' data-a='x&zznotanentity;y'>x&zznotanentity;y</div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("x&zznotanentity;y", target.GetAttribute("data-a"));
            Assert.Equal("x&zznotanentity;y", target.TextContent);
        }

        [Fact]
        public void NamedReference_WithoutSemicolon_DecodesWhenBoundaryIsSafe()
        {
            const string html = "<html><body><div id='t'>&copy 2026 &hellip; &reg</div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("\u00A9 2026 \u2026 \u00AE", target.TextContent);
        }

        [Fact]
        public void AttributeReference_WithoutSemicolon_BeforeEquals_RemainsLiteral()
        {
            const string html = "<html><body><div id='t' data-a='x&copy=1'></div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("x&copy=1", target.GetAttribute("data-a"));
        }

        [Fact]
        public void NumericReference_Windows1252Remap_Applies()
        {
            const string html = "<html><body><div id='t'>&#128;</div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("\u20AC", target.TextContent);
        }

        [Fact]
        public void ExtendedNamedReferences_DecodeViaFallback()
        {
            const string html = "<html><body><div id='t'>&larr;&sum;</div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("\u2190\u2211", target.TextContent);
        }

        [Fact]
        public void ExtendedNamedReferences_DecodeInAttributes()
        {
            const string html = "<html><body><div id='t' data-a='x&larr;z&sum;'></div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("x\u2190z\u2211", target.GetAttribute("data-a"));
        }

        [Fact]
        public void LegacyPrefixNamedReference_CanPartiallyDecode()
        {
            const string html = "<html><body><div id='t'>&notanentity;</div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("\u00ACanentity;", target.TextContent);
        }

        [Fact]
        public void InvalidNumericReference_WithoutDigits_IsPreserved()
        {
            const string html = "<html><body><div id='t'>&#;</div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("&#;", target.TextContent);
        }

        [Fact]
        public void InvalidHexReference_WithoutDigits_IsPreserved()
        {
            const string html = "<html><body><div id='t' data-a='&#x;'>&#x;</div></body></html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var target = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("&#x;", target.GetAttribute("data-a"));
            Assert.Equal("&#x;", target.TextContent);
        }
    }
}
