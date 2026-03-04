using System;
using System.Collections.Generic;
using System.IO;
using FenBrowser.FenEngine.Core;
using Xunit;
using FenExecutionContext = FenBrowser.FenEngine.Core.ExecutionContext;
using FenBrowser.FenEngine.Errors;

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
    }
}


