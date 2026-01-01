using System;
using System.Linq;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssTokenizerTests
    {
        [Fact]
        public void TestIdent()
        {
            var tokenizer = new CssTokenizer("color");
            var token = tokenizer.Consume();
            Assert.Equal(CssTokenType.Ident, token.Type);
            Assert.Equal("color", token.Value);
        }

        [Fact]
        public void TestNumber()
        {
            var tokenizer = new CssTokenizer("123.45");
            var token = tokenizer.Consume();
            Assert.Equal(CssTokenType.Number, token.Type);
            Assert.Equal(123.45, token.NumericValue);
        }

        [Fact]
        public void TestDimension()
        {
            var tokenizer = new CssTokenizer("10px");
            var token = tokenizer.Consume();
            Assert.Equal(CssTokenType.Dimension, token.Type);
            Assert.Equal(10, token.NumericValue);
            Assert.Equal("px", token.Unit);
        }

        [Fact]
        public void TestString()
        {
            var tokenizer = new CssTokenizer("\"hello world\"");
            var token = tokenizer.Consume();
            Assert.Equal(CssTokenType.String, token.Type);
            Assert.Equal("hello world", token.Value);
        }

        [Fact]
        public void TestComment()
        {
            var tokenizer = new CssTokenizer("/* comment */ ident");
            var token = tokenizer.Consume(); // Should skip comment and get whitespace?
            // Actually our tokenizer returns whitespace if strict, but let's check.
            // Consume() first checks comments, then consumes token.
            // If space after comment, returns whitespace.
            Assert.Equal(CssTokenType.Whitespace, token.Type); // space after comment
            token = tokenizer.Consume();
            Assert.Equal(CssTokenType.Ident, token.Type);
            Assert.Equal("ident", token.Value);
        }

        [Fact]
        public void TestComplexSelector()
        {
            var input = "div#main.class > span";
            var tokenizer = new CssTokenizer(input);
            
            // div
            var t1 = tokenizer.Consume();
            Assert.Equal(CssTokenType.Ident, t1.Type);
            Assert.Equal("div", t1.Value);
            
            // #main
            var t2 = tokenizer.Consume();
            Assert.Equal(CssTokenType.Hash, t2.Type);
            Assert.Equal("main", t2.Value);
            Assert.Equal(HashType.Id, t2.HashType);
            
            // .
            var t3 = tokenizer.Consume();
            Assert.Equal(CssTokenType.Delim, t3.Type);
            Assert.Equal('.', t3.Delimiter);
            
            // class
            var t4 = tokenizer.Consume();
            Assert.Equal(CssTokenType.Ident, t4.Type);
            Assert.Equal("class", t4.Value);
            
            // whitespace
            var t5 = tokenizer.Consume();
            Assert.Equal(CssTokenType.Whitespace, t5.Type);
            
            // >
            var t6 = tokenizer.Consume();
            Assert.Equal(CssTokenType.Delim, t6.Type);
            Assert.Equal('>', t6.Delimiter);
            
            // whitespace
            var t7 = tokenizer.Consume();
            Assert.Equal(CssTokenType.Whitespace, t7.Type);
            
            // span
            var t8 = tokenizer.Consume();
            Assert.Equal(CssTokenType.Ident, t8.Type);
            Assert.Equal("span", t8.Value);
        }
        
        [Fact]
        public void TestFunction()
        {
            var input = "rgb(255, 0, 0)";
            var tokenizer = new CssTokenizer(input);
            
            var token = tokenizer.Consume();
            Assert.Equal(CssTokenType.Function, token.Type);
            Assert.Equal("rgb", token.Value);
        }
        
        [Fact]
        public void TestAtRule()
        {
            var tokenizer = new CssTokenizer("@media screen");
            var t1 = tokenizer.Consume();
            Assert.Equal(CssTokenType.AtKeyword, t1.Type);
            Assert.Equal("media", t1.Value);
        }
    }
}
