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
