using System;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
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
        public void Parse_LegacyDoctype_SetsLimitedQuirksCheck()
        {
            // Legacy HTML4 transitional PUBLIC doctypes map to limited-quirks mode.
            var html = "<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01 Transitional//EN\"><html></html>";
            var parser = new HtmlParser(html);
            var doc = parser.Parse();

            Assert.Equal(QuirksMode.LimitedQuirks, doc.Mode);
        }
    }
}
