using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectAndBytecodeTests
{
    [Fact]
    public void ObjectPropertyDescriptorAndPrototypeLookupWorks()
    {
        var heap = new JsHeap();
        var parent = new JsObject();
        parent.DefineOwnProperty("p", new JsPropertyDescriptor(JsValue.FromNumber(10), true, true, true));
        var parentHandle = heap.AllocateObject(parent, AllocationSite.Current());

        var child = new JsObject();
        child.SetPrototype(parentHandle);

        var found = child.TryGetProperty("p", h => heap.GetObject(h), out var descriptor);
        Assert.True(found);
        Assert.Equal(10, descriptor.Value.AsNumber());
    }

    [Fact]
    public void NonWritablePropertyCannotBeSet()
    {
        var obj = new JsObject();
        obj.DefineOwnProperty("x", new JsPropertyDescriptor(JsValue.FromNumber(1), Writable: false, Enumerable: true, Configurable: true));

        Assert.False(obj.SetProperty("x", JsValue.FromNumber(2)));
        Assert.True(obj.TryGetOwnProperty("x", out var descriptor));
        Assert.Equal(1, descriptor.Value.AsNumber());
    }

    [Fact]
    public void BytecodeVerifierRejectsMissingReturn()
    {
        var fn = new BytecodeFunction
        {
            RegisterCount = 2,
            Constants = Array.Empty<JsValue>(),
            VariableSlots = new Dictionary<string, int>(),
            PropertyNames = Array.Empty<string>(),
            ParameterNames = Array.Empty<string>(),
            NestedFunctions = Array.Empty<BytecodeFunction>(),
            Instructions = new[] { new Instruction(OpCode.LoadConst, 1, 0, 0) }
        };

        var verifier = new BytecodeVerifier();
        Assert.Throws<InvalidOperationException>(() => verifier.Verify(fn));
    }

    [Fact]
    public void BytecodeVerifierRejectsInvalidJumpTarget()
    {
        var fn = new BytecodeFunction
        {
            RegisterCount = 2,
            Constants = new[] { JsValue.FromNumber(1) },
            VariableSlots = new Dictionary<string, int>(),
            PropertyNames = Array.Empty<string>(),
            ParameterNames = Array.Empty<string>(),
            NestedFunctions = Array.Empty<BytecodeFunction>(),
            Instructions = new[]
            {
                new Instruction(OpCode.LoadConst, 1, 0, 0),
                new Instruction(OpCode.Jump, 99, 0, 0),
                new Instruction(OpCode.Return, 0, 0, 0)
            }
        };

        var verifier = new BytecodeVerifier();
        Assert.Throws<InvalidOperationException>(() => verifier.Verify(fn));
    }

    [Fact]
    public void CompilerAndInterpreterRunArithmeticAndVariables()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 1 + 2 * 3; x = x + 1; x;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(8, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterRunIfElse()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 0; if (1) { x = 7; } else { x = 9; } x;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(7, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterRunWhileLoop()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 3; while (x) { x = x - 1; } x;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(0, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHonorReturn()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 1; return x + 2; x = 99;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(3, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleTryCatchThrow()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 1; try { throw 9; } catch (e) { x = e; } x;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(9, result.AsNumber());
    }

    [Fact]
    public void InterpreterThrowsForUncaughtThrow()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("throw 5;"));
        new BytecodeVerifier().Verify(fn);

        var ex = Assert.Throws<JsThrownException>(() => new BytecodeInterpreter().Execute(fn));
        Assert.Equal(5, ex.Value.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleObjectAndMemberAccess()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = { a: 1 }; o.a = o.a + 2; o.a;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(3, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleComputedObjectPropertyKey()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = { [1 + 1]: 7 }; o[2];"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(7, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleArrayLiteralAndIndexAccess()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let arr = [1,2,3]; arr[1] = arr[1] + 5; arr[1];"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(7, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleZeroArgFunctionCall()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function value(){ return 41; } value();"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(41, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleSingleArgFunctionCall()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function inc(x){ return x + 1; } inc(9);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(10, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleMultiArgFunctionCallViaCallN()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function add(a,b){ return a + b; } add(2,3);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(5, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterCaptureOuterVariableSnapshot()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 4; function f(){ return x + 1; } x = 100; f();"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(5, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleComparisonAndEqualityOperators()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let a = 2 < 3; let b = 5 >= 5; let c = 4 == 4; let d = 1 != 2; (a && b) || (c && d);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CompilerAndInterpreterHandleModuloOperator()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("10 % 3;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(1, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleUnaryAndConditionalExpressions()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = !0 ? -3 : 5; x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(-3, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleForLoop()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let sum = 0; for (let i = 0; i < 4; i = i + 1) { sum = sum + i; } sum;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(6, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleBreakAndContinueInForLoop()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let s = 0; for (let i = 0; i < 6; i = i + 1) { if (i == 2) continue; if (i == 5) break; s = s + i; } s;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(8, result.AsNumber());
    }

    [Fact]
    public void CompilerRejectsBreakOutsideLoop()
    {
        var compiler = new BytecodeCompiler();
        Assert.Throws<InvalidOperationException>(() => compiler.CompileScript(new SourceText("break;")));
    }

    [Fact]
    public void CompilerRejectsContinueOutsideLoop()
    {
        var compiler = new BytecodeCompiler();
        Assert.Throws<InvalidOperationException>(() => compiler.CompileScript(new SourceText("continue;")));
    }

    [Fact]
    public void CompilerAndInterpreterHandleFunctionExpressionCall()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let r = (function(a){ return a + 1; })(2); r;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(3, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleNewExpressionReturnsObjectByDefault()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function C(a,b){ return a + b; } let o = new C(1,2); o;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(JsValueTag.Object, result.Tag);
    }

    [Fact]
    public void ConstructorThisBindsToNewInstance()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function C(){ this.x = 7; } let o = new C(); o.x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(7, result.AsNumber());
    }

    [Fact]
    public void PlainFunctionCallThisIsUndefinedInCurrentSubset()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function f(){ return typeof this == \"undefined\"; } f();"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void MethodCallBindsThisForZeroArgCall()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = { x: 7, getX: function(){ return this.x; } }; o.getX();"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(7, result.AsNumber());
    }

    [Fact]
    public void MethodCallBindsThisForSingleArgCall()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = { x: 2, add: function(y){ return this.x + y; } }; o.add(5);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(7, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleTypeofUnary()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("typeof 1 == \"number\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CompilerAndInterpreterHandleVoidUnary()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("typeof (void 1) == \"undefined\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CompilerAndInterpreterHandleDeleteUnary()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("delete x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void DeleteRemovesNamedObjectProperty()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = { a: 1 }; delete o.a; o.a == undefined;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void DeleteRemovesComputedObjectProperty()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = { a: 1 }; let k = \"a\"; delete o[k]; o.a == undefined;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void DeleteReturnsFalseForNonConfigurableProperty()
    {
        var obj = new JsObject();
        obj.DefineOwnProperty("x", new JsPropertyDescriptor(JsValue.FromNumber(1), Writable: true, Enumerable: true, Configurable: false));

        Assert.False(obj.DeleteProperty("x"));
        Assert.True(obj.TryGetOwnProperty("x", out _));
    }

    [Fact]
    public void DeleteOnNonObjectPropertyBaseReturnsTrue()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("delete (1).x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void DeleteOnNonObjectComputedBaseReturnsTrue()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("delete true[\"x\"];"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void InOperatorFindsOwnProperty()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("\"a\" in { a: 1 };"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void InOperatorFindsPropertyOnPrototypeChain()
    {
        var heap = new JsHeap();
        var parent = new JsObject();
        parent.DefineOwnProperty("p", new JsPropertyDescriptor(JsValue.FromNumber(1), true, true, true));
        var parentHandle = heap.AllocateObject(parent, AllocationSite.Current());

        var child = new JsObject();
        child.SetPrototype(parentHandle);
        var childHandle = heap.AllocateObject(child, AllocationSite.Current());

        var key = JsValue.FromString("p");
        var has = heap.GetObject(childHandle).TryGetProperty(key.AsString(), h => heap.GetObject(h), out _);
        Assert.True(has);
    }

    [Fact]
    public void InOperatorThrowsWhenRightSideIsNotObject()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("\"a\" in 1;"));
        new BytecodeVerifier().Verify(fn);
        Assert.Throws<InvalidOperationException>(() => new BytecodeInterpreter().Execute(fn));
    }

    [Fact]
    public void InstanceOfReturnsTrueForConstructedInstance()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function C(){}; let o = new C(); o instanceof C;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void InstanceOfReturnsFalseForNonObjectLeft()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("1 instanceof Object;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void TypeofFunctionObjectReturnsFunction()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("typeof function(){} == \"function\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CompilerAndInterpreterHandleRegexLiteralAsObjectPlaceholder()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let r = /abc/i; r;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(JsValueTag.Object, result.Tag);
    }

    [Fact]
    public void DivisionExpressionStillParsesAndExecutes()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("6 / 2;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(3, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleCompoundAssignment()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 1; x += 2; x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(3, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleUnaryPlus()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = +5; x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(5, result.AsNumber());
    }

    [Fact]
    public void LooseEqualityConvertsStringAndNumber()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("\"1\" == 1;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void LooseEqualityConvertsBooleanAndNumber()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("!0 == 1;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CompilerAndInterpreterHandleBooleanLiteral()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("true;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CompilerAndInterpreterHandleNullLiteral()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("null == undefined;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void StrictEqualityDiffersFromLooseEqualityForMixedTypes()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("(0 == false) != (0 === false);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void StrictEqualityTreatsInt32AndNumberAsSameNumericType()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("1 === (1 + 0.5 - 0.5);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void StrictInequalityTreatsInt32AndNumberAsEqualWhenNumericValuesMatch()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("1 !== (1 + 0.5 - 0.5);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void EmptyStringIsFalsyInConditional()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 0; if (\"\") { x = 1; } x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(0, result.AsNumber());
    }

    [Fact]
    public void NonEmptyStringIsTruthyInConditional()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 0; if (\"a\") { x = 1; } x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(1, result.AsNumber());
    }

    [Fact]
    public void PlusConcatenatesWhenLeftOperandIsString()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("\"x\" + 1;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal("x1", result.AsString());
    }

    [Fact]
    public void PlusConcatenatesWhenRightOperandIsString()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("1 + \"x\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal("1x", result.AsString());
    }

    [Fact]
    public void ArithmeticCoercesStringAndBooleanOperandsToNumbers()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("(\"2\" - 1) + (true * 2);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(3, result.AsNumber());
    }

    [Fact]
    public void UnaryPlusCoercesStringToNumber()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("+\"5\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(5, result.AsNumber());
    }

    [Fact]
    public void EmptyStringCoercesToZeroInUnaryPlus()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("+\"\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(0, result.AsNumber());
    }

    [Fact]
    public void WhitespaceStringCoercesToZeroInUnaryPlus()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("+\"   \";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(0, result.AsNumber());
    }

    [Fact]
    public void LooseEqualityTreatsEmptyStringAsZero()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("\"\" == 0;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RelationalOperatorsCompareStringsLexicographically()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("\"2\" > \"10\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RelationalOperatorsUseNumericPathForMixedTypes()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("\"2\" > 10;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void LogicalAndReturnsOriginalOperandValue()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("0 && 5;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(0, result.AsNumber());
    }

    [Fact]
    public void LogicalOrReturnsOriginalOperandValue()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("\"x\" || 7;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal("x", result.AsString());
    }

    [Fact]
    public void LogicalAndShortCircuitsRightHandSide()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 0; function side(){ x = x + 1; return 1; } 0 && side(); x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(0, result.AsNumber());
    }

    [Fact]
    public void LogicalOrShortCircuitsRightHandSide()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 0; function side(){ x = x + 1; return 1; } 1 || side(); x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(0, result.AsNumber());
    }

    [Fact]
    public void NullishCoalescingReturnsRightForNullishLeft()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("null ?? 5;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(5, result.AsNumber());
    }

    [Fact]
    public void NullishCoalescingKeepsNonNullishLeft()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("0 ?? 5;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(0, result.AsNumber());
    }

    [Fact]
    public void NullishCoalescingShortCircuitsRightHandSide()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 0; function side(){ x = x + 1; return 1; } 0 ?? side(); x;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(0, result.AsNumber());
    }

    [Fact]
    public void InterpreterInvokesWriteBarrierForObjectStores()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; let child = {}; o.ref = child; o[1] = child; o.p = 1;"));
        new BytecodeVerifier().Verify(fn);

        var heap = new JsHeap();
        var interpreter = new BytecodeInterpreter(heap);
        _ = interpreter.Execute(fn);

        Assert.Equal(2, heap.WriteBarrierCount);
    }
}
