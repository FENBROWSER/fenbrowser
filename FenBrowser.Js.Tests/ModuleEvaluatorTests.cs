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

    // E.6.next - re-exports.

    [Fact]
    public void NamedReexportRepublishesValueUnderExportName()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource("src", "export var x = 1;");
        evaluator.RegisterSource("mid", "export { x } from 'src';");
        var exports = evaluator.Evaluate("mid");
        Assert.Equal(1d, exports["x"].AsNumber());
    }

    [Fact]
    public void RenamedReexportPublishesUnderAlias()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource("src", "export var x = 7;");
        evaluator.RegisterSource("mid", "export { x as y } from 'src';");
        var exports = evaluator.Evaluate("mid");
        Assert.False(exports.ContainsKey("x"));
        Assert.Equal(7d, exports["y"].AsNumber());
    }

    [Fact]
    public void StarReexportRepublishesAllNamedExportsButNotDefault()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource("src", "export var a = 1; export var b = 2; export default 99;");
        evaluator.RegisterSource("mid", "export * from 'src';");
        var exports = evaluator.Evaluate("mid");
        Assert.Equal(1d, exports["a"].AsNumber());
        Assert.Equal(2d, exports["b"].AsNumber());
        Assert.False(exports.ContainsKey("default"));
    }

    [Fact]
    public void NamespaceReexportPublishesNamespaceObjectUnderName()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("src", "export var a = 5;");
        evaluator.RegisterSource("mid", "export * as ns from 'src';");
        evaluator.RegisterSource("main", "import { ns } from 'mid'; var aa = ns.a;");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("aa", out var v));
        Assert.Equal(5d, v.AsNumber());
    }

    [Fact]
    public void TwoStepReexportPropagatesValue()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("a", "export var x = 10;");
        evaluator.RegisterSource("b", "export { x } from 'a';");
        evaluator.RegisterSource("c", "import { x } from 'b'; var observed = x;");
        _ = evaluator.Evaluate("c");
        Assert.True(interpreter.TryReadGlobalValue("observed", out var v));
        Assert.Equal(10d, v.AsNumber());
    }

    // E.6.next - exotic namespace object.

    [Fact]
    public void NamespaceObjectHasModuleToStringTag()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export var a = 1;");
        evaluator.RegisterSource("main",
            "import * as ns from 'mod'; var tag = Object.prototype.toString.call(ns);");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("tag", out var v));
        Assert.Equal("[object Module]", v.AsString());
    }
}
