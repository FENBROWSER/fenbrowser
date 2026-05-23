using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Modules;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ModuleEvaluatorTests
{
    private static (BytecodeInterpreter, ModuleEvaluator) Setup()
    {
        var interpreter = new BytecodeInterpreter();
        var evaluator = new ModuleEvaluator(interpreter);
        return (interpreter, evaluator);
    }

    [Fact]
    public void ExportDefaultExpressionBecomesDefaultExport()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource("answer", "export default 42;");
        var exports = evaluator.Evaluate("answer");
        Assert.Equal(42d, exports["default"].AsNumber());
    }

    [Fact]
    public void DefaultImportReadsTheDefaultExport()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("answer", "export default 42;");
        evaluator.RegisterSource("main", "import a from 'answer'; var result = a + 1;");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("result", out var v));
        Assert.Equal(43d, v.AsNumber());
    }

    [Fact]
    public void NamedExportFromVarDeclaration()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export var x = 7, y = 8;");
        var exports = evaluator.Evaluate("mod");
        Assert.Equal(7d, exports["x"].AsNumber());
        Assert.Equal(8d, exports["y"].AsNumber());
    }

    [Fact]
    public void NamedImportReadsNamedExport()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export var x = 7;");
        evaluator.RegisterSource("main", "import { x } from 'mod'; var doubled = x * 2;");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("doubled", out var v));
        Assert.Equal(14d, v.AsNumber());
    }

    [Fact]
    public void RenamedNamedImportBindsToAlias()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export var x = 5;");
        evaluator.RegisterSource("main", "import { x as renamed } from 'mod'; var out = renamed;");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("out", out var v));
        Assert.Equal(5d, v.AsNumber());
    }

    [Fact]
    public void ExportFunctionDeclarationIsCallable()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export function double(x) { return x * 2; }");
        evaluator.RegisterSource("main", "import { double } from 'mod'; var r = double(21);");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("r", out var v));
        Assert.Equal(42d, v.AsNumber());
    }

    [Fact]
    public void NamespaceImportExposesAllNamedExports()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export var a = 1; export var b = 2;");
        evaluator.RegisterSource("main", "import * as ns from 'mod'; var sum = ns.a + ns.b;");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("sum", out var v));
        Assert.Equal(3d, v.AsNumber());
    }

    [Fact]
    public void EvaluatingTheSameModuleTwiceReturnsTheCachedExports()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource("once", "export default 99;");
        var first = evaluator.Evaluate("once");
        var second = evaluator.Evaluate("once");
        Assert.Same(first, second);
    }

    [Fact]
    public void MissingModuleSourceThrows()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource("a", "import x from 'b';");
        Assert.Throws<System.InvalidOperationException>(() => evaluator.Evaluate("a"));
    }
}
