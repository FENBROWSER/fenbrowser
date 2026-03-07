using System;
using Xunit;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Core.Bytecode;
using FenBrowser.FenEngine.Core.Bytecode.Compiler;
using FenBrowser.FenEngine.Core.Bytecode.VM;
using FenBrowser.FenEngine.Errors;
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

        private FenValue Evaluate(AstNode ast, FenEnvironment? env = null)
        {
            var scope = env ?? new FenEnvironment();
            var codeBlock = _compiler.Compile(ast);
            return _vm.Execute(codeBlock, scope);
        }

        private static Program ParseProgram(string js)
        {
            var lexer = new Lexer(js);
            var parser = new Parser(lexer, false);
            return parser.ParseProgram();
        }

        private static FenFunction CreateAstBackedFunction(
            FenEnvironment env,
            string declarationSource,
            string expectedName)
        {
            var program = ParseProgram(declarationSource);
            var declaration = Assert.IsType<FunctionDeclarationStatement>(program.Statements[0]);

            var function = new FenFunction(declaration.Function.Parameters, declaration.Function.Body, env)
            {
                Name = declaration.Function.Name,
                IsAsync = declaration.Function.IsAsync,
                IsGenerator = declaration.Function.IsGenerator
            };

            Assert.Equal(expectedName, function.Name);

            // VM constructor path expects ordinary functions to expose a prototype object.
            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(function));
            function.Prototype = prototype;
            function.Set("prototype", FenValue.FromObject(prototype));

            return function;
        }

        [Fact]
        public void Bytecode_BasicArithmetic_ShouldMatchRuntime()
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
        public void Bytecode_VarDeclarationList_ShouldDeclareAllBindings()
        {
            var result = Evaluate("var expectedName, actualName; actualName = 'TypeError'; expectedName = 'RangeError'; expectedName + ':' + actualName;");
            Assert.Equal("RangeError:TypeError", result.AsString());
        }

        [Fact]
        public void Bytecode_StrictVarDeclarationList_ShouldDeclareAllBindings()
        {
            var result = Evaluate("'use strict'; var expectedName, actualName; actualName = 42; actualName;");
            Assert.Equal(42, result.AsNumber());
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
        public void Bytecode_BitwiseExpressionNode_ShouldCompileAndExecute()
        {
            var program = new Program();
            program.Statements.Add(new ExpressionStatement
            {
                Expression = new BitwiseExpression
                {
                    Left = new IntegerLiteral { Value = 6 },
                    Operator = "^",
                    Right = new IntegerLiteral { Value = 3 }
                }
            });

            var result = Evaluate(program);
            Assert.Equal(5, result.AsNumber());
        }

        [Fact]
        public void Bytecode_CompoundAssignmentExpressionNode_ShouldCompileAndExecute()
        {
            var program = new Program();
            var varToken = new Token(TokenType.Var, "var", 1, 1);
            var identToken = new Token(TokenType.Identifier, "x", 1, 5);

            program.Statements.Add(new LetStatement
            {
                Token = varToken,
                Name = new Identifier(identToken, "x"),
                Value = new IntegerLiteral { Value = 4 }
            });

            program.Statements.Add(new ExpressionStatement
            {
                Expression = new CompoundAssignmentExpression
                {
                    Left = new Identifier(identToken, "x"),
                    Operator = "<<=",
                    Right = new IntegerLiteral { Value = 2 }
                }
            });

            program.Statements.Add(new ExpressionStatement
            {
                Expression = new Identifier(identToken, "x")
            });

            var result = Evaluate(program);
            Assert.Equal(16, result.AsNumber());
        }

        [Fact]
        public void Bytecode_DivideModuloExponent_ShouldWork()
        {
            var result = Evaluate("var x = 20 / 5; var y = 20 % 6; var z = 2 ** 4; x + y + z;");
            Assert.Equal(22, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ComparisonVariants_ShouldWork()
        {
            var notEqual = Evaluate("1 != 2;");
            Assert.True(notEqual.ToBoolean());

            var strictNotEqual = Evaluate("1 !== '1';");
            Assert.True(strictNotEqual.ToBoolean());

            var lessOrEqual = Evaluate("2 <= 2;");
            Assert.True(lessOrEqual.ToBoolean());

            var greaterOrEqual = Evaluate("3 >= 2;");
            Assert.True(greaterOrEqual.ToBoolean());
        }

        [Fact]
        public void Bytecode_NullAndUndefinedLiterals_ShouldWork()
        {
            var result = Evaluate("var a = null; var b = undefined; typeof a + ',' + typeof b;");
            Assert.Equal("object,undefined", result.AsString());
        }

        [Fact]
        public void Bytecode_DoubleLiteral_AndConditionalExpression_ShouldWork()
        {
            var result = Evaluate("var x = 1.5; var y = (x > 2) ? 10 : 20; y;");
            Assert.Equal(20, result.AsNumber());
        }

        [Fact]
        public void Bytecode_NullishCoalescingExpression_ShouldWork()
        {
            var result = Evaluate("var a = null ?? 7; var b = undefined ?? 9; var c = 0 ?? 5; a + b + c;");
            Assert.Equal(16, result.AsNumber());
        }

        [Fact]
        public void Bytecode_UpdateExpressions_ShouldWork()
        {
            var result = Evaluate("var i = 1; var a = i++; var b = ++i; var c = i--; var d = --i; a + b + c + d + i;");
            Assert.Equal(9, result.AsNumber());

            var forLoopResult = Evaluate("var sum = 0; for (var i = 0; i < 4; i++) { sum = sum + i; } sum;");
            Assert.Equal(6, forLoopResult.AsNumber());
        }

        [Fact]
        public void Bytecode_LogicalAssignment_ShouldWork()
        {
            var assignResult = Evaluate("var a = 0; a ||= 5; var b = 2; b &&= 7; var c = null; c ??= 9; a + b + c;");
            Assert.Equal(21, assignResult.AsNumber());

            var shortCircuitResult = Evaluate("var d = 4; d ||= 8; var e = 0; e &&= 10; var f = 11; f ??= 12; d + e + f;");
            Assert.Equal(15, shortCircuitResult.AsNumber());
        }

        [Fact]
        public void Bytecode_BitwiseNot_AndDoWhile_ShouldWork()
        {
            var bitwiseNot = Evaluate("~5;");
            Assert.Equal(-6, bitwiseNot.AsNumber());

            var doWhile = Evaluate("var n = 0; do { n = n + 1; } while (n < 3); n;");
            Assert.Equal(3, doWhile.AsNumber());
        }

        [Fact]
        public void Bytecode_FunctionDeclaration_ShouldCall()
        {
            var result = Evaluate("function add(a, b) { return a + b; } add(10, 20);");
            Assert.Equal(30, result.AsNumber());
        }

        [Fact]
        public void Bytecode_FunctionDeclaration_ShouldBeCallableBeforeSourcePosition()
        {
            var result = Evaluate("add(10, 20); function add(a, b) { return a + b; }");
            Assert.Equal(30, result.AsNumber());
        }

        [Fact]
        public void Bytecode_FunctionDeclaration_InFunctionBody_ShouldHoist()
        {
            var result = Evaluate("function outer() { return inner(); function inner() { return 9; } } outer();");
            Assert.Equal(9, result.AsNumber());
        }

        [Fact]
        public void Bytecode_FunctionDeclaration_DuplicateNames_LastDeclarationWins()
        {
            var result = Evaluate("which(); function which() { return 1; } function which() { return 2; }");
            Assert.Equal(2, result.AsNumber());
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
        public void Bytecode_CallOpcode_WithAstBackedFunction_ShouldFailInBytecodeOnlyMode()
        {
            var env = new FenEnvironment();
            var inc = CreateAstBackedFunction(env, "function inc(x) { return x + 1; }", "inc");
            env.Set("inc", FenValue.FromFunction(inc));

            Assert.Throws<Exception>(() => Evaluate(ParseProgram("function run(v) { return inc(v); } run(41);"), env));
        }

        [Fact]
        public void Bytecode_CallFromArrayOpcode_WithAstBackedFunction_ShouldFailInBytecodeOnlyMode()
        {
            var env = new FenEnvironment();
            var inc = CreateAstBackedFunction(env, "function inc(x) { return x + 1; }", "inc");
            env.Set("inc", FenValue.FromFunction(inc));

            Assert.Throws<Exception>(() => Evaluate(ParseProgram("function run(v) { return inc(...[v]); } run(41);"), env));
        }

        [Fact]
        public void Bytecode_ConstructOpcode_WithAstBackedConstructor_ShouldFailInBytecodeOnlyMode()
        {
            var env = new FenEnvironment();
            var pair = CreateAstBackedFunction(env, "function Pair(a, b) { this.sum = a + b; }", "Pair");
            env.Set("Pair", FenValue.FromFunction(pair));

            Assert.Throws<Exception>(() => Evaluate(ParseProgram("function run(a, b) { var p = new Pair(a, b); return p.sum; } run(5, 7);"), env));
        }

        [Fact]
        public void Bytecode_ConstructFromArrayOpcode_WithAstBackedConstructor_ShouldFailInBytecodeOnlyMode()
        {
            var env = new FenEnvironment();
            var pair = CreateAstBackedFunction(env, "function Pair(a, b) { this.sum = a + b; }", "Pair");
            env.Set("Pair", FenValue.FromFunction(pair));

            Assert.Throws<Exception>(() => Evaluate(ParseProgram("function run(a, b) { var p = new Pair(...[a, b]); return p.sum; } run(5, 7);"), env));
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
        public void Bytecode_CommaOperator_ShouldEvaluateLeftAndReturnRight()
        {
            var result = Evaluate("var x = 0; var y = (x = 1, x + 2); x + y;");
            Assert.Equal(4, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ArrowFunctionExpression_ShouldCall()
        {
            var result = Evaluate("var inc = (x) => x + 1; inc(41);");
            Assert.Equal(42, result.AsNumber());
        }

        [Fact]
        public void Bytecode_DefaultParameters_ShouldApplyWhenArgumentIsUndefined()
        {
            var result = Evaluate("function inc(x = 41) { return x + 1; } var a = inc(); var b = inc(9); a + b;");
            Assert.Equal(52, result.AsNumber());
        }

        [Fact]
        public void Bytecode_RestParameters_ShouldCollectExtraArguments()
        {
            var result = Evaluate("function count(head, ...tail) { return head + tail.length; } count(2, 9, 8);");
            Assert.Equal(4, result.AsNumber());
        }

        [Fact]
        public void Bytecode_FunctionArgumentsObject_ShouldExposeLengthAndIndexedValues()
        {
            var result = Evaluate("function f(a) { return arguments[0] + arguments.length; } f(5, 6);");
            Assert.Equal(7, result.AsNumber());
        }

        [Fact]
        public void Bytecode_FunctionLocals_ShouldExecuteWithLocalSlots()
        {
            var result = Evaluate("function sum(a, b) { var x = a + b; return x + 1; } sum(2, 3);");
            Assert.Equal(6, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ArrayDelete_ShouldCreateHoleAndPreserveLength()
        {
            var result = Evaluate("var a = [1, 2, 3]; delete a[1]; var has = 1 in a; var len = a.length; (has ? 100 : 0) + len;");
            Assert.Equal(3, result.AsNumber());
        }

        [Fact]
        public void Bytecode_DestructuringParameter_ShouldBindObjectPattern()
        {
            var result = Evaluate("function pick({ x, y = 2 }) { return x + y; } pick({ x: 5 });");
            Assert.Equal(7, result.AsNumber());
        }

        [Fact]
        public void Bytecode_DestructuringParameter_WithOuterDefault_ShouldBindObjectPattern()
        {
            var result = Evaluate("function pick({ x } = { x: 5 }) { return x; } pick();");
            Assert.Equal(5, result.AsNumber());
        }

        [Fact]
        public void Bytecode_DestructuringAssignment_ShouldUpdateTargets()
        {
            var result = Evaluate("var a = 0; var b = 0; ({ a, b } = { a: 6, b: 9 }); a + b;");
            Assert.Equal(15, result.AsNumber());
        }

        [Fact]
        public void Bytecode_DestructuringDeclaration_ShouldBindFromExpressionSource()
        {
            var result = Evaluate("let { x, y = 2 } = { x: 5 }; x + y;");
            Assert.Equal(7, result.AsNumber());
        }

        [Fact]
        public void Bytecode_DestructuringObjectRest_ShouldCollectRemainingProperties()
        {
            var result = Evaluate("let { a, ...rest } = { a: 1, b: 2, c: 3 }; a + rest.b + rest.c;");
            Assert.Equal(6, result.AsNumber());
        }

        [Fact]
        public void Bytecode_DestructuringArrayRest_ShouldCollectRemainingElements()
        {
            var result = Evaluate("let [head, ...tail] = [1, 2, 3, 4]; head + tail.length + tail[2];");
            Assert.Equal(8, result.AsNumber());
        }

        [Fact]
        public void Bytecode_DestructuringComputedKey_ShouldResolveRuntimeKey()
        {
            var result = Evaluate("var key = 'z'; let { [key]: out } = { z: 9 }; out;");
            Assert.Equal(9, result.AsNumber());
        }

        [Fact]
        public void Bytecode_CatchDestructuring_ShouldBindPattern()
        {
            var result = Evaluate("var out = 0; try { throw { x: 8 }; } catch ({ x }) { out = x; } out;");
            Assert.Equal(8, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ForOfDestructuring_ShouldBindPatternPerIteration()
        {
            var result = Evaluate("var s = 0; for (const { v } of ({ a: { v: 1 }, b: { v: 2 }, c: { v: 3 } })) { s = s + v; } s;");
            Assert.Equal(6, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ArrowFunction_ConstructShouldThrowTypeError()
        {
            var result = Evaluate("var f = (x) => x + 1; var ok = 0; try { new f(); } catch (e) { ok = 1; } ok;");
            Assert.Equal(1, result.AsNumber());
        }

        [Fact]
        public void Bytecode_OptionalChain_PropertyAndComputed_ShouldWork()
        {
            var result = Evaluate("var obj = { a: 5 }; var x = obj?.a; var y = obj?.['a']; x + y;");
            Assert.Equal(10, result.AsNumber());
        }

        [Fact]
        public void Bytecode_OptionalChain_NullishShortCircuit_ShouldReturnUndefined()
        {
            var result = Evaluate("var obj = undefined; var x = obj?.a; typeof x;");
            Assert.Equal("undefined", result.AsString());
        }

        [Fact]
        public void Bytecode_OptionalChain_OptionalCall_ShouldWork()
        {
            var result = Evaluate("var fn = function(x) { return x + 1; }; var a = fn?.(41); var b = 3?.(); typeof b + ':' + a;");
            Assert.Equal("undefined:42", result.AsString());
        }

        [Fact]
        public void Bytecode_InAndInstanceofOperators_ShouldWork()
        {
            var result = Evaluate("function C() {} C.prototype.p = 1; var o = new C(); var hasP = 'p' in o; var inst = o instanceof C; hasP && inst;");
            Assert.True(result.ToBoolean());
        }

        [Fact]
        public void Bytecode_VoidDeleteAndUnaryPlus_ShouldWork()
        {
            var result = Evaluate("var obj = { a: 1 }; var deleted = delete obj.a; var v = void (obj.a = 2); var isUndefined = typeof v == 'undefined'; var n = +'41'; (deleted ? 1000 : 0) + (isUndefined ? 10 : 0) + n;");
            Assert.Equal(1051, result.AsNumber());
        }

        [Fact]
        public void Bytecode_TemplateLiteral_ShouldWork()
        {
            var result = Evaluate("var x = 2; `sum=${x + 1}`;");
            Assert.Equal("sum=3", result.AsString());
        }

        [Fact]
        public void Bytecode_SwitchStatement_WithFallthroughAndBreak_ShouldWork()
        {
            var result = Evaluate("var x = 0; switch (2) { case 1: x = 10; break; case 2: x = 20; case 3: x = x + 1; break; default: x = 99; } x;");
            Assert.Equal(21, result.AsNumber());
        }

        [Fact]
        public void Bytecode_BreakAndContinue_InForLoop_ShouldWork()
        {
            var result = Evaluate("var sum = 0; for (var i = 0; i < 5; i = i + 1) { if (i == 2) { continue; } if (i == 4) { break; } sum = sum + i; } sum;");
            Assert.Equal(4, result.AsNumber());
        }

        [Fact]
        public void Bytecode_RegexLiteral_ShouldCreateRegexObjectLikeRuntime()
        {
            var result = Evaluate("var r = /ab/i; r.source + ':' + r.flags;");
            Assert.Equal("ab:i", result.AsString());
        }

        [Fact]
        public void Bytecode_DeleteIdentifier_ShouldReturnFalse()
        {
            var result = Evaluate("var x = 1; delete x;");
            Assert.False(result.ToBoolean());
        }

        [Fact]
        public void Bytecode_DeleteNonReferenceExpression_ShouldReturnTrue()
        {
            var result = Evaluate("delete (1 + 2);");
            Assert.True(result.ToBoolean());
        }

        [Fact]
        public void Bytecode_DeleteNonReferenceExpression_ShouldPreserveOperandSideEffects()
        {
            var result = Evaluate("var x = 0; var ok = delete (x = 1); (ok ? 10 : 0) + x;");
            Assert.Equal(11, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ArrayLiteral_WithSpread_ShouldWork()
        {
            var result = Evaluate("var a = [1, ...[2, 3], 4]; a[0] + a[1] + a[2] + a[3];");
            Assert.Equal(10, result.AsNumber());
        }

        [Fact]
        public void Bytecode_Call_WithSpread_ShouldWork()
        {
            var result = Evaluate("function add3(a, b, c) { return a + b + c; } add3(1, ...[2, 3]);");
            Assert.Equal(6, result.AsNumber());
        }

        [Fact]
        public void Bytecode_New_WithSpread_ShouldWork()
        {
            var result = Evaluate("function Pair(a, b) { this.sum = a + b; } var p = new Pair(...[5, 7]); p.sum;");
            Assert.Equal(12, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ObjectLiteral_WithSpread_ShouldWork()
        {
            var result = Evaluate("var a = { x: 1 }; var b = { ...a, y: 2 }; b.x + b.y;");
            Assert.Equal(3, result.AsNumber());
        }

        [Fact]
        public void Bytecode_NewTarget_InNormalCall_ShouldBeUndefined()
        {
            var result = Evaluate("function F() { return typeof new.target; } F();");
            Assert.Equal("undefined", result.AsString());
        }

        [Fact]
        public void Bytecode_NewTarget_InConstructor_ShouldReferenceConstructor()
        {
            var result = Evaluate("function F() { this.ok = (new.target === F); } var i = new F(); i.ok;");
            Assert.True(result.ToBoolean());
        }

        [Fact]
        public void Bytecode_ImportMeta_ShouldExposeUrl()
        {
            var result = Evaluate("var m = import.meta; m.url;");
            Assert.Equal("file:///local/script.js", result.AsString());
        }

        [Fact]
        public void Bytecode_IfExpression_ShouldEvaluateToBranchValue()
        {
            var result = Evaluate("var a = if (true) { 1; } else { 2; }; var b = if (false) { 10; } else { 3; }; a + b;");
            Assert.Equal(4, result.AsNumber());
        }

        [Fact]
        public void Bytecode_IfExpression_WithoutElse_ShouldReturnNull()
        {
            var result = Evaluate("var x = if (false) { 1; }; x === null;");
            Assert.True(result.ToBoolean());
        }

        [Fact]
        public void Bytecode_AsyncFunctionDeclaration_ShouldReturnResolvedPromise()
        {
            var result = Evaluate("async function f() { return 7; } f();");
            Assert.True(result.IsObject);

            var promise = result.AsObject() as JsPromise;
            Assert.NotNull(promise);
            Assert.True(promise.IsFulfilled);
            Assert.Equal(7, promise.Result.AsNumber());
        }

        [Fact]
        public void Bytecode_AsyncFunctionExpression_ShouldReturnResolvedPromise()
        {
            var result = Evaluate("var g = async function(x) { return x + 1; }; g(5);");
            Assert.True(result.IsObject);

            var promise = result.AsObject() as JsPromise;
            Assert.NotNull(promise);
            Assert.True(promise.IsFulfilled);
            Assert.Equal(6, promise.Result.AsNumber());
        }

        [Fact]
        public void Bytecode_AwaitExpression_OnPlainValue_ShouldReturnValue()
        {
            var result = Evaluate("async function f() { return await 5; } f();");
            Assert.True(result.IsObject);

            var promise = result.AsObject() as JsPromise;
            Assert.NotNull(promise);
            Assert.True(promise.IsFulfilled);
            Assert.Equal(5, promise.Result.AsNumber());
        }

        [Fact]
        public void Bytecode_AwaitExpression_OnPromiseValue_ShouldUnwrapResult()
        {
            var result = Evaluate("async function g() { return 3; } async function f() { return await g(); } f();");
            Assert.True(result.IsObject);

            var promise = result.AsObject() as JsPromise;
            Assert.NotNull(promise);
            Assert.True(promise.IsFulfilled);
            Assert.Equal(3, promise.Result.AsNumber());
        }

        [Fact]
        public void Bytecode_AsyncThrow_ShouldReturnRejectedPromise()
        {
            var result = Evaluate("async function boom() { throw 'boom'; } boom();");
            Assert.True(result.IsObject);

            var promise = result.AsObject() as JsPromise;
            Assert.NotNull(promise);
            Assert.True(promise.IsRejected);
            Assert.Equal("boom", promise.Result.AsString());
        }

        [Fact]
        public void Bytecode_LabeledBreak_OnBlock_ShouldWork()
        {
            var result = Evaluate("var x = 0; mark: { x = 1; break mark; x = 2; } x;");
            Assert.Equal(1, result.AsNumber());
        }

        [Fact]
        public void Bytecode_LabeledContinue_ToOuterLoop_ShouldWork()
        {
            var result = Evaluate("var hits = 0; outer: for (var i = 0; i < 3; i = i + 1) { for (var j = 0; j < 3; j = j + 1) { if (j == 1) { continue outer; } hits = hits + 1; } } hits;");
            Assert.Equal(3, result.AsNumber());
        }

        [Fact]
        public void Bytecode_LabeledBreak_FromSwitchToOuterLoop_ShouldWork()
        {
            var result = Evaluate("var n = 0; outer: for (var i = 0; i < 5; i = i + 1) { switch (i) { case 2: break outer; default: n = n + 1; } } n;");
            Assert.Equal(2, result.AsNumber());
        }

        [Fact]
        public void Bytecode_BigIntAddition_WithBigIntOperands_ShouldReturnBigInt()
        {
            var result = Evaluate("123n + 1n;");
            Assert.True(result.IsBigInt);
            Assert.Equal("124", result.AsBigInt().ToStringWithoutSuffix());
        }

        [Fact]
        public void Bytecode_BigIntAddition_MixedWithNumber_ShouldThrowTypeError()
        {
            var ex = Assert.Throws<Exception>(() => Evaluate("123n + 1;"));
            Assert.Contains("Cannot mix BigInt and other types", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Bytecode_Addition_ToPrimitiveValueOf_ShouldUseNumericPrimitive()
        {
            var result = Evaluate("({ valueOf: function() { return 7; } }) + 5;");
            Assert.Equal(12, result.AsNumber());
        }

        [Fact]
        public void Bytecode_Addition_ToPrimitiveToString_ShouldUseStringConcatenation()
        {
            var result = Evaluate("({ toString: function() { return 'x'; } }) + 1;");
            Assert.Equal("x1", result.AsString());
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

        [Fact]
        public void Bytecode_ClassStatement_WithFieldsStaticBlockAndMethods_ShouldWork()
        {
            var result = Evaluate("class C { v = 2; m() { return 9; } } var c = new C(); c.v;");
            Assert.Equal(2, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ClassExpression_ShouldReturnConstructableFunction()
        {
            var result = Evaluate("var C = class { q = 5; }; var c = new C(); c.q;");
            Assert.Equal(5, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ClassStatement_StaticField_ShouldBindOnConstructor()
        {
            var source = "class C { static a = 4; v = 3; } C.a;";
            var lexer = new Lexer(source);
            var parser = new Parser(lexer, false);
            var ast = parser.ParseProgram();

            var classStmt = Assert.IsType<ClassStatement>(ast.Statements[0]);
            Assert.Contains(classStmt.Properties, p => p.Static && p.Key?.Value == "a");

            var result = Evaluate(source);
            Assert.Equal(4, result.AsNumber());
        }

        [Fact]
        public void Bytecode_PrivateIdentifierNode_ShouldReadPrivatePrefixedFieldFromThis()
        {
            var env = new FenEnvironment();
            var thisObj = new FenObject();
            thisObj.Set("#secret", FenValue.FromNumber(42));
            env.Set("this", FenValue.FromObject(thisObj));

            var node = new PrivateIdentifier(new Token(TokenType.PrivateIdentifier, "#secret", 1, 1), "secret");
            var program = new Program();
            program.Statements.Add(new ExpressionStatement { Expression = node });

            var result = Evaluate(program, env);
            Assert.Equal(42, result.AsNumber());
        }

        [Fact]
        public void Bytecode_YieldExpressionNode_ShouldEvaluateYieldValue()
        {
            var program = new Program();
            program.Statements.Add(new ExpressionStatement
            {
                Expression = new YieldExpression
                {
                    Value = new IntegerLiteral { Value = 7 },
                    Delegate = false
                }
            });

            var result = Evaluate(program);
            Assert.Equal(7, result.AsNumber());
        }

        [Fact]
        public void Bytecode_WithStatement_ShouldResolveObjectBackedNames()
        {
            var result = Evaluate("with ({ x: 9 }) { x; }");
            Assert.Equal(9, result.AsNumber());
        }

        [Fact]
        public void Bytecode_WithStatement_WithUndefinedTarget_ShouldThrowTypeError()
        {
            var ex = Assert.Throws<Exception>(() => Evaluate("with (undefined) { x; }"));
            Assert.Contains("Cannot convert undefined or null to object", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Bytecode_WithStatement_RespectsUnscopables()
        {
            var result = Evaluate("var x = 1; var o = { x: 9 }; o['Symbol.unscopables'] = { x: true }; with (o) { x; }");
            Assert.Equal(1, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ImportDeclarationNode_ShouldBindFromSyntheticModuleSlot()
        {
            var env = new FenEnvironment();
            var moduleObj = new FenObject();
            moduleObj.Set("x", FenValue.FromNumber(11));
            env.Set("__fen_module_mod", FenValue.FromObject(moduleObj));

            var token = new Token(TokenType.Identifier, "x", 1, 1);
            var program = new Program();
            program.Statements.Add(new ImportDeclaration
            {
                Source = "mod",
                Specifiers = new System.Collections.Generic.List<ImportSpecifier>
                {
                    new ImportSpecifier
                    {
                        Imported = new Identifier(token, "x"),
                        Local = new Identifier(token, "lx")
                    }
                }
            });
            program.Statements.Add(new ExpressionStatement
            {
                Expression = new Identifier(token, "lx")
            });

            var result = Evaluate(program, env);
            Assert.Equal(11, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ExportDeclarationNode_ShouldWriteSyntheticExportSlot()
        {
            var token = new Token(TokenType.Identifier, "a", 1, 1);
            var varToken = new Token(TokenType.Var, "var", 1, 1);
            var program = new Program();

            program.Statements.Add(new LetStatement
            {
                Token = varToken,
                Name = new Identifier(token, "a"),
                Value = new IntegerLiteral { Value = 13 }
            });

            program.Statements.Add(new ExportDeclaration
            {
                Specifiers = new System.Collections.Generic.List<ExportSpecifier>
                {
                    new ExportSpecifier
                    {
                        Local = new Identifier(token, "a"),
                        Exported = new Identifier(token, "b")
                    }
                }
            });

            program.Statements.Add(new ExpressionStatement
            {
                Expression = new Identifier(token, "__fen_export_b")
            });

            var result = Evaluate(program);
            Assert.Equal(13, result.AsNumber());
        }

        [Fact]
        public void Bytecode_MethodDefinitionNode_ShouldCompileWithoutFallback()
        {
            var token = new Token(TokenType.Identifier, "m", 1, 1);
            var method = new MethodDefinition
            {
                Key = new Identifier(token, "m"),
                Kind = "method",
                Static = false,
                Value = new FunctionLiteral
                {
                    Token = token,
                    Body = new BlockStatement
                    {
                        Statements = new System.Collections.Generic.List<Statement>
                        {
                            new ReturnStatement
                            {
                                ReturnValue = new IntegerLiteral { Value = 1 }
                            }
                        }
                    }
                }
            };

            var program = new Program();
            program.Statements.Add(method);
            var result = Evaluate(program);
            Assert.True(result.IsUndefined);
        }

        [Fact]
        public void Bytecode_ClassPropertyNode_ShouldEvaluateInitializer()
        {
            var token = new Token(TokenType.Identifier, "p", 1, 1);
            var assign = new AssignmentExpression
            {
                Left = new Identifier(token, "z"),
                Right = new IntegerLiteral { Value = 21 }
            };

            var program = new Program();
            program.Statements.Add(new ClassProperty
            {
                Key = new Identifier(token, "p"),
                Value = assign
            });
            program.Statements.Add(new ExpressionStatement
            {
                Expression = new Identifier(token, "z")
            });

            var result = Evaluate(program);
            Assert.Equal(21, result.AsNumber());
        }

        [Fact]
        public void Bytecode_StaticBlockNode_ShouldExecuteBody()
        {
            var token = new Token(TokenType.Var, "var", 1, 1);
            var idToken = new Token(TokenType.Identifier, "s", 1, 1);
            var program = new Program();

            program.Statements.Add(new StaticBlock
            {
                Body = new BlockStatement
                {
                    Statements = new System.Collections.Generic.List<Statement>
                    {
                        new LetStatement
                        {
                            Token = token,
                            Name = new Identifier(idToken, "s"),
                            Value = new IntegerLiteral { Value = 8 }
                        }
                    }
                }
            });
            program.Statements.Add(new ExpressionStatement
            {
                Expression = new Identifier(idToken, "s")
            });

            var result = Evaluate(program);
            Assert.Equal(8, result.AsNumber());
        }

        [Fact]
        public void Bytecode_ForOf_CustomSymbolIterator_ShouldWork()
        {
            var code = @"
                var obj = {};
                obj['[Symbol.iterator]'] = function() {
                    var i = 0;
                    return {
                        next: function() {
                            if (i < 3) {
                                i = i + 1;
                                return { value: i * 10, done: false };
                            }
                            return { value: undefined, done: true };
                        }
                    };
                };
                var sum = 0;
                for (var v of obj) {
                    sum = sum + v;
                }
                sum;
            ";
            var result = Evaluate(code);
            Assert.Equal(60, result.AsNumber());
        }
    }
}



