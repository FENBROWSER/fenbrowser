// Html5lib-style Tokenizer Tests for FenBrowser
// Tests tokenizer against known HTML5 edge cases from html5lib-tests
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using FenBrowser.Core.Parsing;

namespace FenBrowser.Tests.Html5lib
{
    /// <summary>
    /// Html5lib-style tokenizer tests validating WHATWG compliance.
    /// Tests cover: entities, RCDATA, RAWTEXT, script data, CDATA, DOCTYPE.
    /// </summary>
    public class Html5libTokenizerTests
    {
        private static string? GetAttributeValue(StartTagToken tag, string name)
        {
            return tag.Attributes.FirstOrDefault(
                attr => string.Equals(attr.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        private List<HtmlToken> Tokenize(string html)
        {
            var tokenizer = new HtmlTokenizer(html);
            return tokenizer.Tokenize().ToList();
        }
        
        // --- Character Reference Tests ---
        
        [Theory]
        [InlineData("&amp;", "&")]
        [InlineData("&lt;", "<")]
        [InlineData("&gt;", ">")]
        [InlineData("&quot;", "\"")]
        [InlineData("&apos;", "'")]
        [InlineData("&nbsp;", "\u00A0")]
        public void Tokenize_NamedCharacterReferences(string input, string expected)
        {
            var tokens = Tokenize(input);
            var charToken = tokens.OfType<CharacterToken>().FirstOrDefault();
            Assert.NotNull(charToken);
            Assert.Equal(expected, charToken.Data.ToString());
        }
        
        [Theory]
        [InlineData("&#65;", "A")]
        [InlineData("&#97;", "a")]
        [InlineData("&#160;", "\u00A0")]
        [InlineData("&#x41;", "A")]
        [InlineData("&#x61;", "a")]
        [InlineData("&#xA0;", "\u00A0")]
        public void Tokenize_NumericCharacterReferences(string input, string expected)
        {
            var tokens = Tokenize(input);
            var charToken = tokens.OfType<CharacterToken>().FirstOrDefault();
            Assert.NotNull(charToken);
            Assert.Equal(expected, charToken.Data.ToString());
        }
        
        // --- Basic Tag Tests ---
        
        [Fact]
        public void Tokenize_SimpleStartTag()
        {
            var tokens = Tokenize("<div>");
            var tag = tokens.OfType<StartTagToken>().FirstOrDefault();
            Assert.NotNull(tag);
            Assert.Equal("div", tag.TagName, ignoreCase: true);
        }
        
        [Fact]
        public void Tokenize_StartTagWithAttributes()
        {
            var tokens = Tokenize("<div class=\"foo\" id='bar'>");
            var tag = tokens.OfType<StartTagToken>().FirstOrDefault();
            Assert.NotNull(tag);
            Assert.Equal("div", tag.TagName, ignoreCase: true);
            Assert.Equal("foo", GetAttributeValue(tag, "class"));
            Assert.Equal("bar", GetAttributeValue(tag, "id"));
        }
        
        [Fact]
        public void Tokenize_SelfClosingTag()
        {
            var tokens = Tokenize("<br/>");
            var tag = tokens.OfType<StartTagToken>().FirstOrDefault();
            Assert.NotNull(tag);
            Assert.Equal("br", tag.TagName, ignoreCase: true);
            Assert.True(tag.SelfClosing);
        }
        
        [Fact]
        public void Tokenize_EndTag()
        {
            var tokens = Tokenize("</div>");
            var tag = tokens.OfType<EndTagToken>().FirstOrDefault();
            Assert.NotNull(tag);
            Assert.Equal("div", tag.TagName, ignoreCase: true);
        }
        
        // --- DOCTYPE Tests ---
        
        [Fact]
        public void Tokenize_SimpleDoctype()
        {
            var tokens = Tokenize("<!DOCTYPE html>");
            var doctype = tokens.OfType<DoctypeToken>().FirstOrDefault();
            Assert.NotNull(doctype);
            Assert.Equal("html", doctype.Name);
        }
        
        [Fact]
        public void Tokenize_LegacyDoctype()
        {
            var tokens = Tokenize("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\">");
            var doctype = tokens.OfType<DoctypeToken>().FirstOrDefault();
            Assert.NotNull(doctype);
        }
        
        // --- Comment Tests ---
        
        [Fact]
        public void Tokenize_Comment()
        {
            var tokens = Tokenize("<!-- comment -->");
            var comment = tokens.OfType<CommentToken>().FirstOrDefault();
            Assert.NotNull(comment);
            Assert.Contains("comment", comment.Data.ToString());
        }
        
        [Fact]
        public void Tokenize_EmptyComment()
        {
            var tokens = Tokenize("<!---->");
            var comment = tokens.OfType<CommentToken>().FirstOrDefault();
            Assert.NotNull(comment);
            Assert.Equal("", comment.Data.ToString());
        }
        
        // --- Script/Style Raw Text Tests ---
        
        [Fact]
        public void Tokenize_ScriptContent()
        {
            var tokens = Tokenize("<script>var x = 1 < 2;</script>");
            var startTag = tokens.OfType<StartTagToken>().FirstOrDefault(t => string.Equals(t.TagName, "script", StringComparison.OrdinalIgnoreCase));
            var endTag = tokens.OfType<EndTagToken>().FirstOrDefault(t => string.Equals(t.TagName, "script", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(startTag);
            Assert.NotNull(endTag);
        }
        
        [Fact]
        public void Tokenize_StyleContent()
        {
            var tokens = Tokenize("<style>.a > .b { color: red; }</style>");
            var startTag = tokens.OfType<StartTagToken>().FirstOrDefault(t => string.Equals(t.TagName, "style", StringComparison.OrdinalIgnoreCase));
            var endTag = tokens.OfType<EndTagToken>().FirstOrDefault(t => string.Equals(t.TagName, "style", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(startTag);
            Assert.NotNull(endTag);
        }
        
        // --- Entity in Attribute Tests ---
        
        [Fact]
        public void Tokenize_EntityInAttribute()
        {
            var tokens = Tokenize("<a href=\"?a=1&amp;b=2\">");
            var tag = tokens.OfType<StartTagToken>().FirstOrDefault();
            Assert.NotNull(tag);
            Assert.Equal("?a=1&b=2", GetAttributeValue(tag, "href"));
        }
        
        [Fact]
        public void Tokenize_UnquotedAttributeValue()
        {
            var tokens = Tokenize("<div class=foo>");
            var tag = tokens.OfType<StartTagToken>().FirstOrDefault();
            Assert.NotNull(tag);
            Assert.Equal("foo", GetAttributeValue(tag, "class"));
        }
        
        // --- Edge Case Tests ---
        
        [Fact]
        public void Tokenize_MultipleTags()
        {
            var tokens = Tokenize("<html><head></head><body></body></html>");
            var startTags = tokens.OfType<StartTagToken>().ToList();
            var endTags = tokens.OfType<EndTagToken>().ToList();
            Assert.Equal(3, startTags.Count);
            Assert.Equal(3, endTags.Count);
        }
        
        [Fact]
        public void Tokenize_MixedContent()
        {
            var tokens = Tokenize("<p>Hello &amp; world</p>");
            var charTokens = tokens.OfType<CharacterToken>().ToList();
            var combined = string.Join("", charTokens.Select(c => c.Data.ToString()));
            Assert.Contains("Hello", combined);
            Assert.Contains("&", combined);
            Assert.Contains("world", combined);
        }
    }
}
