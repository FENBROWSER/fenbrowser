using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class ModuleLoaderTests
    {
        [Fact]
        public void Resolve_Uses_Exact_ImportMap_Entry()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new ExecutionContext());
            loader.SetImportMap(
                new Dictionary<string, string>
                {
                    ["lit"] = "https://cdn.example.com/lit/index.js"
                },
                new Uri("https://app.example.com/main.js"));

            var resolved = loader.Resolve("lit", "https://app.example.com/main.js");
            Assert.Equal("https://cdn.example.com/lit/index.js", resolved);
        }

        [Fact]
        public void Resolve_Uses_Prefix_ImportMap_Entry()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new ExecutionContext());
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
            var loader = new ModuleLoader(new FenEnvironment(), new ExecutionContext());

            var resolved = loader.Resolve("./modules/runtime", "https://app.example.com/main.js");
            Assert.Equal("https://app.example.com/modules/runtime.js", resolved);
        }

        [Fact]
        public void Resolve_Blocks_CrossOrigin_Http_Modules_ByDefault()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new ExecutionContext());

            Assert.Throws<UnauthorizedAccessException>(() =>
                loader.Resolve("https://cdn.example.com/mod.js", "https://app.example.com/main.js"));
        }

        [Fact]
        public void Resolve_Blocks_Unsafe_Module_Schemes()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new ExecutionContext());

            Assert.Throws<UnauthorizedAccessException>(() =>
                loader.Resolve("javascript:alert(1)", "https://app.example.com/main.js"));
        }
    }
}
