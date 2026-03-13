using System;
using System.Collections.Generic;
using System.IO;
using FenBrowser.FenEngine.Core;
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

            var exports = Assert.IsType<FenObject>(
                loader.LoadModuleSrc("export default function F() {} F.foo = 'ok';", "memory://default-function.mjs"));

            var defaultExport = exports.Get("default");
            Assert.True(defaultExport.IsFunction);
            Assert.Equal("ok", defaultExport.AsObject().Get("foo").AsString());
        }

        [Fact]
        public void LoadModuleSrc_ExportDefaultNamedAsyncGenerator_BindsLocalName_And_DefaultExport()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenExecutionContext());

            var exports = Assert.IsType<FenObject>(
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

            var exports = Assert.IsType<FenObject>(loader.LoadModule("https://example.test/a.js"));
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

            var exports = Assert.IsType<FenObject>(
                loader.LoadModuleSrc("var a = 0; var b = 1; export { a as \"0\", b as \"1\" };", "memory://indexed-exports.mjs"));

            var zero = exports.GetOwnPropertyDescriptor("0");
            var one = exports.GetOwnPropertyDescriptor("1");

            Assert.True(zero.HasValue);
            Assert.True(one.HasValue);
            Assert.False(exports.IsExtensible);
            Assert.False(zero.Value.Writable ?? true);
            Assert.False(zero.Value.Configurable ?? true);
            Assert.False(one.Value.Writable ?? true);
            Assert.False(one.Value.Configurable ?? true);
            Assert.Throws<FenTypeError>(() => exports.Set("0", FenValue.FromNumber(1), strict: true));
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
    }
}


