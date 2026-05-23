using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Verifies that closures resolve outer-scope identifier references through the
// EnvironmentRecord chain (JsFunctionObject.OuterEnvironment). Parameters and
// `arguments` are live bindings on the FunctionEnvironmentRecord, so nested
// functions read them through OuterEnv walks.
public sealed class ClosureEnvChainTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ClosureReadsOuterParameterThroughEnvChain()
    {
        const string source = @"
            function outer(x) {
                function inner() { return x; }
                return inner();
            }
            outer(42);";
        Assert.Equal(42, RunNum(source));
    }

    [Fact]
    public void NestedClosureWalksTwoEnvLevels()
    {
        const string source = @"
            function a(x) {
                function b() {
                    function c() { return x; }
                    return c();
                }
                return b();
            }
            a(7);";
        Assert.Equal(7, RunNum(source));
    }

    [Fact]
    public void InnerWriteToOuterParameterIsVisibleToOuter()
    {
        const string source = @"
            function outer(x) {
                function inner() { x = x + 100; }
                inner();
                return x;
            }
            outer(5);";
        Assert.Equal(105, RunNum(source));
    }

    [Fact]
    public void ArrowFunctionCapturesOuterParameter()
    {
        const string source = @"
            function outer(name) {
                let f = () => name;
                return f();
            }
            outer('hello');";
        Assert.Equal("hello", RunStr(source));
    }

    [Fact]
    public void DistinctInvocationsOfOuterProduceIndependentClosureBindings()
    {
        // Two separate invocations of the outer function must produce two independent
        // FunctionEnvironmentRecord instances — the inner closure returned by the
        // first invocation must not see the second invocation's parameter value.
        const string source = @"
            function makeAdder(n) {
                return function (x) { return x + n; };
            }
            let add3 = makeAdder(3);
            let add10 = makeAdder(10);
            add3(100) + add10(100);";
        Assert.Equal(213, RunNum(source));
    }

    [Fact]
    public void InnerFunctionCanCallOuterPeerThroughEnvChain()
    {
        // A nested function referencing a sibling function-declaration that lives in
        // its outer scope must resolve the peer through the env chain.
        const string source = @"
            function outer() {
                function helper(v) { return v * 2; }
                function caller() { return helper(21); }
                return caller();
            }
            outer();";
        Assert.Equal(42, RunNum(source));
    }
}
