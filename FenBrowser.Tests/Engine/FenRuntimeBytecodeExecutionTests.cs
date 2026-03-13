using System;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core.Bytecode;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Security;
using Xunit;
using System.Linq;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class FenRuntimeBytecodeExecutionTests : IDisposable
    {
        public FenRuntimeBytecodeExecutionTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        public void Dispose()
        {
        }

        private static FenRuntime CreateRuntime() => new FenRuntime();

        private static FenValue ExecuteAndReadGlobal(string script, string globalName)
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();

            var runtime = new FenRuntime();
            runtime.ExecuteSimple(script);
            return (FenValue)runtime.GetGlobal(globalName);
        }

        private static FenFunction CreateAstBackedFunction(string declarationSource, string expectedName)
        {
            var lexer = new Lexer(declarationSource);
            var parser = new Parser(lexer, false);
            var program = parser.ParseProgram();
            var declaration = Assert.IsType<FunctionDeclarationStatement>(program.Statements[0]);
            var function = new FenFunction(
                declaration.Function.Parameters,
                declaration.Function.Body,
                new FenEnvironment())
            {
                Name = declaration.Function.Name
            };

            Assert.Equal(expectedName, function.Name);
            var prototype = new FenObject();
            prototype.Set("constructor", FenValue.FromFunction(function));
            function.Prototype = prototype;
            function.Set("prototype", FenValue.FromObject(prototype));
            return function;
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_FunctionDeclarationProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function add(a, b) { return a + b; }");

            var addFnVal = rt.GetGlobal("add");
            Assert.True(addFnVal.IsFunction);
            var addFn = addFnVal.AsFunction();
            Assert.NotNull(addFn);
            Assert.NotNull(addFn.BytecodeBlock);

            rt.ExecuteSimple("var sum = add(2, 3);");
            Assert.Equal(5, ((FenValue)rt.GetGlobal("sum")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_ArrowFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var inc = (x) => x + 1;");

            var incVal = rt.GetGlobal("inc");
            Assert.True(incVal.IsFunction);
            var incFn = incVal.AsFunction();
            Assert.NotNull(incFn);
            Assert.NotNull(incFn.BytecodeBlock);

            rt.ExecuteSimple("var answer = inc(41);");
            Assert.Equal(42, ((FenValue)rt.GetGlobal("answer")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_ForOfConstClosure_CapturesIterationBinding()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var saved;
                for (const v of [true]) {
                    saved = () => v;
                }
                var captured = saved();
            ");

            Assert.True(((FenValue)rt.GetGlobal("captured")).ToBoolean());
        }

        [Fact]
        public void ExecuteSimple_ForOfConstClosure_CalledInsideIteration_CapturesIterationBinding()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var captured;
                for (const v of [true]) {
                    var saved = () => v;
                    captured = saved();
                }
            ");

            Assert.True(((FenValue)rt.GetGlobal("captured")).ToBoolean());
        }

        [Fact]
        public void ExecuteSimple_ForOfConstClosure_ReturnedFromHelper_CapturesIterationBinding()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function helper(func) {
                    return func();
                }

                var captured;
                for (const v of [true]) {
                    captured = helper(() => v);
                }
            ");

            Assert.True(((FenValue)rt.GetGlobal("captured")).ToBoolean());
        }

        [Fact]
        public void ExecuteSimple_ForOfConstClosure_ReturnedFromTwoArgHelper_CapturesIterationBinding()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function helper(func, message) {
                    return func();
                }

                var captured;
                for (const v of [true]) {
                    captured = helper(() => v, 'msg');
                }
            ");

            Assert.True(((FenValue)rt.GetGlobal("captured")).ToBoolean());
        }

        [Fact]
        public void ExecuteSimple_ForOfConstClosure_StoredInLocalWithinTwoArgHelper_CapturesIterationBinding()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function helper(func, message) {
                    var value = func();
                    return value;
                }

                var captured;
                for (const v of [true]) {
                    captured = helper(() => v, 'msg');
                }
            ");

            Assert.True(((FenValue)rt.GetGlobal("captured")).ToBoolean());
        }

        [Fact]
        public void ExecuteSimple_ArrayLiteral_Exposes_Length_And_Index_Access_In_Bytecode()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var values = ['A', 'z'];
                var valueCount = values.length;
                var firstValue = values[0];
                var secondValue = values[1];
            ");

            Assert.Equal(2, ((FenValue)rt.GetGlobal("valueCount")).AsNumber());
            Assert.Equal("A", ((FenValue)rt.GetGlobal("firstValue")).AsString());
            Assert.Equal("z", ((FenValue)rt.GetGlobal("secondValue")).AsString());
        }

        [Fact]
        public void ExecuteSimple_AsyncHelperLoopCapture_PreservesClosureAndMessage()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var probe = async function() {
                    function helper(func, message) {
                        globalThis.directCaptureValue = func();
                        return new Promise(function(resolve) {
                            globalThis.captureValue = func();
                            globalThis.captureMessage = message;
                            resolve();
                        });
                    }

                    for (const v of [true]) {
                        await helper(() => v, '@@asyncIterator = boolean');
                        return [
                            directCaptureValue,
                            typeof directCaptureValue,
                            String(directCaptureValue),
                            captureValue,
                            typeof captureValue,
                            String(captureValue),
                            captureMessage,
                            typeof captureMessage,
                            String(captureMessage)
                        ];
                    }
                };

                var probeOut = probe();
            ");

            var promiseValue = (FenValue)rt.GetGlobal("probeOut");
            var promise = Assert.IsType<JsPromise>(promiseValue.AsObject());
            Assert.True(promise.IsFulfilled);

            var result = Assert.IsAssignableFrom<FenObject>(promise.Result.AsObject());
            Assert.Equal("boolean", result.Get("1").AsString());
            Assert.Equal("true", result.Get("2").AsString());
            Assert.Equal("boolean", result.Get("4").AsString());
            Assert.Equal("true", result.Get("5").AsString());
            Assert.Equal("@@asyncIterator = boolean", result.Get("6").AsString());
            Assert.Equal("string", result.Get("7").AsString());
            Assert.Equal("@@asyncIterator = boolean", result.Get("8").AsString());
        }

        [Fact]
        public void ExecuteSimple_AsyncForOfHelperCall_PreservesCapturedLoopBinding()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var probe = async function() {
                    function helper(func, message) {
                        return [func(), typeof func(), message];
                    }

                    for (const v of [true]) {
                        return helper(() => v, '@@asyncIterator = boolean');
                    }
                };

                var probeOut = probe();
            ");

            var promiseValue = (FenValue)rt.GetGlobal("probeOut");
            var promise = Assert.IsType<JsPromise>(promiseValue.AsObject());
            Assert.True(promise.IsFulfilled);

            var result = Assert.IsAssignableFrom<FenObject>(promise.Result.AsObject());
            Assert.Equal("true", result.Get("0").AsString());
            Assert.Equal("boolean", result.Get("1").AsString());
            Assert.Equal("@@asyncIterator = boolean", result.Get("2").AsString());
        }

        [Fact]
        public void ExecuteSimple_AsyncForOfTwoArgHelperDirectReturn_PreservesCapturedLoopBinding()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var probe = async function() {
                    function helper(func, message) {
                        return func();
                    }

                    for (const v of [true]) {
                        return helper(() => v, '@@asyncIterator = boolean');
                    }
                };

                var probeOut = probe();
            ");

            var promiseValue = (FenValue)rt.GetGlobal("probeOut");
            var promise = Assert.IsType<JsPromise>(promiseValue.AsObject());
            Assert.True(promise.IsFulfilled);
            Assert.Equal("true", promise.Result.AsString());
        }

        [Fact]
        public void ExecuteSimple_AsyncForOfHelperCall_ArrowTemplateLoadsCapturedName()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var probe = async function() {
                    function helper(func, message) {
                        return [func(), typeof func(), message];
                    }

                    for (const v of [true]) {
                        return helper(() => v, '@@asyncIterator = boolean');
                    }
                };
            ");

            var probeFn = rt.GetGlobal("probe").AsFunction();
            Assert.NotNull(probeFn);
            Assert.NotNull(probeFn.BytecodeBlock);

            var nestedFunctions = probeFn.BytecodeBlock.Constants
                .Where(v => v.IsFunction)
                .Select(v => v.AsFunction())
                .Where(f => f?.BytecodeBlock != null)
                .ToList();

            var arrowTemplate = Assert.Single(nestedFunctions.Where(f => f.IsArrowFunction));
            Assert.Contains(FenValue.FromString("v"), arrowTemplate.BytecodeBlock.Constants);
            Assert.DoesNotContain((byte)OpCode.LoadLocal, arrowTemplate.BytecodeBlock.Instructions);
        }

        [Fact]
        public void ExecuteSimple_ForOfConstClosure_PassedThroughHelper_PreservesCapturedValue()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function helper(func, message) {
                    var value = func();
                    return {
                        result: value,
                        resultString: String(value),
                        functionType: typeof func,
                        resultType: typeof value,
                        message: message
                    };
                }

                var report;
                for (const v of [true]) {
                    report = helper(() => v, '@@asyncIterator = boolean');
                }

                directCaptureValue = report.result;
                directCaptureString = report.resultString;
                directCaptureType = report.functionType;
                directCaptureResultType = report.resultType;
                helperMessage = report.message;
            ");

            Assert.Equal("boolean", ((FenValue)rt.GetGlobal("directCaptureType")).AsString());
            Assert.Equal("boolean", ((FenValue)rt.GetGlobal("directCaptureResultType")).AsString());
            Assert.True(((FenValue)rt.GetGlobal("directCaptureValue")).ToBoolean());
            Assert.Equal("true", ((FenValue)rt.GetGlobal("directCaptureString")).AsString());
            Assert.Equal("@@asyncIterator = boolean", ((FenValue)rt.GetGlobal("helperMessage")).AsString());
        }

        [Fact]
        public void ExecuteSimple_ForOfConstClosure_AssignedBeforeHelper_PreservesCapturedValue()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function helper(func, message) {
                    return {
                        result: func(),
                        message: message
                    };
                }

                var saved;
                var report;
                for (const v of [true]) {
                    saved = () => v;
                    report = helper(saved, '@@asyncIterator = boolean');
                }

                directCaptureValue = report.result;
                helperMessage = report.message;
            ");

            Assert.True(((FenValue)rt.GetGlobal("directCaptureValue")).ToBoolean());
            Assert.Equal("@@asyncIterator = boolean", ((FenValue)rt.GetGlobal("helperMessage")).AsString());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_TemplateLiteralFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function fmt(x) { return `v=${x}`; }");

            var fmtVal = rt.GetGlobal("fmt");
            Assert.True(fmtVal.IsFunction);
            var fmtFn = fmtVal.AsFunction();
            Assert.NotNull(fmtFn);
            Assert.NotNull(fmtFn.BytecodeBlock);

            rt.ExecuteSimple("var rendered = fmt(7);");
            Assert.Equal("v=7", ((FenValue)rt.GetGlobal("rendered")).AsString());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_SwitchControlFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function ctrl(n) { var sum = 0; for (var i = 0; i < n; i = i + 1) { if (i == 2) { continue; } switch (i) { case 4: break; default: sum = sum + i; } } return sum; }");

            var ctrlVal = rt.GetGlobal("ctrl");
            Assert.True(ctrlVal.IsFunction);
            var ctrlFn = ctrlVal.AsFunction();
            Assert.NotNull(ctrlFn);
            Assert.NotNull(ctrlFn.BytecodeBlock);

            rt.ExecuteSimple("var outv = ctrl(5);");
            Assert.Equal(4, ((FenValue)rt.GetGlobal("outv")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_RegexLiteralFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function mk() { return /ab/i; }");

            var mkVal = rt.GetGlobal("mk");
            Assert.True(mkVal.IsFunction);
            var mkFn = mkVal.AsFunction();
            Assert.NotNull(mkFn);
            Assert.NotNull(mkFn.BytecodeBlock);

            rt.ExecuteSimple("var re = mk(); var rf = re.flags;");
            Assert.Equal("i", ((FenValue)rt.GetGlobal("rf")).AsString());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_SpreadFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function add(a, b, c) { return a + b + c; }");

            var addVal = rt.GetGlobal("add");
            Assert.True(addVal.IsFunction);
            var addFn = addVal.AsFunction();
            Assert.NotNull(addFn);
            Assert.NotNull(addFn.BytecodeBlock);

            rt.ExecuteSimple("var spreadOut = add(1, ...[2, 3]);");
            Assert.Equal(6, ((FenValue)rt.GetGlobal("spreadOut")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_LabeledControlFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function scan(n) { var hits = 0; outer: for (var i = 0; i < n; i = i + 1) { for (var j = 0; j < 3; j = j + 1) { if (j == 1) { continue outer; } if (i == 2) { break outer; } hits = hits + 1; } } return hits; }");

            var scanVal = rt.GetGlobal("scan");
            Assert.True(scanVal.IsFunction);
            var scanFn = scanVal.AsFunction();
            Assert.NotNull(scanFn);
            Assert.NotNull(scanFn.BytecodeBlock);

            rt.ExecuteSimple("var scanOut = scan(5);");
            Assert.Equal(2, ((FenValue)rt.GetGlobal("scanOut")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_NewTargetFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function C() { this.ok = (new.target === C); }");

            var cVal = rt.GetGlobal("C");
            Assert.True(cVal.IsFunction);
            var cFn = cVal.AsFunction();
            Assert.NotNull(cFn);
            Assert.NotNull(cFn.BytecodeBlock);
            Assert.Contains((byte)OpCode.LoadNewTarget, cFn.BytecodeBlock.Instructions);
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_ImportMetaFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function metaUrl() { return import.meta.url; }");

            var metaVal = rt.GetGlobal("metaUrl");
            Assert.True(metaVal.IsFunction);
            var metaFn = metaVal.AsFunction();
            Assert.NotNull(metaFn);
            Assert.NotNull(metaFn.BytecodeBlock);

            rt.ExecuteSimple("var metaOut = metaUrl();");
            Assert.Equal("file:///local/script.js", ((FenValue)rt.GetGlobal("metaOut")).AsString());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_IfExpressionFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function pick(flag) { return if (flag) { 'yes'; } else { 'no'; }; }");

            var pickVal = rt.GetGlobal("pick");
            Assert.True(pickVal.IsFunction);
            var pickFn = pickVal.AsFunction();
            Assert.NotNull(pickFn);
            Assert.NotNull(pickFn.BytecodeBlock);

            rt.ExecuteSimple("var pickOut = pick(false);");
            Assert.Equal("no", ((FenValue)rt.GetGlobal("pickOut")).AsString());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_AsyncFunctionExpressionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var af = async function(v) { return v + 2; };");

            var afVal = rt.GetGlobal("af");
            Assert.True(afVal.IsFunction);
            var afFn = afVal.AsFunction();
            Assert.NotNull(afFn);
            Assert.NotNull(afFn.BytecodeBlock);
            Assert.True(afFn.IsAsync);

            rt.ExecuteSimple("var afOut = af(5);");
            var outVal = (FenValue)rt.GetGlobal("afOut");
            Assert.True(outVal.IsObject);
            var promise = outVal.AsObject() as JsPromise;
            Assert.NotNull(promise);
            Assert.True(promise.IsFulfilled);
            Assert.Equal(7, promise.Result.AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_AsyncAwaitFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var afw = async function(v) { return await (v + 1); };");

            var afwVal = rt.GetGlobal("afw");
            Assert.True(afwVal.IsFunction);
            var afwFn = afwVal.AsFunction();
            Assert.NotNull(afwFn);
            Assert.NotNull(afwFn.BytecodeBlock);
            Assert.True(afwFn.IsAsync);
            Assert.Contains((byte)OpCode.Await, afwFn.BytecodeBlock.Instructions);

            rt.ExecuteSimple("var afwOut = afw(8);");
            var outVal = (FenValue)rt.GetGlobal("afwOut");
            Assert.True(outVal.IsObject);
            var promise = outVal.AsObject() as JsPromise;
            Assert.NotNull(promise);
            Assert.True(promise.IsFulfilled);
            Assert.Equal(9, promise.Result.AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_AsyncThrowFunctionProducesRejectedPromise()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var afe = async function() { throw 'x'; };");

            var afeVal = rt.GetGlobal("afe");
            Assert.True(afeVal.IsFunction);
            var afeFn = afeVal.AsFunction();
            Assert.NotNull(afeFn);
            Assert.NotNull(afeFn.BytecodeBlock);

            rt.ExecuteSimple("var afeOut = afe();");
            var outVal = (FenValue)rt.GetGlobal("afeOut");
            Assert.True(outVal.IsObject);
            var promise = outVal.AsObject() as JsPromise;
            Assert.NotNull(promise);
            Assert.True(promise.IsRejected);
            Assert.Equal("x", promise.Result.AsString());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_DefaultParameterFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function inc(x = 41) { return x + 1; }");

            var incVal = rt.GetGlobal("inc");
            Assert.True(incVal.IsFunction);
            var incFn = incVal.AsFunction();
            Assert.NotNull(incFn);
            Assert.NotNull(incFn.BytecodeBlock);

            rt.ExecuteSimple("var a = inc(); var b = inc(9);");
            Assert.Equal(42, ((FenValue)rt.GetGlobal("a")).AsNumber());
            Assert.Equal(10, ((FenValue)rt.GetGlobal("b")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_RestParameterFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function count(head, ...tail) { return head + tail.length; }");

            var countVal = rt.GetGlobal("count");
            Assert.True(countVal.IsFunction);
            var countFn = countVal.AsFunction();
            Assert.NotNull(countFn);
            Assert.NotNull(countFn.BytecodeBlock);

            rt.ExecuteSimple("var restOut = count(2, 9, 8);");
            Assert.Equal(4, ((FenValue)rt.GetGlobal("restOut")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_FunctionUsesLocalSlotOpcodes()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function calc(a, b) { var x = a + b; return x; }");

            var calcVal = rt.GetGlobal("calc");
            Assert.True(calcVal.IsFunction);
            var calcFn = calcVal.AsFunction();
            Assert.NotNull(calcFn);
            Assert.NotNull(calcFn.BytecodeBlock);
            Assert.Contains((byte)OpCode.LoadLocal, calcFn.BytecodeBlock.Instructions);
            Assert.Contains((byte)OpCode.StoreLocal, calcFn.BytecodeBlock.Instructions);

            rt.ExecuteSimple("var calcOut = calc(10, 5);");
            Assert.Equal(15, ((FenValue)rt.GetGlobal("calcOut")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_DestructuringParameterFunctionProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function pick({ x, y = 2 }) { return x + y; }");

            var pickVal = rt.GetGlobal("pick");
            Assert.True(pickVal.IsFunction);
            var pickFn = pickVal.AsFunction();
            Assert.NotNull(pickFn);
            Assert.NotNull(pickFn.BytecodeBlock);

            rt.ExecuteSimple("var d1 = pick({ x: 5 }); var d2 = pick({ x: 1, y: 3 });");
            Assert.Equal(7, ((FenValue)rt.GetGlobal("d1")).AsNumber());
            Assert.Equal(4, ((FenValue)rt.GetGlobal("d2")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_DestructuringParameterWithObjectRestProducesBytecodeFunction()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function keep({ value, ...rest }) { return value + rest.extra; }");

            var keepVal = rt.GetGlobal("keep");
            Assert.True(keepVal.IsFunction);
            var keepFn = keepVal.AsFunction();
            Assert.NotNull(keepFn);
            Assert.NotNull(keepFn.BytecodeBlock);

            rt.ExecuteSimple("var keepOut = keep({ value: 40, extra: 2 });");
            Assert.Equal(42, ((FenValue)rt.GetGlobal("keepOut")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_WithStatementInFunctionBody_ShouldCompileAndRun()
        {
            var rt = CreateRuntime();
            var compile = rt.ExecuteSimple("function keep(obj) { with (obj) { return x; } }");
            Assert.NotEqual(FenBrowser.FenEngine.Core.Interfaces.ValueType.Error, compile.Type);

            rt.ExecuteSimple("var outv = keep({ x: 11 });");
            Assert.Equal(11, ((FenValue)rt.GetGlobal("outv")).AsNumber());
        }


        [Fact]
        public void ExecuteSimple_FunctionConstructor_WithBodyProducesCallableBytecodeFunction()
        {
            var rt = CreateRuntime();
            var compile = rt.ExecuteSimple("var fn = new Function('obj', 'with (obj) { return x; }');");
            Assert.NotEqual(FenBrowser.FenEngine.Core.Interfaces.ValueType.Error, compile.Type);

            var fnVal = rt.GetGlobal("fn");
            Assert.NotEqual(FenBrowser.FenEngine.Core.Interfaces.ValueType.Error, fnVal.Type);
        }

        [Fact]
        public void ExecuteSimple_WithAstBackedGlobal_CallHeavyScriptFailsInBytecodeOnlyMode()
        {
            var rt = CreateRuntime();
            var inc = CreateAstBackedFunction("function inc(x) { return x + 1; }", "inc");
            rt.SetGlobal("inc", FenValue.FromFunction(inc));

            var compileResult = rt.ExecuteSimple("function run(v) { return inc(v); }");
            Assert.NotEqual(FenBrowser.FenEngine.Core.Interfaces.ValueType.Error, compileResult.Type);
            var runVal = rt.GetGlobal("run");
            Assert.True(runVal.IsFunction);
            Assert.NotNull(runVal.AsFunction().BytecodeBlock);

            var result = rt.ExecuteSimple("var answer = run(41);");
            Assert.Equal(FenBrowser.FenEngine.Core.Interfaces.ValueType.Error, result.Type);
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_ClassStatementProducesBytecodeConstructor()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("class C { static a = 4; v = 3; }");

            var ctorVal = rt.GetGlobal("C");
            Assert.True(ctorVal.IsFunction);
            var ctorFn = ctorVal.AsFunction();
            Assert.NotNull(ctorFn);
            Assert.NotNull(ctorFn.BytecodeBlock);

            rt.ExecuteSimple("var c = new C(); var classOut = c.v + C.a;");
            var cVal = (FenValue)rt.GetGlobal("c");
            Assert.True(cVal.IsObject);
            Assert.Equal(3, cVal.AsObject().Get("v").AsNumber());
            Assert.Equal(4, ((FenValue)rt.GetGlobal("C")).AsObject().Get("a").AsNumber());
            Assert.Equal(7, ((FenValue)rt.GetGlobal("classOut")).AsNumber());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_ClassComputedAccessorsInForHead_UseEvaluatedKeys()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var empty = {};
                var C, value, staticValue;

                for (C = class {
                    get ['x' in empty]() { return 'via get'; }
                    set ['x' in empty](param) { value = param; }
                    static get ['x' in empty]() { return 'via static get'; }
                    static set ['x' in empty](param) { staticValue = param; }
                }; ; ) {
                    value = C.prototype.false;
                    C.prototype.false = 'via set';
                    staticValue = C.false;
                    C.false = 'via static set';
                    break;
                }

                var classComputedAccessorOut = value + '|' + staticValue;
            ");

            Assert.Equal("via set|via static set", ((FenValue)rt.GetGlobal("classComputedAccessorOut")).AsString());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_ClassFieldDirectEval_AllowsNewTargetAndReturnsUndefined()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var executed = false;
                var C = class {
                    #x = eval('executed = true; new.target;');
                    read() { return this.#x; }
                };
                var c = new C();
                var evalFieldOut = executed + ':' + (c.read() === undefined);
            ");

            Assert.Equal("true:true", ((FenValue)rt.GetGlobal("evalFieldOut")).AsString());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_DerivedClassFieldDirectEval_AllowsSuperProperty()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var executed = false;
                var A = class { };
                A.prototype.x = 7;
                var C = class extends A {
                    #x = eval('executed = true; super.x;');
                };
                new C();
                var evalSuperOut = executed;
            ");

            Assert.True(((FenValue)rt.GetGlobal("evalSuperOut")).AsBoolean());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_NewTarget_IsAllowedInDefaultParameters()
        {
            var rt = CreateRuntime();
            rt.Context.Permissions.Grant(JsPermissions.Eval);
            rt.ExecuteSimple(@"
                var ctorOk = false;
                var callOk = false;
                function check(expected, actual = new.target) {
                    if (expected === undefined) {
                        callOk = (actual === undefined);
                    } else {
                        ctorOk = (actual === expected);
                    }
                }
                new check(check);
                check(undefined);
                var originalOk = ctorOk && callOk;

                ctorOk = false;
                callOk = false;
                var checkSource = check.toString();
                var evaldCheck = eval('(' + checkSource + ')');
                new evaldCheck(evaldCheck);
                evaldCheck(undefined);
                var evalOk = ctorOk && callOk;
                var newTargetDefaultsOut = originalOk && evalOk;
            ");

            Assert.True(((FenValue)rt.GetGlobal("newTargetDefaultsOut")).AsBoolean());
            var checkSource = ((FenValue)rt.GetGlobal("checkSource")).AsString();
            Assert.Contains("function check", checkSource);
            Assert.DoesNotContain("var ctorOk", checkSource);
            Assert.DoesNotContain("assert._isSameValue", checkSource);
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_NewTarget_IsAllowedInMethods()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var objectOk = false;
                var objectGetterOk = false;
                var objectSetterOk = false;
                var classMethodOk = false;
                var classGetterOk = false;
                var classSetterOk = false;
                var ctorOk = false;

                let ol = {
                    olTest(arg) { objectOk = (arg === 4) && (new.target === undefined); },
                    get ol() { objectGetterOk = (new.target === undefined); return 1; },
                    set ol(arg) { objectSetterOk = (arg === 4) && (new.target === undefined); }
                };

                class Cl {
                    constructor() { ctorOk = (new.target === Cl); }
                    clTest(arg) { classMethodOk = (arg === 4) && (new.target === undefined); }
                    get cl() { classGetterOk = (new.target === undefined); return 1; }
                    set cl(arg) { classSetterOk = (arg === 4) && (new.target === undefined); }
                }

                ol.olTest(4);
                ol.ol;
                ol.ol = 4;
                var clInst = new Cl();
                clInst.clTest(4);
                clInst.cl;
                clInst.cl = 4;

                var newTargetMethodsOut = objectOk && objectGetterOk && objectSetterOk && classMethodOk && classGetterOk && classSetterOk && ctorOk;
            ");

            Assert.True(((FenValue)rt.GetGlobal("newTargetMethodsOut")).AsBoolean());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_SuperProperty_WorksInObjectAndClassMethods()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var objectCallOk = false;
                var classCallOk = false;
                var chainTypeError = false;

                class ToStringTest {
                    constructor() { this.foo = 'rhinoceros'; }
                    test() { classCallOk = (super.toString() === super['toString']()) && (super.toString() === this.toString()); }
                }

                let toStrOL = {
                    test() { objectCallOk = (super.toString() === super['toString']()) && (super.toString() === this.toString()); },
                    access() { super.foo.bar; }
                };

                new ToStringTest().test();
                toStrOL.test();
                try { toStrOL.access(); } catch (e) { chainTypeError = true; }

                var superPropertyMethodsOut = objectCallOk && classCallOk && chainTypeError;
            ");

            Assert.True(((FenValue)rt.GetGlobal("superPropertyMethodsOut")).AsBoolean());
        }

        [Fact]
        public void ExecuteSimple_BytecodeFirst_PlainObjectsInheritObjectPrototype()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var plainObjectProtoOut =
                    Object.getPrototypeOf({}) === Object.prototype &&
                    typeof ({}).toString === 'function' &&
                    ({}).toString() === '[object Object]' &&
                    String({}) === '[object Object]';
            ");

            Assert.True(((FenValue)rt.GetGlobal("plainObjectProtoOut")).AsBoolean());
        }

    }
}


