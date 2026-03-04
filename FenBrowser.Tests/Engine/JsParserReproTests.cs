using System;
using System.Linq;
using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JsParserReproTests
    {
        private Parser CreateParser(string input)
        {
            var lexer = new Lexer(input);
            return new Parser(lexer);
        }

        private void AssertNoErrors(Parser parser)
        {
            if (parser.Errors.Any())
            {
                throw new Exception($"Parser errors:\n{string.Join("\n", parser.Errors)}");
            }
        }

        [Fact]
        public void Lexer_DecodesEscapedIdentifier_Exactly()
        {
            var lexer = new Lexer("var privat\\u0065 = 1;");
            // var
            var t1 = lexer.NextToken();
            // identifier
            var t2 = lexer.NextToken();
            Assert.Equal(TokenType.Identifier, t2.Type);
            Assert.Equal("private", t2.Literal);
        }

        [Fact]
        public void Parse_StrictMode_Rejects_EscapedFutureReservedWord()
        {
            var input = "\"use strict\"; var privat\\u0065 = 1;";
            var parser = CreateParser(input);
            parser.ParseProgram();

            Assert.Contains(parser.Errors, e => e.Contains("strict mode reserved word 'private'", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_AsyncArrowFunction_InCallExpression()
        {
            // async (x) => 1
            var input = "call(async (x) => 1)";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
            
            // Validate structure
            var stmt = program.Statements.First() as ExpressionStatement;
            var call = stmt.Expression as CallExpression;
            var arrow = call.Arguments.First() as ArrowFunctionExpression;
            Assert.NotNull(arrow);
            Assert.True(arrow.IsAsync);
            Assert.Single(arrow.Parameters);
            Assert.Equal("x", arrow.Parameters[0].Value);
        }

        [Fact]
        public void Parse_AsyncArrowFunction_TwoParams()
        {
            var input = "call(async (x, y) => x + y)";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_AsyncArrowFunction_NoParams()
        {
            var input = "call(async () => 1)";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ComplexRegex_InConditional()
        {
            // The failing regex pattern from log
            // Note: C# verbatim string, \\u is passed as \u to Lexer if not double escaped? 
            // C# @"..." preserves backslashes. 
            // Code has \uD800. In C# string literal this is unicode char if not verbatim?
            // @"..." ignores escape sequences. So \uD800 is literally \ u D 8 0 0.
            // But Lexer expects valid JS string.
            // If Input has literally \ u D 8 0 0, JS string/regex parser handles it.
            var input = @"if(b&&(eaa?!a.isWellFormed():/(?:[^\uD800-\uDBFF]|^)[\uDC00-\uDFFF]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])/.test(a)))throw Error('q');";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_Throw_After_Conditional()
        {
           // Context around failure
           var input = @"if(cond) throw Error('q'); a=(faa||1)";
           var parser = CreateParser(input);
           var program = parser.ParseProgram();
           AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidHexEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\xG1`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidUnicodeCodePointEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\u{110000}`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidLegacyOctalEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\08`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }
        [Fact]
        public void Parse_StrictMode_WithStatement_ShouldFail()
        {
            var parser = CreateParser("\"use strict\"; with ({ x: 1 }) { x; }");
            parser.ParseProgram();
            Assert.Contains(parser.Errors, e => e.Contains("with statement", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Parse_TemplateLiteral_InvalidUnicodeEscape_ShouldFail()
        {
            var parser = CreateParser("`bad: \\u00G0`");
            parser.ParseProgram();
            Assert.NotEmpty(parser.Errors);
        }

        [Fact]
        public void Parse_WithStatement_SingleStatementClassDeclaration_ShouldFail()
        {
            var parser = CreateParser("with ({}) class C {}");
            parser.ParseProgram();
            Assert.Contains(parser.Errors, e => e.Contains("Declaration not allowed in with statement", StringComparison.OrdinalIgnoreCase));
        }
        [Fact]
        public void Parse_CompoundAssignment_PlusAssign()
        {
            var input = "var x = 1; x += 2;";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }
    }
}



