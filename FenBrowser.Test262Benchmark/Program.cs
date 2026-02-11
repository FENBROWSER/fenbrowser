using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Testing;

namespace FenBrowser.Test262Benchmark
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Test262 Benchmark Tool ===");
            
            // Hardcoded path based on user environment
            string test262Path = @"C:\Users\udayk\Videos\FENBROWSER\test262";
            if (!Directory.Exists(test262Path))
            {
                Console.WriteLine($"Error: Test262 path not found at {test262Path}");
                return;
            }

            // Results file
            string resultsFile = @"C:\Users\udayk\Videos\FENBROWSER\test262_results.md";

            // Initialize Runner
            var runner = new Test262Runner(test262Path, timeoutMs: 500);

            Console.WriteLine("Discovering tests...");
            var allFiles = runner.DiscoverTests(""); // Run all tests

            Console.WriteLine($"Found {allFiles.Count} tests.");
            
            int chunkSize = 1000;
            var chunks = allFiles.Chunk(chunkSize).ToList();
            
            if (args.Length > 0 && args[0] == "run")
            {
                string filePath = args[1];
                Console.WriteLine($"Running single test: {filePath}");
                var singleRunner = new Test262Runner(test262Path, timeoutMs: 5000); // 5s timeout for single run
                // We need to expose RunSingleTestAsync or use RunSpecificTestsAsync with 1 file
                // But RunSpecificTestsAsync expects files relative to test262 root or absolute?
                // Test262Runner.RunSingleTestAsync takes absolute path.
                // Let's rely on RunSpecificTestsAsync handling absolute paths if we pass them?
                // Actually Test262Runner.RunSpecificTestsAsync calls RunSingleTestAsync.
                
                var result = await singleRunner.RunSpecificTestsAsync(new[] { filePath });
                var r = result[0];
                Console.WriteLine($"Result: {(r.Passed ? "PASS" : "FAIL")}");
                if (!r.Passed) Console.WriteLine($"Error: {r.Error}");
                return;
            }

            if (args.Length > 0 && args[0] == "run_chunk")
            {
                int chunkIdx = int.Parse(args[1]) - 1;
                var chunkToRun = chunks[chunkIdx];
                Console.WriteLine($"=== Running Chunk {chunkIdx + 1} (Detailed) ===");
                
                var results = await runner.RunSpecificTestsAsync(chunkToRun);
                int passed = 0;
                int failed = 0;
                
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    if (r.Passed)
                    {
                        passed++;
                    }
                    else
                    {
                        failed++;
                        Console.WriteLine($"FAIL: {Path.GetFileName(chunkToRun[i])}");
                        Console.WriteLine($"  Error: {r.Error}");
                        Console.WriteLine("--------------------------------------------------");
                    }
                }
                
                Console.WriteLine($"Chunk {chunkIdx + 1} Complete. Pass: {passed}, Fail: {failed}");
                return;
            }

            if (args.Length > 0 && args[0] == "analyze")
            {
                int chunkIdx = int.Parse(args[1]) - 1;
                var chunkToAnalyze = chunks[chunkIdx];
                Console.WriteLine($"=== Analyzing Chunk {chunkIdx + 1} ===");
                Console.WriteLine($"Range: {chunkIdx * chunkSize + 1} - {chunkIdx * chunkSize + chunkToAnalyze.Length}");
                Console.WriteLine("First 5 files:");
                foreach(var f in chunkToAnalyze.Take(5)) Console.WriteLine(Path.GetRelativePath(test262Path, f));
                Console.WriteLine("Last 5 files:");
                foreach(var f in chunkToAnalyze.TakeLast(5)) Console.WriteLine(Path.GetRelativePath(test262Path, f));
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# Test262 Benchmark Results - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"Total Tests: {allFiles.Count}");
            sb.AppendLine($"Chunk Size: {chunkSize}");
            sb.AppendLine();
            sb.AppendLine("| Chunk | Range | Time (ms) | Tests | Passed | Failed | Pass % | Avg/Test (ms) |");
            sb.AppendLine("|-------|-------|-----------|-------|--------|--------|--------|---------------|");

            long totalMs = 0;
            int totalPassed = 0;
            int totalFailed = 0;
            
            var chunkStats = new List<(int ChunkId, string Range, long TimeMs, int Passed, int Failed, double PassRate)>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var startIndex = i * chunkSize + 1;
                var endIndex = startIndex + chunk.Length - 1;
                string range = $"{startIndex}-{endIndex}";
                
                Console.Write($"Running Chunk {i+1}/{chunks.Count} ({range})... ");
                
                var sw = Stopwatch.StartNew();
                
                // execute chunk
                var results = await runner.RunSpecificTestsAsync(chunk);

                sw.Stop();
                long ms = sw.ElapsedMilliseconds;
                totalMs += ms;
                
                int passed = results.Count(r => r.Passed);
                int failed = results.Count(r => !r.Passed);
                totalPassed += passed;
                totalFailed += failed;
                
                double avg = (double)ms / chunk.Length;
                double passRate = (double)passed / chunk.Length * 100.0;
                
                chunkStats.Add((i + 1, range, ms, passed, failed, passRate));

                Console.WriteLine($"{ms}ms | Pass: {passed} | Fail: {failed} ({passRate:F1}%)");
                sb.AppendLine($"| {i+1} | {range} | {ms} | {chunk.Length} | {passed} | {failed} | {passRate:F1}% | {avg:F2} |");
            }

            sb.AppendLine();
            sb.AppendLine($"**Total Time:** {TimeSpan.FromMilliseconds(totalMs)} ({totalMs}ms)");
            sb.AppendLine($"**Total Passed:** {totalPassed} ({((double)totalPassed / allFiles.Count * 100.0):F2}%)");
            sb.AppendLine($"**Total Failed:** {totalFailed} ({((double)totalFailed / allFiles.Count * 100.0):F2}%)");
            
            sb.AppendLine();
            sb.AppendLine("## Worst 5 Chunks (by Failure Count)");
            sb.AppendLine("| Chunk | Range | Failed | Pass % |");
            sb.AppendLine("|-------|-------|--------|--------|");
            
            foreach (var stat in chunkStats.OrderByDescending(s => s.Failed).Take(5))
            {
                sb.AppendLine($"| {stat.ChunkId} | {stat.Range} | {stat.Failed} | {stat.PassRate:F1}% |");
            }
            
            // Append to file
            await File.AppendAllTextAsync(resultsFile, "\n" + sb.ToString());
            Console.WriteLine($"Results saved to {resultsFile}");
        }
    }
}
