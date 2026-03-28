using System;
using System.Collections.Generic;
using System.IO;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Types;
using Xunit;
using FenExecutionContext = FenBrowser.FenEngine.Core.ExecutionContext;
using FenBrowser.FenEngine.Errors;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.Tests.Engine
{
    public class ModuleLoaderTests
    {
        [Fact]
        public void Resolve_Uses_Exact_ImportMap_Entry()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());
            loader.SetImportMap(
                new Dictionary<string, string>
                {
                    ["lit"] = "https://app.example.com/vendor/lit/index.js"
                },
                new Uri("https://app.example.com/main.js"));

            var resolved = loader.Resolve("lit", "https://app.example.com/main.js");
            Assert.Equal("https://app.example.com/vendor/lit/index.js", resolved);
        }

        [Fact]
        public void Resolve_Uses_Prefix_ImportMap_Entry()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());
            loader.SetImportMap(
                new Dictionary<string, string>
                {
                    ["app/"] = "/assets/app/"
                },
                new Uri("https://app.example.com/main.js"));

            var resolved = loader.Resolve("app/utils/math.js", "https://app.example.com/main.js");
            Assert.Equal("https://app.example.com/assets/app/utils/math.js", resolved);
        }

        [Fact]
        public void Resolve_Appends_Js_For_Http_Module_Without_Extension()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());

            var resolved = loader.Resolve("./modules/runtime", "https://app.example.com/main.js");
            Assert.Equal("https://app.example.com/modules/runtime.js", resolved);
        }

        [Fact]
        public void Resolve_Blocks_CrossOrigin_Http_Modules_ByDefault()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());

            Assert.Throws<UnauthorizedAccessException>(() =>
                loader.Resolve("https://cdn.example.com/mod.js", "https://app.example.com/main.js"));
        }

        [Fact]
        public void Resolve_Blocks_Unsafe_Module_Schemes()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());

            Assert.Throws<UnauthorizedAccessException>(() =>
                loader.Resolve("javascript:alert(1)", "https://app.example.com/main.js"));
        }

        [Fact]
        public void LoadModule_MissingFile_ThrowsInvalidOperationException()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());

            var ex = Assert.Throws<InvalidOperationException>(() =>
                loader.LoadModule(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mjs")));

            Assert.Contains("Failed to load module", ex.Message);
        }

        [Fact]
        public void LoadModuleSrc_ParseError_ThrowsFenSyntaxError()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());
            var badModuleSource = "export const = ;";

            var ex = Assert.Throws<FenSyntaxError>(() => loader.LoadModuleSrc(badModuleSource, "memory://bad-module.mjs"));

            Assert.Contains("Module parse error", ex.Message);
        }

        [Fact]
        public void LoadModuleSrc_ExportDefaultNamedFunction_BindsLocalName_And_DefaultExport()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());

            var exports = Assert.IsAssignableFrom<FenObject>(
                loader.LoadModuleSrc("export default function F() {} F.foo = 'ok';", "memory://default-function.mjs"));

            var defaultExport = exports.Get("default");
            Assert.True(defaultExport.IsFunction);
            Assert.Equal("ok", defaultExport.AsObject().Get("foo").AsString());
        }

        [Fact]
        public void LoadModuleSrc_ExportDefaultNamedAsyncGenerator_BindsLocalName_And_DefaultExport()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());

            var exports = Assert.IsAssignableFrom<FenObject>(
                loader.LoadModuleSrc("export default async function * AG() {} AG.foo = 'ok';", "memory://default-async-generator.mjs"));

            var defaultExport = exports.Get("default");
            Assert.True(defaultExport.IsFunction);
            Assert.Equal("ok", defaultExport.AsObject().Get("foo").AsString());
        }

        [Fact]
        public void LoadModule_SelfImport_Sees_Live_Reexported_String_Name_During_Evaluation()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/a.js"] = "import * as self from \"./a.js\"; export { Mercury as \"\\u263F\" } from \"./fixture.js\"; export default self[\"\\u263F\"];",
                ["https://example.test/fixture.js"] = "export function Mercury() {}"
            };

            var loader = new ModuleLoader(
                new FenEnvironment(),
                new FenExecutionContext(),
                uri => sources[uri.AbsoluteUri]);

            var exports = Assert.IsAssignableFrom<FenObject>(loader.LoadModule("https://example.test/a.js"));
            var defaultExport = exports.Get("default");
            var stringNamedExport = exports.Get("\u263F");

            Assert.True(defaultExport.IsFunction);
            Assert.True(stringNamedExport.IsFunction);
            Assert.Same(stringNamedExport.AsFunction(), defaultExport.AsFunction());
        }

        [Fact]
        public void LoadModuleSrc_ModuleNamespaceExports_Are_Frozen_After_Evaluation()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());

            var exports = Assert.IsAssignableFrom<FenObject>(
                loader.LoadModuleSrc("var a = 0; var b = 1; export { a as \"0\", b as \"1\" };", "memory://indexed-exports.mjs"));

            var zero = exports.GetOwnPropertyDescriptor("0");
            var one = exports.GetOwnPropertyDescriptor("1");

            Assert.True(zero.HasValue);
            Assert.True(one.HasValue);
            Assert.False(exports.IsExtensible);
            // Module namespace properties are accessor-based (live bindings) and non-configurable
            Assert.True(zero.Value.IsAccessor, "Export binding should be an accessor (live binding)");
            Assert.False(zero.Value.Configurable ?? true);
            Assert.True(one.Value.IsAccessor, "Export binding should be an accessor (live binding)");
            Assert.False(one.Value.Configurable ?? true);
            // Set throws TypeError because module namespaces are read-only
            Assert.Throws<FenTypeError>(() => exports.Set("0", FenValue.FromNumber(1)));
            Assert.False(exports.Delete("0"));
            Assert.True(exports.Delete("2"));
        }

        [Fact]
        public void LoadModule_ImportedNamespace_Strict_Assignment_To_Existing_Export_Throws()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/test.js"] = "import * as ns from \"./fixture.js\"; ns[0] = 1;",
                ["https://example.test/fixture.js"] = "var a = 0; var b = 1; export { a as \"0\", b as \"1\" };"
            };

            var loader = new ModuleLoader(
                new FenEnvironment(),
                new FenExecutionContext(),
                uri => sources[uri.AbsoluteUri]);

            Assert.ThrowsAny<Exception>(() => loader.LoadModule("https://example.test/test.js"));
        }

        [Fact]
        public void LoadModule_ImportedNamespace_Strict_Assignment_To_Missing_Export_Throws()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/test.js"] = "import * as ns from \"./fixture.js\"; ns[2] = 2;",
                ["https://example.test/fixture.js"] = "var a = 0; var b = 1; export { a as \"0\", b as \"1\" };"
            };

            var loader = new ModuleLoader(
                new FenEnvironment(),
                new FenExecutionContext(),
                uri => sources[uri.AbsoluteUri]);

            Assert.ThrowsAny<Exception>(() => loader.LoadModule("https://example.test/test.js"));
        }

        [Fact]
        public void LoadModule_ImportedNamespace_Strict_Delete_Existing_Export_Throws()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/test.js"] = "import * as ns from \"./fixture.js\"; delete ns[0];",
                ["https://example.test/fixture.js"] = "var a = 0; var b = 1; export { a as \"0\", b as \"1\" };"
            };

            var loader = new ModuleLoader(
                new FenEnvironment(),
                new FenExecutionContext(),
                uri => sources[uri.AbsoluteUri]);

            Assert.ThrowsAny<Exception>(() => loader.LoadModule("https://example.test/test.js"));
        }

        [Fact]
        public void LoadModule_ImportedNamespace_Assignment_Inside_Arrow_Throws()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/test.js"] = "import * as ns from \"./fixture.js\"; (() => { ns[0] = 1; })();",
                ["https://example.test/fixture.js"] = "var a = 0; var b = 1; export { a as \"0\", b as \"1\" };"
            };

            var loader = new ModuleLoader(
                new FenEnvironment(),
                new FenExecutionContext(),
                uri => sources[uri.AbsoluteUri]);

            Assert.ThrowsAny<Exception>(() => loader.LoadModule("https://example.test/test.js"));
        }

        [Fact]
        public void LoadModule_ImportedNamespace_Missing_Assignment_Inside_Arrow_Throws()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/test.js"] = "import * as ns from \"./fixture.js\"; (() => { ns[2] = 2; })();",
                ["https://example.test/fixture.js"] = "var a = 0; var b = 1; export { a as \"0\", b as \"1\" };"
            };

            var loader = new ModuleLoader(
                new FenEnvironment(),
                new FenExecutionContext(),
                uri => sources[uri.AbsoluteUri]);

            Assert.ThrowsAny<Exception>(() => loader.LoadModule("https://example.test/test.js"));
        }

        [Fact]
        public void LoadModule_ImportedNamespace_Delete_Inside_Arrow_Throws()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/test.js"] = "import * as ns from \"./fixture.js\"; (() => { delete ns[0]; })();",
                ["https://example.test/fixture.js"] = "var a = 0; var b = 1; export { a as \"0\", b as \"1\" };"
            };

            var loader = new ModuleLoader(
                new FenEnvironment(),
                new FenExecutionContext(),
                uri => sources[uri.AbsoluteUri]);

            Assert.ThrowsAny<Exception>(() => loader.LoadModule("https://example.test/test.js"));
        }

        [Fact]
        public void LoadModule_RuntimeBacked_ImportedNamespace_IndexedReads_And_ReflectOps_Work()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/test.js"] = @"
                    import * as ns from './fixture.js';
                    globalThis.readZero = ns[0];
                    globalThis.reflectReadOne = Reflect.get(ns, 1);
                    globalThis.readMissing = ns[2];
                    globalThis.hasZero = (0 in ns);
                    globalThis.reflectHasOne = Reflect.has(ns, 1);
                    globalThis.hasMissing = (2 in ns);
                    globalThis.reflectSetOne = Reflect.set(ns, 1, 1);
                    globalThis.reflectDeleteOne = Reflect.deleteProperty(ns, 1);
                ",
                ["https://example.test/fixture.js"] = "var a = 0; var b = 1; export { a as \"0\", b as \"1\" };"
            };

            var runtime = new FenRuntime();
            var loader = new ModuleLoader(
                runtime.GlobalEnv,
                runtime.Context,
                uri => sources[uri.AbsoluteUri]);

            runtime.SetModuleLoader(loader);
            loader.LoadModule("https://example.test/test.js");

            var window = ((FenValue)runtime.GetGlobal("window")).AsObject();

            Assert.Equal(0, window.Get("readZero").AsNumber());
            Assert.Equal(1, window.Get("reflectReadOne").AsNumber());
            Assert.True(window.Get("readMissing").IsUndefined);
            Assert.True(window.Get("hasZero").ToBoolean());
            Assert.True(window.Get("reflectHasOne").ToBoolean());
            Assert.False(window.Get("hasMissing").ToBoolean());
            Assert.False(window.Get("reflectSetOne").ToBoolean());
            Assert.False(window.Get("reflectDeleteOne").ToBoolean());
        }

        [Fact]
        public void LoadModule_RuntimeBacked_ImportedNamespace_Throws_Are_Catchable_As_TypeErrors()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/test.js"] = @"
                    import * as ns from './fixture.js';
                    function captureName(fn) {
                        try {
                            fn();
                            return 'no-throw';
                        } catch (e) {
                            globalThis.lastCaught = e;
                            globalThis.lastCaughtType = typeof e;
                            globalThis.lastCaughtString = String(e);
                            globalThis.lastCaughtName = e.name;
                            globalThis.lastCaughtMessage = e.message;
                            return e.name;
                        }
                    }

                    globalThis.assignExistingName = captureName(() => { ns[0] = 1; });
                    globalThis.assignMissingName = captureName(() => { ns[2] = 2; });
                    globalThis.deleteExistingName = captureName(() => { delete ns[0]; });
                ",
                ["https://example.test/fixture.js"] = "var a = 0; var b = 1; export { a as \"0\", b as \"1\" };"
            };

            var runtime = new FenRuntime();
            var loader = new ModuleLoader(
                runtime.GlobalEnv,
                runtime.Context,
                uri => sources[uri.AbsoluteUri]);

            runtime.SetModuleLoader(loader);
            loader.LoadModule("https://example.test/test.js");

            var window = ((FenValue)runtime.GetGlobal("window")).AsObject();
            var caughtType = window.Get("lastCaughtType").AsString();
            var caughtString = window.Get("lastCaughtString").AsString();
            var caughtName = window.Get("lastCaughtName").AsString();
            var caughtMessage = window.Get("lastCaughtMessage").AsString();

            Assert.Equal("object", caughtType);
            Assert.True(
                string.Equals(window.Get("assignExistingName").AsString(), "TypeError", StringComparison.Ordinal),
                $"type={caughtType}, name={caughtName}, message={caughtMessage}, string={caughtString}");
            Assert.Equal("TypeError", window.Get("assignMissingName").AsString());
            Assert.Equal("TypeError", window.Get("deleteExistingName").AsString());
        }

        [Fact]
        public void LoadModule_ExportStar_Reexports_Named_Exports_But_Not_Default()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/dep.js"] = "export const named = 1; export default 9;",
                ["https://example.test/root.js"] = "export * from './dep.js';"
            };

            var loader = new ModuleLoader(
                new FenEnvironment(),
                new FenExecutionContext(),
                uri => sources[uri.AbsoluteUri]);

            var exports = Assert.IsAssignableFrom<FenObject>(loader.LoadModule("https://example.test/root.js"));

            Assert.Equal(1, exports.Get("named").AsNumber());
            Assert.False(exports.GetOwnPropertyDescriptor("default").HasValue);
        }

        [Fact]
        public void LoadModule_ExportStar_DoesNotOverride_Explicit_Local_Export()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/dep.js"] = "export const value = 'dependency';",
                ["https://example.test/root.js"] = "export const value = 'local'; export * from './dep.js';"
            };

            var loader = new ModuleLoader(
                new FenEnvironment(),
                new FenExecutionContext(),
                uri => sources[uri.AbsoluteUri]);

            var exports = Assert.IsAssignableFrom<FenObject>(loader.LoadModule("https://example.test/root.js"));

            Assert.Equal("local", exports.Get("value").AsString());
        }

        [Fact]
        public void LoadModule_ExportStar_Conflicting_Reexports_Are_Not_Exposed()
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/a.js"] = "export const shared = 'a';",
                ["https://example.test/b.js"] = "export const shared = 'b';",
                ["https://example.test/root.js"] = "export * from './a.js'; export * from './b.js';"
            };

            var loader = new ModuleLoader(
                new FenEnvironment(),
                new FenExecutionContext(),
                uri => sources[uri.AbsoluteUri]);

            var exports = Assert.IsAssignableFrom<FenObject>(loader.LoadModule("https://example.test/root.js"));

            Assert.False(exports.GetOwnPropertyDescriptor("shared").HasValue);
            Assert.True(exports.Get("shared").IsUndefined);
        }

        [Fact]
        public void DynamicImport_Returns_RealPromise_Resolved_With_ModuleNamespace()
        {
            EventLoopCoordinator.ResetInstance();

            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/dep.js"] = "export const answer = 42;",
                ["https://example.test/main.js"] = "globalThis.dynamicImportPromise = import('./dep.js');"
            };

            var runtime = new FenRuntime();
            var loader = new ModuleLoader(
                runtime.GlobalEnv,
                runtime.Context,
                uri => sources[uri.AbsoluteUri]);

            runtime.SetModuleLoader(loader);
            loader.LoadModule("https://example.test/main.js");

            var window = ((FenValue)runtime.GetGlobal("window")).AsObject();
            var promise = Assert.IsType<JsPromise>(window.Get("dynamicImportPromise").AsObject());
            Assert.True(promise.IsFulfilled);
            Assert.Equal(42, promise.Result.AsObject().Get("answer").AsNumber());
        }

        [Fact]
        public void DynamicImport_ThenCallback_Resolves_RelativeTo_CurrentModulePath()
        {
            EventLoopCoordinator.ResetInstance();

            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/modules/dep.js"] = "export const value = 'loaded';",
                ["https://example.test/modules/main.js"] = @"
                    globalThis.dynamicImportOrder = [];
                    import('./dep').then(function(ns) {
                        globalThis.dynamicImportValue = ns.value;
                        globalThis.dynamicImportOrder.push('then');
                    });
                    globalThis.dynamicImportOrder.push('sync');
                "
            };

            var runtime = new FenRuntime();
            var loader = new ModuleLoader(
                runtime.GlobalEnv,
                runtime.Context,
                uri => sources[uri.AbsoluteUri]);

            runtime.SetModuleLoader(loader);
            loader.LoadModule("https://example.test/modules/main.js");
            EventLoopCoordinator.Instance.RunUntilEmpty();

            var window = ((FenValue)runtime.GetGlobal("window")).AsObject();
            Assert.Equal("loaded", window.Get("dynamicImportValue").AsString());

            var order = window.Get("dynamicImportOrder").AsObject();
            Assert.Equal("sync", order.Get("0").AsString());
            Assert.Equal("then", order.Get("1").AsString());
        }

        [Fact]
        public void DynamicImport_MissingModule_Returns_RejectedPromise_And_Catch_Observes_Failure()
        {
            EventLoopCoordinator.ResetInstance();

            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/main.js"] = @"
                    globalThis.dynamicImportPromise = import('./missing.js');
                    globalThis.dynamicImportCaught = 'pending';
                    globalThis.dynamicImportPromise.catch(function(err) {
                        globalThis.dynamicImportCaught = String(err);
                    });
                "
            };

            var runtime = new FenRuntime();
            var loader = new ModuleLoader(
                runtime.GlobalEnv,
                runtime.Context,
                uri => sources[uri.AbsoluteUri]);

            runtime.SetModuleLoader(loader);
            loader.LoadModule("https://example.test/main.js");
            EventLoopCoordinator.Instance.RunUntilEmpty();

            var window = ((FenValue)runtime.GetGlobal("window")).AsObject();
            var promise = Assert.IsType<JsPromise>(window.Get("dynamicImportPromise").AsObject());
            Assert.True(promise.IsRejected);
            Assert.Contains("Failed to load module", window.Get("dynamicImportCaught").AsString());
        }

        // ── Tranche 1C: Live bindings, namespace exotic object, cycle semantics ──

        [Fact]
        public void LoadModuleSrc_Returns_ModuleNamespaceObject()
        {
            // ECMA-262 §10.4.6: Module namespace is an exotic object
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());
            var exports = loader.LoadModuleSrc("export const x = 1;", "memory://ns-type.mjs");

            Assert.IsType<ModuleNamespaceObject>(exports);
        }

        [Fact]
        public void LoadModuleSrc_Namespace_Has_ToStringTag_Module()
        {
            // ECMA-262 §10.4.6: @@toStringTag = "Module"
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());
            var exports = Assert.IsAssignableFrom<FenObject>(
                loader.LoadModuleSrc("export const x = 1;", "memory://tag.mjs"));

            Assert.Equal("Module", exports.Get("Symbol(Symbol.toStringTag)").AsString());
        }

        [Fact]
        public void LoadModuleSrc_Namespace_Is_NonExtensible()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());
            var exports = Assert.IsAssignableFrom<FenObject>(
                loader.LoadModuleSrc("export const x = 1;", "memory://nonext.mjs"));

            Assert.False(exports.IsExtensible);
        }

        [Fact]
        public void LoadModuleSrc_Live_Binding_Reflects_Mutation_Via_Exported_Function()
        {
            // ECMA-262 §16.2.1.7.4: Live bindings — reading an export
            // after mutation in the source module environment must see the updated value.
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/counter.js"] = @"
                    export let count = 0;
                    export function increment() { count++; }
                ",
                ["https://example.test/main.js"] = @"
                    import { count, increment } from './counter.js';
                    globalThis.before = count;
                    increment();
                    globalThis.after = count;
                "
            };

            var runtime = new FenRuntime();
            var loader = new ModuleLoader(
                runtime.GlobalEnv,
                runtime.Context,
                uri => sources[uri.AbsoluteUri]);

            runtime.SetModuleLoader(loader);
            loader.LoadModule("https://example.test/main.js");

            var window = ((FenValue)runtime.GetGlobal("window")).AsObject();
            Assert.Equal(0, window.Get("before").AsNumber());
            Assert.Equal(1, window.Get("after").AsNumber());
        }

        [Fact]
        public void LoadModuleSrc_Live_Binding_Star_Reexport_Reflects_Mutation()
        {
            // Live binding through export * re-export chain
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/base.js"] = @"
                    export let value = 'initial';
                    export function mutate() { value = 'mutated'; }
                ",
                ["https://example.test/reexport.js"] = "export * from './base.js';",
                ["https://example.test/main.js"] = @"
                    import { value, mutate } from './reexport.js';
                    globalThis.before = value;
                    mutate();
                    globalThis.after = value;
                "
            };

            var runtime = new FenRuntime();
            var loader = new ModuleLoader(
                runtime.GlobalEnv,
                runtime.Context,
                uri => sources[uri.AbsoluteUri]);

            runtime.SetModuleLoader(loader);
            loader.LoadModule("https://example.test/main.js");

            var window = ((FenValue)runtime.GetGlobal("window")).AsObject();
            Assert.Equal("initial", window.Get("before").AsString());
            Assert.Equal("mutated", window.Get("after").AsString());
        }

        [Fact]
        public void LoadModule_Cyclic_Modules_Resolve_Without_Deadlock()
        {
            // ECMA-262 §16.2.1.5.2: Cyclic module graphs must not deadlock.
            // Module A imports from B, B imports from A.
            // A's export may be undefined during B's evaluation (not yet initialized),
            // but must not throw or hang.
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/a.js"] = @"
                    import { b } from './b.js';
                    export const a = 'A';
                    globalThis.seenBInA = b;
                ",
                ["https://example.test/b.js"] = @"
                    import { a } from './a.js';
                    export const b = 'B';
                    globalThis.seenAInB = a;
                "
            };

            var runtime = new FenRuntime();
            var loader = new ModuleLoader(
                runtime.GlobalEnv,
                runtime.Context,
                uri => sources[uri.AbsoluteUri]);

            runtime.SetModuleLoader(loader);
            // Should not throw or deadlock
            loader.LoadModule("https://example.test/a.js");

            var window = ((FenValue)runtime.GetGlobal("window")).AsObject();
            // B was evaluated first (dependency), so it saw A's export before it was initialized
            // A saw B's completed export
            Assert.Equal("B", window.Get("seenBInA").AsString());
        }

        [Fact]
        public void LoadModule_Namespace_Set_Throws_TypeError()
        {
            // ECMA-262 §10.4.6.8: [[Set]] on module namespace always throws
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());
            var exports = Assert.IsAssignableFrom<FenObject>(
                loader.LoadModuleSrc("export const x = 1;", "memory://set-throw.mjs"));

            Assert.Throws<FenTypeError>(() => exports.Set("x", FenValue.FromNumber(2)));
            Assert.Throws<FenTypeError>(() => exports.Set("newProp", FenValue.FromNumber(3)));
        }

        [Fact]
        public void LoadModule_Namespace_Export_Bindings_Are_Accessor_Based()
        {
            // Live bindings are implemented as accessor properties (getters), not data properties
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());
            var exports = Assert.IsAssignableFrom<FenObject>(
                loader.LoadModuleSrc("export const x = 42;", "memory://accessor.mjs"));

            var desc = exports.GetOwnPropertyDescriptor("x");
            Assert.True(desc.HasValue);
            Assert.True(desc.Value.IsAccessor, "Export should be an accessor property (live binding)");
            Assert.NotNull(desc.Value.Getter);
            Assert.Null(desc.Value.Setter); // Read-only: no setter
        }

        [Fact]
        public void LoadModule_Same_Module_Returns_Cached_Namespace()
        {
            // ECMA-262 §16.2.1.5.2: Per-realm module cache — same specifier returns same object
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://example.test/dep.js"] = "export const x = 1;",
                ["https://example.test/a.js"] = "import * as dep from './dep.js'; globalThis.nsA = dep;",
                ["https://example.test/b.js"] = "import * as dep from './dep.js'; globalThis.nsB = dep;"
            };

            var runtime = new FenRuntime();
            var loader = new ModuleLoader(
                runtime.GlobalEnv,
                runtime.Context,
                uri => sources[uri.AbsoluteUri]);

            runtime.SetModuleLoader(loader);
            loader.LoadModule("https://example.test/a.js");
            loader.LoadModule("https://example.test/b.js");

            var window = ((FenValue)runtime.GetGlobal("window")).AsObject();
            var nsA = window.Get("nsA").AsObject();
            var nsB = window.Get("nsB").AsObject();
            Assert.Same(nsA, nsB);
        }
    }
}


