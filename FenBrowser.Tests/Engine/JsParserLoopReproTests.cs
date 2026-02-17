using System;
using System.Linq;
using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JsParserLoopReproTests
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
        public void Parse_ForLoop_Empty_WithConstInBlock()
        {
            var input = "for(;;){const e=1;}";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ForLoop_Empty_WithFunctionCallInConst()
        {
            var input = "for(;;){const e=c(-1);}";
            var parser = CreateParser(input);
            var program = parser.ParseProgram();
            AssertNoErrors(parser);
        }

        [Fact]
        public void Parse_ForLoop_LogSnippet_Isolated()
        {
             // Using the snippet that caused the error in logs:
             // rba();let d=0;for(;;){const e=c(-1),f=c(0),g=c(64),h=c(64); ...
             var input = "rba();let d=0;for(;;){const e=c(-1),f=c(0),g=c(64),h=c(64);}";
             var parser = CreateParser(input);
             var program = parser.ParseProgram();
             AssertNoErrors(parser);
        }
    }
}
