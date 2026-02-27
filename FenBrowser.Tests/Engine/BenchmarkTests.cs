
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Tests.Engine
{
    public class BenchmarkTests
    {
        private List<string> _benchmarkLogs = new List<string>();

        private static bool TryReadScript(string fileName, out string script, out string resolvedPath)
        {
            script = string.Empty;
            resolvedPath = string.Empty;

            var candidates = new[]
            {
                Path.Combine(Environment.CurrentDirectory, fileName),
                Path.Combine(AppContext.BaseDirectory, fileName),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", fileName)),
                Path.Combine(@"C:\Users\udayk\Videos\FENBROWSER", fileName)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    resolvedPath = candidate;
                    script = File.ReadAllText(candidate);
                    return true;
                }
            }

            return false;
        }

        [Fact]
        public void RunLoopBenchmark()
        {
            var host = new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: msg => { Console.WriteLine(msg); File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\loop_perf.log", msg + "\n"); }
            );
            var engine = new JavaScriptEngine(host);
            if (!TryReadScript("loop_perf.js", out var script, out _))
            {
                // Benchmark fixtures are optional in CI/local branches.
                return;
            }
            try
            {
                var result = engine.Evaluate(script);
                File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\loop_perf.log", "Final Result: " + result + "\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\loop_perf.log", "EXCEPTION: " + ex.ToString() + "\n");
                throw;
            }
        }

        [Fact]
        public async Task RunRuntimeBenchmarks()
        {
            // Arrange
            var host = new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: msg => {
                    _benchmarkLogs.Add(msg);
                }
            );

            var engine = new JavaScriptEngine(host);

            if (!TryReadScript("benchmarks.js", out var script, out _))
            {
                // Benchmark fixtures are optional in CI/local branches.
                return;
            }

            // Act
            var result = engine.Evaluate(script);

            // Assert
            if (result != null && result.ToString().StartsWith("Error:"))
            {
                File.WriteAllText(@"C:\Users\udayk\Videos\FENBROWSER\benchmark_error.txt", result.ToString());
                throw new Exception($"JS Evaluation Error: {result}");
            }

            Assert.NotEmpty(_benchmarkLogs);
            
            // Write results to results_runtime.md
            string resultsPath = @"C:\Users\udayk\Videos\FENBROWSER\results_runtime.md";
            
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Runtime Performance Baseline Results");
            sb.AppendLine($"Date: {DateTime.Now}");
            if (result != null) sb.AppendLine($"Final Result: {result}");
            sb.AppendLine();
            sb.AppendLine("| Benchmark | Duration |");
            sb.AppendLine("|-----------|----------|");
            sb.AppendLine("| Raw Logs Count | " + _benchmarkLogs.Count + " |");

            bool foundBench = false;
            foreach (var log in _benchmarkLogs)
            {
                if (log.Contains("Completed:"))
                {
                    // Format: [BENCHMARK] Completed: Loop Math in 123ms
                    var parts = log.Split(new[] { "Completed:", "in", "ms" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        string name = parts[1].Trim();
                        string duration = parts[2].Trim() + "ms";
                        sb.AppendLine($"| {name} | {duration} |");
                        foundBench = true;
                    }
                }
            }

            if (!foundBench)
            {
                sb.AppendLine();
                sb.AppendLine("## Full Log Dump");
                foreach (var log in _benchmarkLogs) sb.AppendLine("- " + log);
            }

            File.WriteAllText(resultsPath, sb.ToString());
        }

        [Fact]
        public void CompareRuntimeAndBytecode()
        {
            if (!TryReadScript("compare_engines.js", out var script, out _))
            {
                // Benchmark fixtures are optional in CI/local branches.
                return;
            }

            var host = new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: msg => { }
            );
            var runtimeEngine = new JavaScriptEngine(host);

            // Warmup
            runtimeEngine.Evaluate(script);

            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            object resultRuntime = runtimeEngine.Evaluate(script);
            sw1.Stop();

            var lexer = new FenBrowser.FenEngine.Core.Lexer(script);
            var parser = new FenBrowser.FenEngine.Core.Parser(lexer, false);
            var ast = parser.ParseProgram();
            var compiler = new FenBrowser.FenEngine.Core.Bytecode.Compiler.BytecodeCompiler();
            var codeBlock = compiler.Compile(ast);
            var vm = new FenBrowser.FenEngine.Core.Bytecode.VM.VirtualMachine();

            // Warmup
            var env1 = new FenBrowser.FenEngine.Core.FenEnvironment();
            vm.Execute(codeBlock, env1);

            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var env2 = new FenBrowser.FenEngine.Core.FenEnvironment();
            var resultBytecode = vm.Execute(codeBlock, env2);
            sw2.Stop();

            var sb = new StringBuilder();
            sb.AppendLine("# Engines Comparison Result");
            sb.AppendLine($"Runtime Result: {resultRuntime}");
            sb.AppendLine($"Runtime Time: {sw1.ElapsedMilliseconds} ms");
            sb.AppendLine($"Bytecode Result: {resultBytecode.ToString()}");
            sb.AppendLine($"Bytecode Time: {sw2.ElapsedMilliseconds} ms");

            File.WriteAllText(@"C:\Users\udayk\Videos\FENBROWSER\engine_comparison_results.md", sb.ToString());
            Assert.NotNull(resultRuntime);
            Assert.Equal(resultRuntime?.ToString(), resultBytecode.ToString());
        }
    }
}
