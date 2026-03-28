using System;
using System.IO;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Errors;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class ProductionHardeningBatch2Tests
    {
        [Fact]
        public void ModuleLoader_BareSpecifier_WithoutImportMap_ThrowsTypeError()
        {
            var loader = new ModuleLoader(new FenEnvironment(), new FenBrowser.FenEngine.Core.ExecutionContext());

            Assert.Throws<FenTypeError>(() => loader.Resolve("react", "https://app.example.com/main.js"));
        }

        [Fact]
        public void ModuleLoader_NodeModules_AreDisabled_ByDefault()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "fen-node-modules-" + Guid.NewGuid().ToString("N"));
            var appDir = Path.Combine(workspace, "app");
            var nodeModulesDir = Path.Combine(workspace, "node_modules", "react");

            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(nodeModulesDir);
            File.WriteAllText(Path.Combine(nodeModulesDir, "index.js"), "export const ok = true;");

            try
            {
                var loader = new ModuleLoader(new FenEnvironment(), new FenBrowser.FenEngine.Core.ExecutionContext());
                var referrer = Path.Combine(appDir, "main.js");

                Assert.Throws<FenTypeError>(() => loader.Resolve("react", referrer));
            }
            finally
            {
                Directory.Delete(workspace, recursive: true);
            }
        }

        [Fact]
        public void FenEnvironment_DoesNotTreat_DocumentElementId_AsBinding()
        {
            var env = new FenEnvironment();
            var element = new FenObject();
            element.Set("id", FenValue.FromString("hero"));

            var document = new FenObject();
            document.Set("getElementById", FenValue.FromFunction(new FenFunction("getElementById", (args, thisVal) =>
            {
                if (args.Length > 0 && string.Equals(args[0].AsString(), "hero", StringComparison.Ordinal))
                {
                    return FenValue.FromObject(element);
                }

                return FenValue.Null;
            })));

            env.Set("document", FenValue.FromObject(document));

            Assert.False(env.HasBinding("hero"));
            Assert.True(env.Get("hero").IsUndefined);
        }

        [Fact]
        public void Window_NamedProperty_StillResolves_At_Window_Object_Layer()
        {
            var env = new FenEnvironment();
            var context = new FenBrowser.FenEngine.Core.ExecutionContext { Environment = env };
            var element = new FenObject();
            element.Set("id", FenValue.FromString("hero"));

            var document = new FenObject();
            document.Set("getElementById", FenValue.FromFunction(new FenFunction("getElementById", (args, thisVal) =>
            {
                if (args.Length > 0 && string.Equals(args[0].AsString(), "hero", StringComparison.Ordinal))
                {
                    return FenValue.FromObject(element);
                }

                return FenValue.Null;
            })));

            var window = new FenObject();
            window.Set("__fen_window_named_access__", FenValue.FromBoolean(true));
            window.Set("document", FenValue.FromObject(document));

            env.Set("window", FenValue.FromObject(window));
            env.Set("globalThis", FenValue.FromObject(window));
            env.Set("self", FenValue.FromObject(window));
            env.Set("document", FenValue.FromObject(document));

            var resolved = window.Get("hero", context);

            Assert.Same(element, resolved.AsObject());
        }

        [Fact]
        public void FenRuntime_Source_Registers_Promise_Once()
        {
            var source = File.ReadAllText(GetFenRuntimeSourcePath());
            Assert.Equal(1, CountOccurrences(source, "SetGlobal(\"Promise\""));
        }

        [Fact]
        public void FenRuntime_Source_Registers_QueueMicrotask_Once()
        {
            var source = File.ReadAllText(GetFenRuntimeSourcePath());
            Assert.Equal(1, CountOccurrences(source, "SetGlobal(\"queueMicrotask\""));
        }

        [Fact]
        public void FenRuntime_Source_Registers_Intl_Once()
        {
            var source = File.ReadAllText(GetFenRuntimeSourcePath());
            Assert.Equal(1, CountOccurrences(source, "SetGlobal(\"Intl\""));
        }

        [Fact]
        public void FenRuntime_Rejects_InvalidArrowParameterExpression()
        {
            var parser = new Parser(new Lexer("(a + b) => 1;"), allowRecovery: false);
            _ = parser.ParseProgram();

            Assert.Contains(parser.Errors, error => error.Contains("Invalid parameter pattern", StringComparison.OrdinalIgnoreCase));

            var runtime = new FenRuntime();
            var result = (FenValue)runtime.ExecuteSimple("(a + b) => 1;");

            Assert.Equal(FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw, result.Type);
            Assert.Contains("Invalid parameter pattern", result.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FenRuntime_Rejects_InvalidFunctionParameterExpression()
        {
            var parser = new Parser(new Lexer("function f(a + b) { return a; }"), allowRecovery: false);
            _ = parser.ParseProgram();

            Assert.Contains(parser.Errors, error => error.Contains("expected next token to be RParen", StringComparison.OrdinalIgnoreCase));

            var runtime = new FenRuntime();
            var result = (FenValue)runtime.ExecuteSimple("function f(a + b) { return a; }");

            Assert.Equal(FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw, result.Type);
            Assert.Contains("expected next token to be RParen", result.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static string GetFenRuntimeSourcePath()
        {
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "FenBrowser.FenEngine",
                "Core",
                "FenRuntime.cs"));
        }

        private static int CountOccurrences(string input, string pattern)
        {
            int count = 0;
            int index = 0;

            while ((index = input.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += pattern.Length;
            }

            return count;
        }
    }
}
