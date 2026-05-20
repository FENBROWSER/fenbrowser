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
    public void ArrayLiteralUsesArrayPrototypeAndConstructorIdentity()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let arr = []; (arr instanceof Array) && (arr.constructor === Array);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ArrayConstructorCreatesArrayInstances()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let a = Array(1, 2); let b = new Array; (a instanceof Array) && (a.length == 2) && (b instanceof Array) && (b.length == 0);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ArrayPrototypePushAppendsElementsAndReturnsLength()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let a = [1, 2]; let len = a.push(3, 4); (len == 4) && (a.length == 4) && (a[3] == 4);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ArrayConstructorRejectsInvalidNumericLengthWithRangeError()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let ok = false; try { new Array(1.5); } catch (e) { ok = e instanceof RangeError; } ok;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
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
    public void CompilerAndInterpreterCaptureOuterVariableCell()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 4; function f(){ return x + 1; } x = 100; f();"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(101, result.AsNumber());
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
    public void FunctionClosuresWriteThroughCapturedVariables()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let touched = false; let f = function(){ touched = true; }; f(); touched;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
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
    public void MethodCallBindsThisForMultiArgCall()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = { x: 1, sum: function(a,b){ return this.x + a + b; } }; o.sum(2,3);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(6, result.AsNumber());
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
    public void InOperatorThrowsTypeErrorWhenRightSideIsNotObject()
    {
        var cases = new[]
        {
            "\"a\" in 1;",
            "\"length\" in \"abc\";",
            "\"a\" in true;",
            "\"a\" in null;",
            "\"a\" in undefined;"
        };

        var compiler = new BytecodeCompiler();
        foreach (var source in cases)
        {
            var fn = compiler.CompileScript(new SourceText(source));
            new BytecodeVerifier().Verify(fn);

            var heap = new JsHeap();
            var ex = Assert.Throws<JsThrownException>(() => new BytecodeInterpreter(heap).Execute(fn));
            Assert.Equal(JsValueTag.Object, ex.Value.Tag);

            var error = heap.GetObject(ex.Value.AsObjectHandle());
            Assert.True(error.TryGetProperty("name", h => heap.GetObject(h), out var name));
            Assert.Equal("TypeError", name.Value.AsString());
        }
    }

    [Fact]
    public void InOperatorThrowsCatchableTypeErrorForPrimitiveRightSide()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let ok = false; try { \"length\" in \"abc\"; } catch (e) { ok = e instanceof TypeError; } ok;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void InOperatorCoercesPrimitivePropertyKeysToSpecStrings()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; o.Infinity = 1; o.undefined = 1; o[\"null\"] = 1; (Infinity in o) && (undefined in o) && (null in o);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void GlobalInfinityAndNaNAreSeededWhenReferenced()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("(Infinity > 1) && (NaN != NaN);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void NumberIntrinsicExposesMaxValueForInOperator()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("\"MAX_VALUE\" in Number;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void NumberCallReturnsPrimitiveAndConstructorCreatesInstance()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let callIsInstance = Number(0) instanceof Number; let constructIsInstance = new Number instanceof Number; (!callIsInstance) && constructIsInstance;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void NumberCallConvertsBoxedStringAndNumberObjects()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("(Number(new String(\"10\")) == 10) && (Number(new Object(10)) == 10) && (Number(\"abc\") != Number(\"abc\")) && (Number(\"INFINITY\") != Number(\"INFINITY\")) && (Number(\"infinity\") != Number(\"infinity\"));"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void TypeErrorConstructAndCallCreateErrorSubclassInstances()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let a = new TypeError; let b = TypeError(\"failed\"); (a instanceof Error) && (a instanceof TypeError) && (b instanceof Error) && (b instanceof TypeError);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void BooleanCallReturnsPrimitiveAndConstructorCreatesInstance()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let callIsInstance = Boolean(false) instanceof Boolean; let constructIsInstance = new Boolean instanceof Boolean; (!callIsInstance) && constructIsInstance;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void BooleanPrototypeToStringAndValueOfUseBooleanData()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("(Boolean.prototype.toString() == \"false\") && ((new Boolean()).valueOf() == false) && ((new Boolean(true)).toString() == \"true\") && ((new Boolean(0)).valueOf() == false) && ((new Boolean(new Object())).valueOf() == true);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void NativeCallErrorsAreCatchableByTryCatch()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let ok = false; try { Boolean.prototype.valueOf.call(new String()); } catch (e) { ok = e instanceof TypeError; } ok;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void DateObjectsAreOrdinaryNonBooleanReceiversForBooleanPrototypeMethods()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let ok = false; let d = new Date(0); Object.defineProperty(d, \"toString\", { value: Boolean.prototype.toString }); try { d.toString(); } catch (e) { ok = e instanceof TypeError; } ok;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CompilerAndInterpreterHandleArrowFunctionArguments()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function call(fn){ return fn(); } call(() => { return 7; });"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(7, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleExpressionBodyArrowFunction()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let inc = x => x + 1; inc(4);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(5, result.AsNumber());
    }

    [Fact]
    public void StringCallReturnsPrimitiveAndConstructorCreatesInstance()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let callIsInstance = String(\"\") instanceof String; let constructIsInstance = new String instanceof String; (!callIsInstance) && constructIsInstance;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void StringCallFormatsNumbersWithEcmaSpelling()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("(String(-0) == \"0\") && (String(Number.NaN) == \"NaN\") && (String(.00000012345) == \"1.2345e-7\") && (String(1000000000000000000000) == \"1e+21\");"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void StringCallUsesArrayToStringAndOverrides()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let a = String(new Array(1, 2, 3)); let old = Array.prototype.toString; Array.prototype.toString = function(){ return \"__ARRAY__\"; }; let b = String(new Array); Array.prototype.toString = old; (a == \"1,2,3\") && (b == \"__ARRAY__\");"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void EvalWithoutArgumentsReturnsUndefined()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("String(eval()) == \"undefined\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void TopLevelThisUsesGlobalObjectForStringConversion()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("var toString = function(){ return \"__THIS__\"; }; String(this) == \"__THIS__\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
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
    public void ObjectLiteralPrototypeChainsToObjectIntrinsic()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("({}) instanceof Object;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectIntrinsicCanBeUsedThroughVariableAlias()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("var object = {}; var OBJECT = Object; object instanceof OBJECT;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectConstructorCreatesOrdinaryObjectsForNullishValues()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let a = Object(null); let b = new Object(undefined); (a instanceof Object) && (b instanceof Object) && (a.constructor === Object) && (b.constructor === Object);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectConstructorReturnsExistingObjectArgument()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = { a: 1 }; (Object(o) === o) && (new Object(o) === o);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectConstructorBoxesPrimitiveArguments()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("(Object(true) instanceof Boolean) && (Object(1) instanceof Number) && (Object(\"x\") instanceof String) && (Object(true).constructor === Boolean) && (Object(1).constructor === Number) && (Object(\"x\").constructor === String);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectConstructorBoxedPrimitivesParticipateInLooseEquality()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("(Object(true) == true) && (Object(1.1) == 1.1) && (Object(Infinity) == Infinity) && (Object(\"x\") == \"x\") && (Object(\"\") == \"\");"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectPrototypeProvidesConstructorAndStringMethods()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; (o.constructor === Object) && (o.toString() == \"[object Object]\") && (o.toLocaleString() == \"[object Object]\");"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectPrototypeIsPrototypeOfDetectsArrayPrototypeChain()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("Array.prototype.isPrototypeOf(new Array(0));"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectDefinePropertyDefinesDataValueOnObjects()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; Object.defineProperty(o, \"x\", { value: 7 }); o.x == 7;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void NativeBuiltinsExposeLengthMetadata()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("Object.defineProperty.length == 3 && Object.length == 1 && Object.prototype.hasOwnProperty.length == 1 && Object.prototype.hasOwnProperty.call.length == 1 && Array.prototype.push.length == 1;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectPrototypeHasOwnPropertyChecksOnlyOwnProperties()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; Object.defineProperty(o, \"x\", { value: 7 }); o.hasOwnProperty(\"x\") && (o.hasOwnProperty(\"toString\") == false) && Object.prototype.hasOwnProperty.call(o, \"x\");"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectPrototypeHasOwnPropertyConvertsUndefinedKey()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; Object.defineProperty(o, undefined, {}); o.hasOwnProperty(\"undefined\");"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectDefinePropertyUsesEcmaNumberStringificationForKeys()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let a = {}; let b = {}; Object.defineProperty(a, 100000000000000000000, {}); Object.defineProperty(b, 1e+21, {}); a.hasOwnProperty(\"100000000000000000000\") && b.hasOwnProperty(\"1e+21\");"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void StringConstructorUsesEcmaNumberStringification()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("String(100000000000000000000) == \"100000000000000000000\" && String(1e+21) == \"1e+21\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectDefinePropertyConvertsObjectKeysThroughToPrimitive()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let a = {}; let b = {}; let c = {}; let d = {}; Object.defineProperty(a, [1, 2], {}); Object.defineProperty(b, new String(\"Hello\"), {}); Object.defineProperty(c, new Boolean(false), {}); Object.defineProperty(d, { toString: function(){ return \"abc\"; } }, {}); a.hasOwnProperty(\"1,2\") && b.hasOwnProperty(\"Hello\") && c.hasOwnProperty(\"false\") && d.hasOwnProperty(\"abc\");"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ToPrimitiveCallbacksWriteThroughCapturedVariables()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let touched = false; let o = {}; let key = { toString: function(){ touched = true; return \"x\"; } }; Object.defineProperty(o, key, {}); touched && o.hasOwnProperty(\"x\");"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectDefinePropertyFallsBackToValueOfForObjectKeys()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; let key = { toString: function(){ return {}; }, valueOf: function(){ return \"v\"; } }; Object.defineProperty(o, key, {}); o.hasOwnProperty(\"v\");"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectDefinePropertyRejectsObjectKeysWithoutPrimitiveConversion()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let ok = false; let key = { toString: function(){ return {}; }, valueOf: function(){ return {}; } }; try { Object.defineProperty({}, key, {}); } catch (e) { ok = e instanceof TypeError; } ok;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectDefinePropertyRejectsMixedAccessorAndDataDescriptor()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; let ok = false; try { Object.defineProperty(o, \"x\", { get: function(){ return 1; }, value: 1 }); } catch (e) { ok = e instanceof TypeError; } ok && (o.hasOwnProperty(\"x\") == false);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectDefinePropertyRejectsNonCallableAccessorFields()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; let setOk = false; let getOk = false; try { Object.defineProperty(o, \"x\", { set: 42 }); } catch (e) { setOk = e instanceof TypeError; } try { Object.defineProperty(o, \"y\", { get: 42 }); } catch (e) { getOk = e instanceof TypeError; } setOk && getOk && (o.hasOwnProperty(\"x\") == false) && (o.hasOwnProperty(\"y\") == false);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectGetOwnPropertyDescriptorReportsDataDescriptor()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; Object.defineProperty(o, \"x\", { value: 7, writable: true, enumerable: false, configurable: null }); let d = Object.getOwnPropertyDescriptor(o, \"x\"); d.value == 7 && d.writable && (d.enumerable == false) && (d.configurable == false);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectPrototypePropertyIsEnumerableChecksOwnEnumerableFlag()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; Object.defineProperty(o, \"visible\", { value: 1, enumerable: true }); Object.defineProperty(o, \"hidden\", { value: 2, enumerable: false }); o.propertyIsEnumerable(\"visible\") && (o.propertyIsEnumerable(\"hidden\") == false) && (o.propertyIsEnumerable(\"toString\") == false);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ObjectPrototypeHasOwnPropertyRejectsNullishReceivers()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let ok = false; try { Object.prototype.hasOwnProperty.call(null, \"x\"); } catch (e) { ok = e instanceof TypeError; } ok;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void NativeFunctionCallInvokesObjectPrototypeToStringWithExplicitThis()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let a = new Array(0); Object.prototype.toString.call(a) == \"[object Array]\";"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ConstructedObjectsWithPrimitivePrototypeFallBackToObjectPrototype()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function C(){}; C.prototype = 1; let o = new C(); o instanceof Object;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void InstanceOfReturnsFalseForNonObjectLeft()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function C(){}; 1 instanceof C;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void InstanceOfThrowsCatchableTypeErrorWhenRightSideIsNotObject()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let ok = false; try { true instanceof true; } catch (e) { ok = e instanceof TypeError; } ok;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void InstanceOfThrowsCatchableTypeErrorWhenRightSideIsNotCallable()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let ok = false; try { 1 instanceof {}; } catch (e) { ok = e instanceof TypeError; } ok;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void InstanceOfThrowsCatchableTypeErrorWhenPrototypeIsNotObject()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("function C(){}; C.prototype = 1; let o = new C(); let ok = false; try { o instanceof C; } catch (e) { ok = e instanceof TypeError; } ok;"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
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
    public void CommaExpressionReturnsRightmostValueAfterEvaluatingLeftSide()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 0; let y = (x = 1, 2, 3); (x == 1) && (y == 3);"));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void InterpreterInvokesWriteBarrierForObjectStores()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = {}; let child = {}; o.ref = child; o[1] = child; o.p = 1;"));
        new BytecodeVerifier().Verify(fn);

        var heap = new JsHeap();
        var interpreter = new BytecodeInterpreter(heap);
        var warmup = compiler.CompileScript(new SourceText("Object;"));
        new BytecodeVerifier().Verify(warmup);
        _ = interpreter.Execute(warmup);
        var before = heap.WriteBarrierCount;

        _ = interpreter.Execute(fn);

        Assert.True(heap.WriteBarrierCount - before >= 2);
    }
}
