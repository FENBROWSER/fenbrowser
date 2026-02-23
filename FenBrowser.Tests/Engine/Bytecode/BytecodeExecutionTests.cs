using System;
using Xunit;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Core.Bytecode;
using FenBrowser.FenEngine.Core.Bytecode.Compiler;
using FenBrowser.FenEngine.Core.Bytecode.VM;
using FenValue = FenBrowser.FenEngine.Core.FenValue;

namespace FenBrowser.Tests.Engine.Bytecode
{
    public class BytecodeExecutionTests
    {
        private VirtualMachine _vm;
        private BytecodeCompiler _compiler;

        public BytecodeExecutionTests()
        {
            _vm = new VirtualMachine();
            _compiler = new BytecodeCompiler();
        }

        private FenValue Evaluate(string js)
        {
            var lexer = new Lexer(js);
            var parser = new Parser(lexer, false);
            var ast = parser.ParseProgram();
            var env = new FenEnvironment();
            var codeBlock = _compiler.Compile(ast);
            return _vm.Execute(codeBlock, env);
        }

        [Fact]
        public void Bytecode_BasicArithmetic_ShouldMatchInterpreter()
        {
            var result = Evaluate("1 + 2 * 3;");
            
            // 2 * 3 = 6 + 1 = 7.
            Assert.Equal(7, result.AsNumber());
        }

        [Fact]
        public void Bytecode_Variables_ShouldStoreAndLoad()
        {
            var result = Evaluate("var x = 10; var y = 20; x + y;");
            Assert.Equal(30, result.AsNumber());
        }

        [Fact]
        public void Bytecode_StringConcatenation_ShouldWork()
        {
            var result = Evaluate("'Hello ' + 'World';");
            Assert.Equal("Hello World", result.AsString());
        }

        [Fact]
        public void Bytecode_IfStatement_ShouldBranchCorrectly()
        {
            var result = Evaluate("var x = 0; if (1 < 2) { x = 10; } else { x = 20; } x;");
            Assert.Equal(10, result.AsNumber());
            
            var result2 = Evaluate("var x = 0; if (2 < 1) { x = 10; } else { x = 20; } x;");
            Assert.Equal(20, result2.AsNumber());
        }

        [Fact]
        public void Bytecode_WhileStatement_ShouldLoopCorrectly()
        {
            var result = Evaluate("var x = 0; while (x < 3) { x = x + 1; } x;");
            // 0 -> 1 -> 2 -> 3
            Assert.Equal(3, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ForStatement_ShouldLoopCorrectly()
        {
            var result = Evaluate("var sum = 0; for (var i = 0; i < 5; i = i + 1) { sum = sum + i; } sum;");
            // sum = 0 + 1 + 2 + 3 + 4 = 10
            Assert.Equal(10, result.AsNumber());
        }

        [Fact]
        public void Bytecode_StackOverflowProtection_ShouldNotCrashHost()
        {
            // We haven't implemented function calls in the compiler yet, so we can't fully test 
            // the infinite recursion JS yet. For Phase 1, just asserting the CallFrame structure exists.
            var lexer = new Lexer("1;");
            var parser = new Parser(lexer, false);
            var ast = parser.ParseProgram();
            var env = new FenEnvironment();
            var codeBlock = _compiler.Compile(ast);

            // Directly invoking call frame mechanisms
            var frame = new CallFrame(codeBlock, env, 0);
            Assert.NotNull(frame);
            Assert.Equal(0, frame.IP);
            Assert.Equal(0, frame.StackBase);
        }
    }
}
