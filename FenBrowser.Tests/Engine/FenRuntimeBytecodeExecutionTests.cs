using System;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core.Bytecode;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Types;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class FenRuntimeBytecodeExecutionTests : IDisposable
    {
        private const string BytecodeEnvKey = "FEN_USE_CORE_BYTECODE";
        private readonly string _previousBytecodeEnv;

        public FenRuntimeBytecodeExecutionTests()
        {
            _previousBytecodeEnv = Environment.GetEnvironmentVariable(BytecodeEnvKey);
            Environment.SetEnvironmentVariable(BytecodeEnvKey, "1");
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(BytecodeEnvKey, _previousBytecodeEnv);
        }

        private static FenRuntime CreateRuntime() => new FenRuntime();

        private static FenValue ExecuteAndReadGlobal(string script, string globalName, bool bytecodeEnabled)
        {
            var previous = Environment.GetEnvironmentVariable(BytecodeEnvKey);
            try
            {
                Environment.SetEnvironmentVariable(BytecodeEnvKey, bytecodeEnabled ? "1" : "0");
                EngineContext.Reset();
                EventLoopCoordinator.ResetInstance();

                var runtime = new FenRuntime();
                runtime.ExecuteSimple(script);
                return (FenValue)runtime.GetGlobal(globalName);
            }
            finally
            {
                Environment.SetEnvironmentVariable(BytecodeEnvKey, previous);
            }
        }

        private static void AssertFenValueEquivalent(FenValue expected, FenValue actual, string script)
        {
            Assert.True(
                expected.Type == actual.Type,
                $"Script mismatch type:\n{script}\nExpected={expected.Type}, Actual={actual.Type}, ExpectedValue={expected}, ActualValue={actual}");

            switch (expected.Type)
            {
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.Number:
                {
                    var expectedNumber = expected.AsNumber();
                    var actualNumber = actual.AsNumber();
                    if (double.IsNaN(expectedNumber))
                    {
                        Assert.True(double.IsNaN(actualNumber), $"Script: {script}");
                    }
                    else
                    {
                        Assert.Equal(expectedNumber, actualNumber);
                    }
                    break;
                }
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.String:
                    Assert.Equal(expected.AsString(), actual.AsString());
                    break;
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.Boolean:
                    Assert.Equal(expected.ToBoolean(), actual.ToBoolean());
                    break;
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.Null:
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.Undefined:
                    break;
                default:
                    Assert.Equal(expected.ToString(), actual.ToString());
                    break;
            }
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
        public void ExecuteSimple_CompileUnsupported_UsesInterpreterFallback()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("function keep(x) { if (false) { break; } return x; }");

            var keepVal = rt.GetGlobal("keep");
            Assert.True(keepVal.IsFunction);
            var keepFn = keepVal.AsFunction();
            Assert.NotNull(keepFn);
            Assert.Null(keepFn.BytecodeBlock);
        }

        [Fact]
        public void ExecuteSimple_WithInterpreterOnlyGlobals_CallHeavyScriptUsesBytecodeMainline()
        {
            var rt = CreateRuntime();

            // Force interpreter fallback so this function remains AST-backed.
            rt.ExecuteSimple("function inc(x) { if (false) { break; } return x + 1; }");
            var incVal = rt.GetGlobal("inc");
            Assert.True(incVal.IsFunction);
            Assert.Null(incVal.AsFunction().BytecodeBlock);

            // Bytecode-compiled function should still be able to call AST-backed globals.
            rt.ExecuteSimple("function run(v) { return inc(v); }");
            var runVal = rt.GetGlobal("run");
            Assert.True(runVal.IsFunction);
            Assert.NotNull(runVal.AsFunction().BytecodeBlock);

            rt.ExecuteSimple("var answer = run(41);");
            Assert.Equal(42, ((FenValue)rt.GetGlobal("answer")).AsNumber());
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
        public void ExecuteSimple_BytecodeAndInterpreter_ShouldMatchOnEdgeCaseCorpus()
        {
            var corpus = new[]
            {
                "var out = delete (1 + 2);",
                "var x = 0; var out = (delete (x = 1)) && (x === 1);",
                "var out = (function({ value, ...rest }) { return value + rest.b; })({ value: 3, b: 4 });",
                "var out = (function(key, obj) { var t = 0; ({ [key]: t } = obj); return t; })('z', { z: 9 });",
                "var out = (function(){ var s = 0; for (const { v } of ({ a:{v:1}, b:{v:2}, c:{v:3} })) { s = s + v; } return s; })();",
                "var out = (function(){ var x = 0; try { throw { a: 5 }; } catch (e) { x = e.a; } return x; })();",
                "var out = (function(){ var hits = 0; outer: for (var i = 0; i < 3; i = i + 1) { for (var j = 0; j < 3; j = j + 1) { if (j === 1) continue outer; hits = hits + 1; } } return hits; })();"
            };

            foreach (var script in corpus)
            {
                var bytecodeResult = ExecuteAndReadGlobal(script, "out", bytecodeEnabled: true);
                var interpreterResult = ExecuteAndReadGlobal(script, "out", bytecodeEnabled: false);
                AssertFenValueEquivalent(interpreterResult, bytecodeResult, script);
            }
        }
    }
}
