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
        evaluator.RegisterSource("main", "import a from 'answer'; globalThis.result = a + 1;");
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
        evaluator.RegisterSource("main", "import { x } from 'mod'; globalThis.doubled = x * 2;");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("doubled", out var v));
        Assert.Equal(14d, v.AsNumber());
    }

    [Fact]
    public void RenamedNamedImportBindsToAlias()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export var x = 5;");
        evaluator.RegisterSource("main", "import { x as renamed } from 'mod'; globalThis.out = renamed;");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("out", out var v));
        Assert.Equal(5d, v.AsNumber());
    }

    [Fact]
    public void ExportFunctionDeclarationIsCallable()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export function double(x) { return x * 2; }");
        evaluator.RegisterSource("main", "import { double } from 'mod'; globalThis.r = double(21);");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("r", out var v));
        Assert.Equal(42d, v.AsNumber());
    }

    [Fact]
    public void NamespaceImportExposesAllNamedExports()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", "export var a = 1; export var b = 2;");
        evaluator.RegisterSource("main", "import * as ns from 'mod'; globalThis.sum = ns.a + ns.b;");
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
    public void SeparateModulesCanReuseTopLevelBindingNames()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("first", "let e = 1; export var value = e;");
        evaluator.RegisterSource("second", "var e = 2; export var value = e;");

        var first = evaluator.Evaluate("first");
        var second = evaluator.Evaluate("second");

        Assert.Equal(1d, first["value"].AsNumber());
        Assert.Equal(2d, second["value"].AsNumber());
        Assert.False(interpreter.TryReadGlobalValue("e", out _));
    }

    [Fact]
    public void ClassMethodObservesLaterModuleVarAssignment()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource(
            "main",
            """
            var callback;
            class Component {
                connected() {
                    return callback.call(this);
                }
            }
            callback = function () { return 42; };
            export default new Component().connected();
            """);

        var exports = evaluator.Evaluate("main");

        Assert.Equal(42d, exports["default"].AsNumber());
    }

    [Fact]
    public void ImportMetaUrlUsesTheModuleSpecifier()
    {
        var (_, evaluator) = Setup();
        const string moduleUrl = "https://example.test/assets/app.js";
        evaluator.RegisterSource(moduleUrl, "export var url = import.meta.url;");

        var exports = evaluator.Evaluate(moduleUrl);

        Assert.Equal(moduleUrl, exports["url"].AsString());
    }

    [Fact]
    public void RelativeImportUsesAnAbsoluteResolvedModuleUrl()
    {
        const string entryUrl = "https://example.test/assets/app.js";
        const string dependencyUrl = "https://example.test/assets/dependency.js";
        var interpreter = new BytecodeInterpreter();
        var evaluator = new ModuleEvaluator(
            interpreter,
            hostSourceResolver: specifier =>
                specifier == dependencyUrl
                    ? "export var url = import.meta.url;"
                    : null);
        evaluator.RegisterSource(
            entryUrl,
            "import { url } from './dependency.js'; export var dependencyUrl = url;");

        var exports = evaluator.Evaluate(entryUrl);

        Assert.Equal(dependencyUrl, exports["dependencyUrl"].AsString());
    }

    [Fact]
    public void DynamicImportUsesHostResolverAndReturnsChunkNamespace()
    {
        const string entryUrl = "https://example.test/static/client/runtime.js";
        const string chunkUrl = "https://example.test/static/client/1234.chunk.js";
        var interpreter = new BytecodeInterpreter();
        var fetched = new System.Collections.Generic.List<string>();
        var evaluator = new ModuleEvaluator(interpreter, hostSourceResolver: specifier =>
        {
            fetched.Add(specifier);
            return specifier == chunkUrl
                ? "export const __rspack_esm_ids = [1234]; export const __webpack_modules__ = { 5() { return 5; } };"
                : null;
        });
        evaluator.RegisterSource(
            entryUrl,
            """
            import("./1234.chunk.js").then(module => {
                globalThis.chunkLength = module.__rspack_esm_ids.length;
            });
            """);

        _ = evaluator.Evaluate(entryUrl);

        Assert.True(interpreter.TryReadGlobalValue("chunkLength", out var value));
        Assert.Equal(1d, value.AsNumber());
        Assert.Contains(chunkUrl, fetched);
    }

    [Fact]
    public void NamespaceImportExposesExportedConstArray()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource("dependency", "export const ids = [79575];");
        evaluator.RegisterSource(
            "entry",
            "import * as dependency from 'dependency'; export var length = dependency.ids.length;");

        var exports = evaluator.Evaluate("entry");

        Assert.Equal(1d, exports["length"].AsNumber());
    }

    [Fact]
    public void CyclicNamespaceImportReadsBindingAfterModuleInitialization()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource(
            "self",
            "export const ids = [85363]; import * as self from 'self'; export var length = self.ids.length;");

        var exports = evaluator.Evaluate("self");

        Assert.Equal(1d, exports["length"].AsNumber());
    }

    [Fact]
    public void ExportedFunctionPropertyReceivesNamespaceArgument()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource(
            "runtime",
            "function require() {} var exported = require; require.register = module => module.ids.length; export { exported as runtime };");
        evaluator.RegisterSource("foundation", "export const ids = [79575];");
        evaluator.RegisterSource(
            "entry",
            "import { runtime } from 'runtime'; import * as foundation from 'foundation'; export var length = runtime.register(foundation);");

        var exports = evaluator.Evaluate("entry");

        Assert.Equal(1d, exports["length"].AsNumber());
    }

    [Fact]
    public void RspackStyleRegistrarReadsNamespaceArray()
    {
        var (_, evaluator) = Setup();
        evaluator.RegisterSource(
            "runtime",
            "var register; function require() {} var exported = require; register = module => { var key, id, ids = module.ids, modules = module.modules, hook = module.hook, index = 0; for (key in modules) require[key] = modules[key]; for (hook && hook(require); index < ids.length; index++) id = ids[index]; }; require.C = register; export { exported as runtime };");
        evaluator.RegisterSource(
            "foundation",
            "export const ids = [79575]; export const modules = { 1(a) { return a; } };");
        evaluator.RegisterSource(
            "app",
            "export const ids = [77844]; export const modules = { 2(a) { return a; } };");
        evaluator.RegisterSource(
            "entry",
            "import { runtime } from 'runtime'; import * as foundation from 'foundation'; import * as app from 'app'; runtime.C(foundation); runtime.C(app); export var length = app.ids.length;");

        var exports = evaluator.Evaluate("entry");

        Assert.Equal(1d, exports["length"].AsNumber());
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
        evaluator.RegisterSource("main", "import { ns } from 'mid'; globalThis.aa = ns.a;");
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
        evaluator.RegisterSource("c", "import { x } from 'b'; globalThis.observed = x;");
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
            "import * as ns from 'mod'; globalThis.tag = Object.prototype.toString.call(ns);");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("tag", out var v));
        Assert.Equal("[object Module]", v.AsString());
    }

    // E.6.next - host source resolver delegate.

    [Fact]
    public void HostResolverIsConsultedOnSourceMiss()
    {
        var interpreter = new BytecodeInterpreter();
        var fetched = new System.Collections.Generic.List<string>();
        var evaluator = new ModuleEvaluator(interpreter, hostSourceResolver: spec =>
        {
            fetched.Add(spec);
            return spec switch
            {
                "lib" => "export default 5;",
                _ => null,
            };
        });
        evaluator.RegisterSource("main", "import x from 'lib'; globalThis.out = x;");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("out", out var v));
        Assert.Equal(5d, v.AsNumber());
        Assert.Contains("lib", fetched);
    }

    [Fact]
    public void HostResolverNullForUnknownThrows()
    {
        var interpreter = new BytecodeInterpreter();
        var evaluator = new ModuleEvaluator(interpreter, hostSourceResolver: _ => null);
        Assert.Throws<System.InvalidOperationException>(() => evaluator.Evaluate("missing"));
    }
}
