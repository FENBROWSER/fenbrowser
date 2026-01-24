using System;
using FenBrowser.Core;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class HtmlLiteParserTests
    {
        [Fact]
        public void Parse_StandardHtml5Doctype_SetsNoQuirksCheck()
        {
            var html = "<!DOCTYPE html><html><body></body></html>";
            var parser = new HtmlParser(html);
            var doc = parser.Parse();

            Assert.Equal(QuirksMode.NoQuirks, doc.Mode);
        }

        [Fact]
        public void Parse_CaseInsensitiveDoctype_SetsNoQuirksCheck()
        {
            var html = "<!doctype HTML><html><body></body></html>";
            var parser = new HtmlParser(html);
            var doc = parser.Parse();

            Assert.Equal(QuirksMode.NoQuirks, doc.Mode);
        }

        [Fact]
        public void Parse_MissingDoctype_SetsQuirksCheck()
        {
            var html = "<html><body></body></html>";
            var parser = new HtmlParser(html);
            var doc = parser.Parse();

            Assert.Equal(QuirksMode.Quirks, doc.Mode);
        }

        [Fact]
        public void Parse_LegacyDoctype_SetsNoQuirksCheck_ForNow()
        {
            // Current implementation treats any DOCTYPE as NoQuirks to prefer Standards
            var html = "<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01 Transitional//EN\"><html></html>";
            var parser = new HtmlParser(html);
            var doc = parser.Parse();

            Assert.Equal(QuirksMode.NoQuirks, doc.Mode);
        }
    }
}
