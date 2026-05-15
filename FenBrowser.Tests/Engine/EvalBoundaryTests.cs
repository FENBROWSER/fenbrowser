using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    // Spec: ECMA-262 §19.2.1.1 PerformEval — `eval(string)` is a *direct* eval
    // only when the call-site Identifier `eval` resolves to the global %eval%
    // intrinsic. The parser cannot tell at parse time whether `eval` has been
    // shadowed in an enclosing scope, so the runtime must verify and fall back
    // to indirect-call semantics (invoke the resolved function as if by any
    // other call) when shadowing is detected.
    [Collection("Engine Tests")]
    public class EvalBoundaryTests
    {
        [Fact]
        public void ShadowedEval_FunctionScope_DoesNotInvokeGlobalEval()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                function f() {
                    var eval = function(s) { return 'shadow:' + s; };
                    return eval('42');
                }
                var result = f();
            ");

            Assert.Equal("shadow:42", runtime.GetGlobal("result").ToString());
        }

        [Fact]
        public void ShadowedEval_NonStringArgument_PassesThrough()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var called = false;
                function f() {
                    var eval = function(x) { called = true; return x + 1; };
                    return eval(41);
                }
                var result = f();
            ");

            Assert.True(runtime.GetGlobal("called").ToBoolean(),
                "Shadowed eval must be called like any other function, not bypassed");
            Assert.Equal(42d, runtime.GetGlobal("result").ToNumber());
        }

        [Fact]
        public void ShadowedEval_BlockScope_DoesNotInvokeGlobalEval()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                function f() {
                    let eval = function(s) { return 'block-shadow:' + s; };
                    return eval('hello');
                }
                var result = f();
            ");

            Assert.Equal("block-shadow:hello", runtime.GetGlobal("result").ToString());
        }

        [Fact]
        public void GlobalEvalStillReachable_WhenNotShadowed()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var fnType = typeof eval;
            ");

            Assert.Equal("function", runtime.GetGlobal("fnType").ToString());
        }
    }
}
