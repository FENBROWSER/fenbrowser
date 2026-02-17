using System;
using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class TemplateLiteralTests
    {
        private readonly FenRuntime _runtime;

        public TemplateLiteralTests()
        {
            _runtime = new FenRuntime();
        }

        [Fact]
        public void SimpleTemplate_NoSubstitutions()
        {
            var result = _runtime.ExecuteSimple("`hello world`");
            Assert.Equal("hello world", result.ToString());
        }

        [Fact]
        public void TemplateWithOneExpression()
        {
            var result = _runtime.ExecuteSimple("`hello ${1 + 2}`");
            Assert.Equal("hello 3", result.ToString());
        }

        [Fact]
        public void TemplateWithMultipleExpressions()
        {
            var result = _runtime.ExecuteSimple("`a${1}b${2}c`");
            Assert.Equal("a1b2c", result.ToString());
        }

        [Fact]
        public void TemplateWithComplexExpression_ObjectLiteral()
        {
            // This was the bug: ${ {a:1} } failed because Lexer didn't handle nested braces in expression
            var result = _runtime.ExecuteSimple("`obj: ${{a:1}}`");
            Assert.Equal("obj: [object Object]", result.ToString());
        }

        [Fact]
        public void TemplateWithNestedTemplate()
        {
            var result = _runtime.ExecuteSimple("`nested: ${`inner ${10}`}`");
            Assert.Equal("nested: inner 10", result.ToString());
        }

        [Fact]
        public void TemplateWithBracesInsideExpression()
        {
            var result = _runtime.ExecuteSimple("`sum: ${ ({a:1}).a + 2 }`");
            Assert.Equal("sum: 3", result.ToString());
        }

        [Fact]
        public void NestedObjectAndArray()
        {
             var result = _runtime.ExecuteSimple("`val: ${ [{a:1}][0].a }`");
             Assert.Equal("val: 1", result.ToString());
        }

        [Fact]
        public void TopLevelReturn_Allowed()
        {
            // ExecuteSimple with allowReturn=true
            var result = _runtime.ExecuteSimple("return 42;", allowReturn: true);
            // Result of return statement is the returned value's completion value?
            // If ExecuteSimple wraps it, we expect "42".
            Assert.Equal("42", result.ToString());
        }
        
        [Fact]
        public void EscapedBackticks()
        {
            var result = _runtime.ExecuteSimple(@"`\`\``");
            Assert.Equal("``", result.ToString());
        }
    }
}
