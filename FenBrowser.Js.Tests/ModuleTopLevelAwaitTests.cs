using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Modules;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Modules compile as FunctionKind.Async because top-level await is legal in
/// one, so the module body needs an AsyncContext to suspend into. Execute set
/// one up but ExecuteWithEnvironment - the entry point ModuleEvaluator uses -
/// did not, so any top-level await on a pending promise threw
/// "Pending await is not supported in this execution context". x.com's
/// entry-client module died on exactly that.
/// </summary>
public sealed class ModuleTopLevelAwaitTests
{
    private static (BytecodeInterpreter, ModuleEvaluator) Setup()
    {
        var interpreter = new BytecodeInterpreter();
        var evaluator = new ModuleEvaluator(interpreter);
        return (interpreter, evaluator);
    }

    [Fact]
    public void TopLevelAwaitOfAlreadyResolvedPromise()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", "globalThis.out = await Promise.resolve(5);");
        _ = evaluator.Evaluate("mod");
        Assert.True(interpreter.TryReadGlobalValue("out", out var v));
        Assert.Equal(5d, v.AsNumber());
    }

    [Fact]
    public void TopLevelAwaitOfPendingPromiseResumesAfterSettlement()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource(
            "mod",
            "var settle;" +
            "var pending = new Promise(function (resolve) { settle = resolve; });" +
            "Promise.resolve().then(function () { settle(11); });" +
            "globalThis.out = await pending;");
        _ = evaluator.Evaluate("mod");
        Assert.True(interpreter.TryReadGlobalValue("out", out var v));
        Assert.Equal(11d, v.AsNumber());
    }

    [Fact]
    public void TopLevelAwaitOfNonPromiseValue()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", "globalThis.out = await 3;");
        _ = evaluator.Evaluate("mod");
        Assert.True(interpreter.TryReadGlobalValue("out", out var v));
        Assert.Equal(3d, v.AsNumber());
    }

    [Fact]
    public void SequentialTopLevelAwaitsRunInOrder()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource(
            "mod",
            "globalThis.log = '';" +
            "globalThis.log += await Promise.resolve('a');" +
            "globalThis.log += await Promise.resolve('b');" +
            "globalThis.log += 'c';");
        _ = evaluator.Evaluate("mod");
        Assert.True(interpreter.TryReadGlobalValue("log", out var v));
        Assert.Equal("abc", v.AsString());
    }

    [Fact]
    public void TopLevelAwaitInsideAnAsyncFunctionStillWorks()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource(
            "mod",
            "async function run() { return await Promise.resolve(9); }" +
            "globalThis.out = await run();");
        _ = evaluator.Evaluate("mod");
        Assert.True(interpreter.TryReadGlobalValue("out", out var v));
        Assert.Equal(9d, v.AsNumber());
    }

    // A module with no await at all must still evaluate synchronously and hand
    // back its exports, not a promise.
    [Fact]
    public void ModuleWithoutAwaitStillExportsSynchronously()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export default 42;");
        var exports = evaluator.Evaluate("mod");
        Assert.Equal(42d, exports["default"].AsNumber());
    }

    [Fact]
    public void ExportsAreVisibleAfterATopLevelAwait()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export var x = await Promise.resolve(6);");
        var exports = evaluator.Evaluate("mod");
        Assert.Equal(6d, exports["x"].AsNumber());
    }
}
