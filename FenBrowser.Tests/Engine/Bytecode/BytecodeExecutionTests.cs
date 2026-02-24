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

        [Fact]
        public void Bytecode_ArrayLiteral_ShouldCreateArray()
        {
            var result = Evaluate("var a = [10, 20, 30]; a[1];");
            Assert.Equal(20, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ObjectLiteral_ShouldCreateObject()
        {
            var result = Evaluate("var obj = { x: 5, y: 15 }; obj.x + obj.y;");
            Assert.Equal(20, result.AsNumber());
        }

        [Fact]
        public void Bytecode_PropertyAssignment_ShouldStoreAndLoad()
        {
            var result = Evaluate("var obj = {}; obj.a = 100; obj['b'] = 200; obj.a + obj.b;");
            Assert.Equal(300, result.AsNumber());
        }

        [Fact]
        public void Bytecode_UnaryAndTypeof_ShouldWork()
        {
            var result = Evaluate("var x = -10; typeof x;");
            Assert.Equal("number", result.AsString());

            var boolResult = Evaluate("!true;");
            Assert.Equal(false, boolResult.ToBoolean());
        }

        [Fact]
        public void Bytecode_BitwiseOperations_ShouldWork()
        {
            var result = Evaluate("var x = 5 | 2; x;");
            Assert.Equal(7, result.AsNumber());

            var andResult = Evaluate("5 & 1;");
            Assert.Equal(1, andResult.AsNumber());
            
            var shiftResult = Evaluate("1 << 3;");
            Assert.Equal(8, shiftResult.AsNumber());
        }

        [Fact]
        public void Bytecode_FunctionDeclaration_ShouldCall()
        {
            var result = Evaluate("function add(a, b) { return a + b; } add(10, 20);");
            Assert.Equal(30, result.AsNumber());
        }

        [Fact]
        public void Bytecode_Closures_ShouldCaptureEnvironment()
        {
            var result = Evaluate("function makeAdder(x) { return function(y) { return x + y; }; } var add5 = makeAdder(5); add5(15);");
            Assert.Equal(20, result.AsNumber());
        }

        [Fact]
        public void Bytecode_FunctionExpression_ShouldCall()
        {
            var result = Evaluate("var mult = function(a, b) { return a * b; }; mult(4, 5);");
            Assert.Equal(20, result.AsNumber());
        }

        [Fact]
        public void Bytecode_LogicalShortCircuit_ShouldWork()
        {
            var result = Evaluate("var x = 0; true || (x = 1); x;");
            Assert.Equal(0, result.AsNumber()); // x remains 0

            var result2 = Evaluate("var y = 0; false && (y = 1); y;");
            Assert.Equal(0, result2.AsNumber()); // y remains 0
            
            var result3 = Evaluate("true && 5;");
            Assert.Equal(5, result3.AsNumber());

            var result4 = Evaluate("false || 10;");
            Assert.Equal(10, result4.AsNumber());
        }

        [Fact]
        public void Bytecode_ForIn_ShouldIterateKeys()
        {
            var result = Evaluate("var obj = {a: 1, b: 2}; var keys = ''; for (var k in obj) { keys = keys + k; } keys;");
            Assert.Equal("ab", result.AsString());
        }

        [Fact]
        public void Bytecode_ForOf_ShouldIterateValues()
        {
            var result = Evaluate("var obj = {a: 10, b: 20}; var sum = 0; for (var v of obj) { sum = sum + v; } sum;");
            Assert.Equal(30, result.AsNumber());
        }

        [Fact]
        public void Bytecode_TryCatch_ShouldHandleExceptions()
        {
            var code = @"
                var result = 0;
                try {
                    throw 42;
                    result = 1;
                } catch (e) {
                    result = e;
                }
                result;
            ";
            var result = Evaluate(code);
            Assert.Equal(42, result.AsNumber());
        }
    }
}
