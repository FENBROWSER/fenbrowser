using System;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// JS-4: Unicode whitespace and identifier handling regression tests.
    /// Validates that the lexer correctly handles Unicode whitespace and identifiers.
    /// </summary>
    [Collection("Engine Tests")]
    public class UnicodeConformanceTests
    {
        public UnicodeConformanceTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        private FenRuntime CreateRuntime() => new FenRuntime();

        private bool ParsesWithoutError(string code)
        {
            var lexer = new Lexer(code);
            var parser = new Parser(lexer);
            parser.ParseProgram();
            return parser.Errors.Count == 0;
        }

        [Fact]
        public void Lexer_BasicIdentifiers_Parse()
        {
            Assert.True(ParsesWithoutError("var x = 1;"));
        }

        [Fact]
        public void Lexer_NBSP_AsWhitespace_ParsesWithoutError()
        {
            // U+00A0 (NBSP) should be treated as whitespace
            var code = "var\u00A0x\u00A0=\u00A01;";
            Assert.True(ParsesWithoutError(code));
        }

        [Fact]
        public void Lexer_FormFeed_AsWhitespace()
        {
            // U+000C (Form Feed) should be treated as whitespace
            var code = "var\u000Cx\u000C=\u000C1;";
            Assert.True(ParsesWithoutError(code));
        }

        [Fact]
        public void Lexer_VerticalTab_AsWhitespace()
        {
            // U+000B (Vertical Tab) should be treated as whitespace
            var code = "var\u000Bx\u000B=\u000B1;";
            Assert.True(ParsesWithoutError(code));
        }

        [Fact]
        public void Runtime_UnicodeString_LengthCorrect()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = 'hello'; var len = s.length;");
            Assert.Equal(5.0, rt.GetGlobal("len").ToNumber());
        }

        [Fact]
        public void Runtime_String_CharAtWorks()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = 'abc'; var c = s.charAt(1);");
            Assert.Equal("b", rt.GetGlobal("c").ToString());
        }

        [Fact]
        public void Runtime_String_CharCodeAtWorks()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var code = 'A'.charCodeAt(0);");
            Assert.Equal(65.0, rt.GetGlobal("code").ToNumber());
        }

        [Fact]
        public void Runtime_String_FromCharCode()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = String.fromCharCode(72, 101, 108, 108, 111);");
            Assert.Equal("Hello", rt.GetGlobal("s").ToString());
        }

        [Fact]
        public void Runtime_String_FromCodePoint()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = String.fromCodePoint(65, 66, 67);");
            Assert.Equal("ABC", rt.GetGlobal("s").ToString());
        }

        [Fact]
        public void Runtime_String_CodePointAt()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var cp = 'A'.codePointAt(0);");
            Assert.Equal(65.0, rt.GetGlobal("cp").ToNumber());
        }

        [Fact]
        public void Runtime_Identifier_WithUnderscore()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var _private = 42; var $jquery = 1;");
            Assert.Equal(42.0, rt.GetGlobal("_private").ToNumber());
            Assert.Equal(1.0, rt.GetGlobal("$jquery").ToNumber());
        }
    }
}
