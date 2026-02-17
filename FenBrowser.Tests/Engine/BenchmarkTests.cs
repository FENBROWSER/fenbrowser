
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
    }
}
