using System;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
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
        public void ExecuteSimple_CompileUnsupported_UsesInterpreterFallback()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var inc = (x) => x + 1;");

            var incVal = rt.GetGlobal("inc");
            Assert.True(incVal.IsFunction);
            var incFn = incVal.AsFunction();
            Assert.NotNull(incFn);
            Assert.Null(incFn.BytecodeBlock);
        }

        [Fact]
        public void ExecuteSimple_WithInterpreterOnlyGlobals_CallHeavyScriptAvoidsVmPath()
        {
            var rt = CreateRuntime();

            // Arrow functions are currently interpreter fallback and yield AST-backed functions.
            rt.ExecuteSimple("var inc = (x) => x + 1;");
            rt.ExecuteSimple("var answer = inc(41);");

            Assert.Equal(42, ((FenValue)rt.GetGlobal("answer")).AsNumber());
        }
    }
}
