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
            Console.WriteLine("HELLO WORLD - VERIFICATION");
            
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
            // 3-minute timeout per test — anything longer is a failure
            var runner = new Test262Runner(test262Path, timeoutMs: 5000);

            Console.WriteLine("Discovering tests...");
            var allFiles = runner.DiscoverTests("").ToList();

            Console.WriteLine($"Found {allFiles.Count} tests.");
            
            int chunkSize = 1000;
            var chunks = allFiles.Chunk(chunkSize).ToList();

            if (args.Length > 0 && args[0] == "get_chunk_count")
            {
                Console.WriteLine(chunks.Count);
                return;
            }

            if (args.Length > 0 && args[0] == "diag_verify")
            {
                // Test the EXACT combined script scenario
                var rt = new FenBrowser.FenEngine.Core.FenRuntime();
                rt.OnConsoleMessage = (msg) => Console.WriteLine($"[JS] {msg}");

                // Load actual harness files
                var hp = Path.Combine(test262Path, "harness");
                var assertContent = File.ReadAllText(Path.Combine(hp, "assert.js"));
                var staContent = File.ReadAllText(Path.Combine(hp, "sta.js"));
                var propContent = File.ReadAllText(Path.Combine(hp, "propertyHelper.js"));

                // Build the EXACT same combined script as the test runner
                var sb = new StringBuilder();
                sb.Append(assertContent);
                sb.Append("\n;\n");
                sb.Append(staContent);
                sb.Append("\n;\n");
                sb.Append(propContent);
                sb.Append("\n;\n");

                // Add some debug instrumentation BEFORE the test code
                sb.Append(@"
console.log('=== PRE-TEST DIAG ===');
console.log('typeof verifyProperty: ' + typeof verifyProperty);
console.log('typeof assert: ' + typeof assert);
console.log('typeof assert.sameValue: ' + typeof assert.sameValue);
console.log('typeof Test262Error: ' + typeof Test262Error);
console.log('typeof Date: ' + typeof Date);
console.log('typeof Date.prototype: ' + typeof Date.prototype);
console.log('typeof Date.prototype.getYear: ' + typeof Date.prototype.getYear);

// Test prototype behavior
function MyError(msg) { this.message = msg; }
console.log('BEFORE: typeof MyError.prototype = ' + typeof MyError.prototype);
console.log('BEFORE: MyError.prototype === undefined : ' + (MyError.prototype === undefined));

// Now assign toString
MyError.prototype.toString = function() { return 'MyError: ' + this.message; };
console.log('AFTER: typeof MyError.prototype = ' + typeof MyError.prototype);
console.log('AFTER: typeof MyError.prototype.toString = ' + typeof MyError.prototype.toString);

var err = new MyError('test message');
console.log('typeof err: ' + typeof err);
console.log('err.message: ' + err.message);
console.log('typeof err.toString: ' + typeof err.toString);
console.log('typeof err.constructor: ' + typeof err.constructor);

// Check internal prototype chain
console.log('err has own toString: ' + err.hasOwnProperty('toString'));
console.log('err has own message: ' + err.hasOwnProperty('message'));

// Direct call attempt
try { console.log('err.toString(): ' + err.toString()); } catch(x) { console.log('err.toString() THREW'); }
try { console.log('"" + err: ' + ('' + err)); } catch(x) { console.log('"" + err THREW'); }

// Test verifyProperty call
try {
    console.log('Calling verifyProperty...');
    verifyProperty(Date.prototype.getYear, 'length', {
        enumerable: false,
        writable: false,
        configurable: true,
        value: 0
    });
    console.log('verifyProperty PASSED');
} catch (e) {
    console.log('typeof e: ' + typeof e);
    console.log('e.message: ' + e.message);
    console.log('e.toString type: ' + typeof e.toString);
    try { console.log('e.toString(): ' + e.toString()); } catch(x) { console.log('e.toString() THREW: ' + x); }
    console.log('"" + e: ' + ('' + e));
}
");
                var fullScript = sb.ToString();
                Console.WriteLine($"Full script length: {fullScript.Length}");
                var result = rt.ExecuteSimple(fullScript);
                Console.WriteLine($"Result: Type={result.Type}, Val={result.ToString().Substring(0, Math.Min(300, result.ToString().Length))}");
                return;
            }

            if (args.Length > 0 && args[0] == "diag_args")
            {
                // Test arguments.length behavior
                var rt = new FenBrowser.FenEngine.Core.FenRuntime();
                var messages = new List<string>();
                rt.OnConsoleMessage = (msg) => { messages.Add(msg); Console.WriteLine($"[JS] {msg}"); };

                var code = @"
function test(a, b, c, d) {
    console.log('arguments.length = ' + arguments.length);
    console.log('typeof arguments = ' + typeof arguments);
    console.log('a = ' + a + ', b = ' + b + ', c = ' + c + ', d = ' + d);
    console.log('arguments.length > 2 = ' + (arguments.length > 2));
    console.log('arguments.length > 2 === true : ' + (arguments.length > 2 === true));
    var gt = arguments.length > 2;
    console.log('gt = ' + gt + ', typeof gt = ' + typeof gt);
    console.log('gt === true : ' + (gt === true));
    if (gt === true) {
        console.log('STRICT CHECK PASSED');
    } else {
        console.log('STRICT CHECK FAILED - gt value: ' + gt + ', type: ' + typeof gt);
    }
}
test(1, 2, 3, 4);
";
                var result = rt.ExecuteSimple(code);
                Console.WriteLine($"Result: Type={result.Type}, Val={result}");
                return;
            }

            if (args.Length > 0 && args[0] == "diag_template")
            {
                // Test template literal parsing and incremental harness parsing
                // Load actual harness files and parse them combined
                var harnessPath3 = Path.Combine(test262Path, "harness");
                var assertContent3 = File.ReadAllText(Path.Combine(harnessPath3, "assert.js"));
                var staContent3 = File.ReadAllText(Path.Combine(harnessPath3, "sta.js"));
                var propContent3 = File.ReadAllText(Path.Combine(harnessPath3, "propertyHelper.js"));
                // Step 1: assert.js alone
                {
                    var l1 = new FenBrowser.FenEngine.Core.Lexer(assertContent3);
                    var p1 = new FenBrowser.FenEngine.Core.Parser(l1);
                    var prog1 = p1.ParseProgram();
                    Console.WriteLine($"  assert.js alone: [{(p1.Errors.Count == 0 ? "OK" : $"FAIL({p1.Errors.Count})")}] stmts={prog1.Statements.Count}");
                    foreach (var e in p1.Errors.Take(3)) Console.WriteLine($"    -> {e}");
                }
                // Step 2: assert.js + sta.js
                {
                    var combined = assertContent3 + "\n;\n" + staContent3;
                    var l2 = new FenBrowser.FenEngine.Core.Lexer(combined);
                    var p2 = new FenBrowser.FenEngine.Core.Parser(l2);
                    var prog2 = p2.ParseProgram();
                    Console.WriteLine($"  assert+sta: [{(p2.Errors.Count == 0 ? "OK" : $"FAIL({p2.Errors.Count})")}] stmts={prog2.Statements.Count}");
                    foreach (var e in p2.Errors.Take(3)) Console.WriteLine($"    -> {e}");
                }
                // Step 3: assert.js + sta.js + propertyHelper.js
                {
                    var combined = assertContent3 + "\n;\n" + staContent3 + "\n;\n" + propContent3;
                    var l3 = new FenBrowser.FenEngine.Core.Lexer(combined);
                    var p3 = new FenBrowser.FenEngine.Core.Parser(l3);
                    var prog3 = p3.ParseProgram();
                    Console.WriteLine($"  assert+sta+propHelper: [{(p3.Errors.Count == 0 ? "OK" : $"FAIL({p3.Errors.Count})")}] stmts={prog3.Statements.Count}");
                    foreach (var e in p3.Errors.Take(3)) Console.WriteLine($"    -> {e}");
                }
                // Step 4: assert.js + sta.js + propertyHelper.js + test code
                {
                    var testCode = "verifyProperty(Date.prototype, \"getYear\", {\n  enumerable: false,\n  writable: true,\n  configurable: true\n});\n";
                    var combined = assertContent3 + "\n;\n" + staContent3 + "\n;\n" + propContent3 + "\n;\n" + testCode;
                    var l4 = new FenBrowser.FenEngine.Core.Lexer(combined);
                    var p4 = new FenBrowser.FenEngine.Core.Parser(l4);
                    var prog4 = p4.ParseProgram();
                    Console.WriteLine($"  full combo: [{(p4.Errors.Count == 0 ? "OK" : $"FAIL({p4.Errors.Count})")}] stmts={prog4.Statements.Count}");
                    foreach (var e in p4.Errors.Take(3)) Console.WriteLine($"    -> {e}");
                }
                // Step 5: runtime execution with assert.js + sta.js + propertyHelper.js
                {
                    var combined = assertContent3 + "\n;\n" + staContent3 + "\n;\n" + propContent3;
                    var rt2 = new FenBrowser.FenEngine.Core.FenRuntime();
                    var result2 = rt2.ExecuteSimple(combined);
                    Console.WriteLine($"  Runtime (assert+sta+propHelper): type={result2?.Type}, val={result2}");
                }
                // Step 6: Check statement count for simple test cases
                {
                    var tests6 = new[] {
                        ("1 func", "function a() { return 1; }"),
                        ("2 funcs", "function a() { return 1; }\nfunction b() { return 2; }"),
                        ("func+var", "function a() { return 1; }\nvar x = 1;"),
                        ("func+assign", "function a() { return 1; }\na.x = 42;"),
                        ("func+func+assign", "function a() { return 1; }\nfunction b() { return 2; }\na.x = 42;"),
                        ("assert-like", "function assert(m) { if (true) return; throw new Error(m); }\nassert._isSameValue = function(a, b) { return a === b; };"),
                    };
                    foreach (var (label, code) in tests6)
                    {
                        var lp = new FenBrowser.FenEngine.Core.Lexer(code);
                        var pp = new FenBrowser.FenEngine.Core.Parser(lp);
                        var prog = pp.ParseProgram();
                        var st = pp.Errors.Count == 0 ? "OK" : $"FAIL({pp.Errors.Count})";
                        var stmtTypes = string.Join(", ", prog.Statements.Select(s => s.GetType().Name.Replace("Statement", "")));
                        Console.WriteLine($"  {label}: [{st}] stmts={prog.Statements.Count} [{stmtTypes}]");
                        foreach (var e in pp.Errors.Take(2)) Console.WriteLine($"    -> {e}");
                    }
                }
                // Step 7: Test try/catch/if/else
                {
                    var tests7 = new[] {
                        ("try-catch simple", "try { x = 1; } catch (e) { y = 2; }"),
                        ("try-catch-finally", "try { x = 1; } catch (e) { y = 2; } finally { z = 3; }"),
                        ("try with if inside", "try { if (true) { return; } } catch (e) { }"),
                        ("if-else", "if (true) { x = 1; } else { y = 2; }"),
                        ("if-else-if", "if (true) { x = 1; } else if (false) { y = 2; } else { z = 3; }"),
                        ("assert.sameValue-like", @"
function test(actual, expected, message) {
  try {
    if (actual === expected) {
      return;
    }
  } catch (error) {
    throw new Error(message);
  }

  if (message === undefined) {
    message = '';
  } else {
    message += ' ';
  }

  throw new Error(message);
}
"),
                    };
                    foreach (var (label, code) in tests7)
                    {
                        var lp = new FenBrowser.FenEngine.Core.Lexer(code.Trim());
                        var pp = new FenBrowser.FenEngine.Core.Parser(lp);
                        var prog = pp.ParseProgram();
                        var st = pp.Errors.Count == 0 ? "OK" : $"FAIL({pp.Errors.Count})";
                        Console.WriteLine($"  {label}: [{st}] stmts={prog.Statements.Count}");
                        foreach (var e in pp.Errors.Take(3)) Console.WriteLine($"    -> {e}");
                    }
                }
                string[] repros = new string[0];
                for (int i = 0; i < repros.Length; i++)
                {
                    var l = new FenBrowser.FenEngine.Core.Lexer(repros[i]);
                    var p = new FenBrowser.FenEngine.Core.Parser(l);
                    var prog = p.ParseProgram();
                    var status = p.Errors.Count == 0 ? "OK" : $"FAIL({p.Errors.Count})";
                    Console.WriteLine($"  [{status}] stmts={prog.Statements.Count} | {repros[i]}");
                    foreach (var err in p.Errors.Take(2))
                        Console.WriteLine($"    -> {err}");
                }

                // Incremental test: parse assert.js in chunks to find where it breaks
                Console.WriteLine("\n--- Incremental assert.js parse test ---");
                var assertContent = File.ReadAllText(Path.Combine(test262Path, "harness", "assert.js"));
                var assertLines = assertContent.Split('\n');
                int[] checkpoints = { 20, 30, 50, 103, 115, 120, 125, 170 };
                foreach (var cp in checkpoints)
                {
                    var partial = string.Join('\n', assertLines.Take(Math.Min(cp, assertLines.Length)));
                    var lp = new FenBrowser.FenEngine.Core.Lexer(partial);
                    var pp = new FenBrowser.FenEngine.Core.Parser(lp);
                    var prog = pp.ParseProgram();
                    var st = pp.Errors.Count == 0 ? "OK" : $"FAIL({pp.Errors.Count})";
                    var stmtTypes = string.Join(", ", prog.Statements.Select(s => s.GetType().Name.Replace("Statement", "")));
                    Console.WriteLine($"  lines 1-{cp}: [{st}] stmts={prog.Statements.Count} [{stmtTypes}]");
                    foreach (var err in pp.Errors.Take(2))
                        Console.WriteLine($"    -> {err}");
                }
                // Bisect to find which part of the function body causes the issue
                Console.WriteLine("\n--- Bisect assert.js function body ---");
                string[] bisectTests = new[] {
                    "function assert() { }\nassert.x = 42;",
                    "function assert() { return; }\nassert.x = 42;",
                    "function assert() { if (true) { return; } }\nassert.x = 42;",
                    "function assert(m) { if (true) { return; } throw new Error(m); }\nassert.x = 42;",
                    "function assert(m) { if (true) { return; } if (false) { m = 1; } throw new Error(m); }\nassert.x = 42;",
                    "function assert(m) { if (true) { return; }\n\n  if (false) { m = 'hi' + assert._toString(m); }\n  throw new Error(m); }\nassert.x = 42;",
                    // The exact assert.js content (compact)
                    "function assert(mustBeTrue, message) { if (mustBeTrue === true) { return; } if (message === undefined) { message = 'Expected true but got ' + assert._toString(mustBeTrue); } throw new Test262Error(message); }\nassert._isSameValue = 42;",
                };
                for (int bi = 0; bi < bisectTests.Length; bi++)
                {
                    var bt = bisectTests[bi];
                    var bl = new FenBrowser.FenEngine.Core.Lexer(bt);
                    var bp = new FenBrowser.FenEngine.Core.Parser(bl);
                    var bprog = bp.ParseProgram();
                    var bst = bp.Errors.Count == 0 ? "OK" : $"FAIL({bp.Errors.Count})";
                    Console.WriteLine($"  [{bst}] stmts={bprog.Statements.Count} | test {bi+1}: {bt.Replace("\n", "\\n").Substring(0, Math.Min(100, bt.Length))}");
                    foreach (var err in bp.Errors.Take(1))
                        Console.WriteLine($"    -> {err}");
                }
                return;
            }

            if (args.Length > 0 && args[0] == "diag_harness")
            {
                // Parse the harness (assert.js + sta.js + optional extra includes) and report errors
                var harnessPath2 = Path.Combine(test262Path, "harness");
                var assertJs2 = File.ReadAllText(Path.Combine(harnessPath2, "assert.js"));
                var staJs2 = File.ReadAllText(Path.Combine(harnessPath2, "sta.js"));
                var combined = assertJs2 + "\n;\n" + staJs2 + "\n;\n";
                // Add extra harness files if specified
                for (int ai = 1; ai < args.Length; ai++)
                {
                    var extraPath = Path.Combine(harnessPath2, args[ai]);
                    if (File.Exists(extraPath))
                    {
                        combined += File.ReadAllText(extraPath) + "\n;\n";
                        Console.WriteLine($"Added harness include: {args[ai]}");
                    }
                }
                Console.WriteLine($"Harness length: {combined.Length} chars");

                var lexer = new FenBrowser.FenEngine.Core.Lexer(combined);
                var parser = new FenBrowser.FenEngine.Core.Parser(lexer);
                var program = parser.ParseProgram();
                Console.WriteLine($"Statements: {program.Statements.Count}");
                Console.WriteLine($"Parse errors: {parser.Errors.Count}");
                foreach (var err in parser.Errors.Take(10))
                    Console.WriteLine($"  ERR: {err}");

                if (parser.Errors.Count == 0)
                    Console.WriteLine("HARNESS PARSED OK!");

                // Also try lexing to find any Illegal tokens
                Console.WriteLine("\n--- Lexer scan ---");
                var lexer2 = new FenBrowser.FenEngine.Core.Lexer(combined);
                int illegal = 0;
                FenBrowser.FenEngine.Core.Token tok;
                while ((tok = lexer2.NextToken()).Type != FenBrowser.FenEngine.Core.TokenType.Eof)
                {
                    if (tok.Type == FenBrowser.FenEngine.Core.TokenType.Illegal)
                    {
                        illegal++;
                        if (illegal <= 5)
                            Console.WriteLine($"  ILLEGAL at line {tok.Line}: '{tok.Literal}'");
                    }
                }
                Console.WriteLine($"Total Illegal tokens: {illegal}");

                // Also try executing through the runtime
                Console.WriteLine("\n--- Runtime execution ---");
                var rt = new FenBrowser.FenEngine.Core.FenRuntime();
                var result = rt.ExecuteSimple(combined);
                Console.WriteLine($"Runtime result type: {result?.Type}");
                if (result != null && (result.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error || result.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw))
                    Console.WriteLine($"Runtime error: {result}");
                else
                    Console.WriteLine("Runtime execution OK!");
                return;
            }

            if (args.Length > 0 && args[0] == "run_single")
            {
                // Run a single test file through the Test262Runner
                string testPath = args[1];
                Console.WriteLine($"Running single test: {testPath}");
                var singleResult = await runner.RunSingleTestAsync(testPath);
                Console.WriteLine($"Passed: {singleResult.Passed}");
                Console.WriteLine($"Expected: {singleResult.Expected}");
                Console.WriteLine($"Actual: {singleResult.Actual}");
                Console.WriteLine($"Error: {singleResult.Error}");
                Console.WriteLine($"Duration: {singleResult.Duration.TotalMilliseconds}ms");
                return;
            }

            if (args.Length > 0 && args[0] == "run_chunk")
            {
                int chunkIdx = int.Parse(args[1]) - 1;
                string outputFile = (args.Length > 2) ? args[2] : resultsFile;
                if (chunkIdx < 0 || chunkIdx >= chunks.Count)
                {
                    Console.WriteLine($"Error: Chunk index {chunkIdx + 1} out of range (1-{chunks.Count})");
                    return;
                }

                var chunkToRun = chunks[chunkIdx];
                int startIndex = chunkIdx * chunkSize + 1;
                int endIndex = startIndex + chunkToRun.Length - 1;
                string range = $"{startIndex}-{endIndex}";

                Console.WriteLine($"=== Running Chunk {chunkIdx + 1}/{chunks.Count} ({range}) ===");
                
                var sw = Stopwatch.StartNew();
                try 
                {
                    var results = await runner.RunSpecificTestsAsync(chunkToRun);
                    sw.Stop();
                    
                    long ms = sw.ElapsedMilliseconds;
                    int passed = results.Count(r => r.Passed);
                    int failed = results.Count(r => !r.Passed);
                    double avg = (double)ms / chunkToRun.Length;
                    double passRate = (double)passed / chunkToRun.Length * 100.0;

                    Console.WriteLine($"{ms}ms | Pass: {passed} | Fail: {failed} ({passRate:F1}%)");
                    
                    // DEBUG: Categorize ALL failure errors
                    var failedTests = results.Where(r => !r.Passed).ToList();
                    if (failedTests.Any())
                    {
                        // Group by error pattern to find top failure categories
                        var errorGroups = failedTests
                            .GroupBy(f => {
                                var err = f.Error ?? f.Actual ?? "unknown";
                                // Normalize: extract first line or key pattern
                                if (err.Contains("no prefix parse function for")) return "PARSE: no prefix parse function";
                                if (err.Contains("Expected '}'")) return "PARSE: Expected '}'";
                                if (err.Contains("Expected ';'")) return "PARSE: Expected ';'";
                                if (err.Contains("Unexpected token")) return "PARSE: Unexpected token";
                                if (err.Contains("SyntaxError")) return "PARSE: SyntaxError (other)";
                                if (err.Contains("Timeout")) return "TIMEOUT";
                                if (err.Contains("not defined") || err.Contains("is not defined")) return "ReferenceError: not defined";
                                if (err.Contains("is not a function")) return "TypeError: not a function";
                                if (err.Contains("is not a constructor")) return "TypeError: not a constructor";
                                if (err.Contains("Cannot read propert")) return "TypeError: Cannot read property";
                                if (err.Contains("TypeError")) return "TypeError (other)";
                                if (err.Contains("RangeError")) return "RangeError";
                                if (err.Contains("Maximum call stack")) return "StackOverflow";
                                if (err.Contains("Test262Error")) return "Test262Error (assertion failed)";
                                if (err.Contains("expected to fail but succeeded")) return "Negative test: should have thrown";
                                if (err.Contains("assert")) return "Assertion failure";
                                var firstLine = err.Split('\n')[0];
                                if (firstLine.Length > 80) firstLine = firstLine.Substring(0, 80);
                                return firstLine;
                            })
                            .OrderByDescending(g => g.Count())
                            .Take(15);

                        Console.WriteLine("\n[DIAG] Failure Categories:");
                        foreach (var g in errorGroups)
                        {
                            Console.WriteLine($"  [{g.Count(),4}] {g.Key}");
                        }

                        // Also print 3 sample errors for the top category
                        var topGroup = failedTests
                            .GroupBy(f => f.Error ?? f.Actual ?? "unknown")
                            .OrderByDescending(g => g.Count())
                            .First();
                        Console.WriteLine($"\n[DIAG] Top error sample ({topGroup.Count()}x):");
                        foreach (var s in topGroup.Take(3))
                        {
                            Console.WriteLine($"  File: {Path.GetFileName(s.TestFile)}");
                            Console.WriteLine($"  Error: {(s.Error ?? "").Substring(0, Math.Min(200, (s.Error ?? "").Length))}");
                            Console.WriteLine($"  Actual: {(s.Actual ?? "").Substring(0, Math.Min(200, (s.Actual ?? "").Length))}");
                        }
                    }

                    // Append immediately
                    string line = $"| {chunkIdx + 1} | {range} | {ms} | {chunkToRun.Length} | {passed} | {failed} | {passRate:F1}% | {avg:F2} |";
                    await File.AppendAllTextAsync(outputFile, line + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR executing chunk {chunkIdx + 1}: {ex.Message}");
                    await File.AppendAllTextAsync(outputFile, $"| {chunkIdx + 1} | {range} | ERROR | {chunkToRun.Length} | 0 | 0 | 0% | 0 |" + Environment.NewLine);
                }
                return;
            }

            // Default: Run All (Sequential in one process)
            // Always append a new run header
            var headerIds = new StringBuilder();
            headerIds.AppendLine();
            headerIds.AppendLine($"# Test262 Benchmark Results - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            headerIds.AppendLine();
            headerIds.AppendLine($"Total Tests: {allFiles.Count}");
            headerIds.AppendLine($"Chunk Size: {chunkSize}");
            headerIds.AppendLine();
            headerIds.AppendLine("| Chunk | Range | Time (ms) | Tests | Passed | Failed | Pass % | Avg/Test (ms) |");
            headerIds.AppendLine("|-------|-------|-----------|-------|--------|--------|--------|---------------|");
            
            await File.AppendAllTextAsync(resultsFile, headerIds.ToString());
            
            Console.WriteLine($"Results will be appended to {resultsFile}");

            long totalMs = 0;
            int totalPassed = 0;
            int totalFailed = 0;
            
            var chunkStats = new List<(int ChunkId, string Range, long TimeMs, int Passed, int Failed, double PassRate)>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var startIndex = i * chunkSize + 1;
                var endIndex = startIndex + chunk.Length - 1; 
                if (startIndex + chunk.Length > allFiles.Count) 
                {
                   endIndex = allFiles.Count;
                }
                endIndex = startIndex + chunk.Length - 1;

                string range = $"{startIndex}-{endIndex}";
                
                Console.Write($"Running Chunk {i+1}/{chunks.Count} ({range})... ");
                
                var sw = Stopwatch.StartNew();
                
                // execute chunk
                try 
                {
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
                    
                    // Append immediately
                    string line = $"| {i+1} | {range} | {ms} | {chunk.Length} | {passed} | {failed} | {passRate:F1}% | {avg:F2} |";
                    await File.AppendAllTextAsync(resultsFile, line + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR executing chunk {i+1}: {ex.Message}");
                    await File.AppendAllTextAsync(resultsFile, $"| {i+1} | {range} | ERROR | {chunk.Length} | 0 | 0 | 0% | 0 |" + Environment.NewLine);
                }
            }

            var footer = new StringBuilder();
            footer.AppendLine();
            footer.AppendLine($"**Total Time:** {TimeSpan.FromMilliseconds(totalMs)} ({totalMs}ms)");
            footer.AppendLine($"**Total Passed:** {totalPassed} ({((double)totalPassed / allFiles.Count * 100.0):F2}%)");
            footer.AppendLine($"**Total Failed:** {totalFailed} ({((double)totalFailed / allFiles.Count * 100.0):F2}%)");
            
            footer.AppendLine();
            footer.AppendLine("## Worst 5 Chunks (by Failure Count)");
            footer.AppendLine("| Chunk | Range | Failed | Pass % |");
            footer.AppendLine("|-------|-------|--------|--------|");
            
            foreach (var stat in chunkStats.OrderByDescending(s => s.Failed).Take(5))
            {
                footer.AppendLine($"| {stat.ChunkId} | {stat.Range} | {stat.Failed} | {stat.PassRate:F1}% |");
            }
            
            // Append footer
            await File.AppendAllTextAsync(resultsFile, "\n" + footer.ToString());
            Console.WriteLine($"All chunks complete. Summary saved to {resultsFile}");
        }
    }
}
