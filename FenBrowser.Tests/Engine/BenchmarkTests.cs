
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
            string script = File.ReadAllText(@"C:\Users\udayk\Videos\FENBROWSER\loop_perf.js");
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
        public async Task RunInterpreterBenchmarks()
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
            
            string benchmarkPath = @"C:\Users\udayk\Videos\FENBROWSER\benchmarks.js";

            Assert.True(File.Exists(benchmarkPath), $"Benchmark script not found at {benchmarkPath}");
            string script = File.ReadAllText(benchmarkPath);

            // Act
            var result = engine.Evaluate(script);

            // Assert
            if (result != null && result.ToString().StartsWith("Error:"))
            {
                File.WriteAllText(@"C:\Users\udayk\Videos\FENBROWSER\benchmark_error.txt", result.ToString());
                throw new Exception($"JS Evaluation Error: {result}");
            }

            Assert.NotEmpty(_benchmarkLogs);
            
            // Write results to results_interpreter.md
            string resultsPath = @"C:\Users\udayk\Videos\FENBROWSER\results_interpreter.md";
            
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Interpreter Performance Baseline Results");
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
        public void CompareInterpreterAndBytecode()
        {
            string scriptPath = @"C:\Users\udayk\Videos\FENBROWSER\compare_engines.js";
            string script = File.ReadAllText(scriptPath);

            var host = new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: msg => { }
            );
            var interpreterEngine = new JavaScriptEngine(host);

            // Warmup
            interpreterEngine.Evaluate(script);

            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            object resultInterpreter = interpreterEngine.Evaluate(script);
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
            sb.AppendLine($"Interpreter Result: {resultInterpreter}");
            sb.AppendLine($"Interpreter Time: {sw1.ElapsedMilliseconds} ms");
            sb.AppendLine($"Bytecode Result: {resultBytecode.ToString()}");
            sb.AppendLine($"Bytecode Time: {sw2.ElapsedMilliseconds} ms");

            File.WriteAllText(@"C:\Users\udayk\Videos\FENBROWSER\engine_comparison_results.md", sb.ToString());
            Assert.True(true);
        }
    }
}
