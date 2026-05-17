using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// Per-script profile of x.com's inline JS. Page-load profile reports JS at ~3.5s total —
    /// this isolates which script(s) dominate so we know whether the cost is one pathological
    /// case (huge object literal, regex, eval, etc.) or spread evenly.
    /// </summary>
    public class InlineScriptProfile
    {
        private readonly ITestOutputHelper _out;

        public InlineScriptProfile(ITestOutputHelper output)
        {
            _out = output;
        }

        private void WriteLine(string s = "")
        {
            _out.WriteLine(s);
            try { File.AppendAllText(@"C:\Users\udayk\Videos\fenbrowser-test\inline_script_profile.log", s + Environment.NewLine); } catch { }
        }

        private static string ResolveFixture(string fileName)
        {
            var probe = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(probe); i++)
            {
                var candidate = Path.Combine(probe, "FenBrowser.Tests", "Engine", fileName);
                if (File.Exists(candidate)) return candidate;
                probe = Path.GetDirectoryName(probe);
            }
            return null;
        }

        [Fact(DisplayName = "x.com inline script per-script profile")]
        public void Profile_EachInlineScript_ReportsTimingAndShape()
        {
            try { File.WriteAllText(@"C:\Users\udayk\Videos\fenbrowser-test\inline_script_profile.log", ""); } catch { }
            var fixturePath = ResolveFixture("sample_xcom.html");
            if (fixturePath == null) { WriteLine("no fixture"); return; }

            var html = File.ReadAllText(fixturePath);
            var doc = HtmlParser.ParseDocument(html);

            var scripts = new List<string>();
            CollectInlineScripts(doc.DocumentElement, scripts);
            WriteLine($"Found {scripts.Count} inline scripts");
            WriteLine(new string('-', 80));
            WriteLine($"{"#",3} {"bytes",10} {"compile ms",12} {"run ms",10} {"status",10}  shape-tags");
            WriteLine(new string('-', 80));

            double totalCompile = 0, totalRun = 0;
            for (int i = 0; i < scripts.Count; i++)
            {
                var src = scripts[i];
                var shape = ShapeTags(src);

                // Fresh runtime per script so prior side effects don't pollute timing.
                var runtime = new FenRuntime();

                // Cheap warmup so JIT/PGO doesn't show up here as cold-start noise.
                var sw1 = Stopwatch.StartNew();
                double compileMs, runMs;
                string status;
                try
                {
                    runtime.ExecuteSimple(src);
                    sw1.Stop();
                    compileMs = 0; // ExecuteSimple is compile+run; we treat as wall-clock only.
                    runMs = sw1.Elapsed.TotalMilliseconds;
                    status = "ok";
                }
                catch (Exception ex)
                {
                    sw1.Stop();
                    compileMs = 0;
                    runMs = sw1.Elapsed.TotalMilliseconds;
                    status = ex.GetType().Name;
                }

                totalCompile += compileMs;
                totalRun += runMs;
                WriteLine($"{i,3} {src.Length,10:N0} {compileMs,12:F1} {runMs,10:F1} {status,10}  {shape}");
            }

            WriteLine(new string('-', 80));
            WriteLine($"  total run: {totalRun:F1} ms");
            WriteLine();
            WriteLine("=== Drilldown on largest script ===");
            int largestIdx = 0;
            for (int i = 1; i < scripts.Count; i++)
                if (scripts[i].Length > scripts[largestIdx].Length) largestIdx = i;
            var largest = scripts[largestIdx];
            WriteLine($"Script #{largestIdx}, {largest.Length:N0} bytes");
            WriteLine($"  preview (first 240 chars): {Preview(largest, 240)}");

            // Heuristic shape counters — give us hints about what bytecode patterns dominate.
            WriteLine($"  function decls : {CountRegex(largest, "function ")}");
            WriteLine($"  arrow fns      : {CountRegex(largest, "=>")}");
            WriteLine($"  try blocks     : {CountRegex(largest, "try{") + CountRegex(largest, "try {")}");
            WriteLine($"  string literals: {CountRegex(largest, "\"") / 2}  (rough — quote pairs)");
            WriteLine($"  regex literals : {CountRegex(largest, "/")}  (slash count, includes division)");
            WriteLine($"  new exprs      : {CountRegex(largest, "new ")}");
            WriteLine($"  for loops      : {CountRegex(largest, "for(") + CountRegex(largest, "for (")}");
            WriteLine($"  while loops    : {CountRegex(largest, "while(") + CountRegex(largest, "while (")}");
            WriteLine($"  obj literals   : {CountRegex(largest, "{") }  (lone-brace count)");

            // Split parse vs execute for the largest script to see which dominates.
            WriteLine();
            WriteLine("=== Parse-vs-execute split (largest script) ===");
            var swParse = Stopwatch.StartNew();
            var lexer = new FenBrowser.FenEngine.Core.Lexer(largest);
            var parser = new FenBrowser.FenEngine.Core.Parser(lexer);
            var program = parser.ParseProgram();
            swParse.Stop();
            WriteLine($"  parse only   : {swParse.Elapsed.TotalMilliseconds,10:F1} ms");

            var swCompile = Stopwatch.StartNew();
            var compiler = new FenBrowser.FenEngine.Core.Bytecode.Compiler.BytecodeCompiler();
            var codeBlock = compiler.Compile(program);
            swCompile.Stop();
            WriteLine($"  compile only : {swCompile.Elapsed.TotalMilliseconds,10:F1} ms");
            WriteLine($"  bytecode bytes: {codeBlock.Instructions.Length:N0}");
            WriteLine($"  constants    : {codeBlock.Constants.Count:N0}");

            var swExecute = Stopwatch.StartNew();
            var runtime2 = new FenRuntime();
            runtime2.ExecuteSimple(largest);
            swExecute.Stop();
            WriteLine($"  parse+exec   : {swExecute.Elapsed.TotalMilliseconds,10:F1} ms");
            WriteLine($"  ⇒ exec-only ~ {swExecute.Elapsed.TotalMilliseconds - swParse.Elapsed.TotalMilliseconds - swCompile.Elapsed.TotalMilliseconds,10:F1} ms");
        }

        private static string Preview(string s, int n)
        {
            s = s.Replace("\n", "\\n").Replace("\r", "");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private static int CountRegex(string s, string sub)
        {
            int n = 0, idx = 0;
            while ((idx = s.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0)
            {
                n++;
                idx += sub.Length;
            }
            return n;
        }

        private static string ShapeTags(string src)
        {
            var tags = new List<string>();
            if (src.Contains("\"use strict\"") || src.Contains("'use strict'")) tags.Add("strict");
            if (src.Contains("eval(")) tags.Add("eval");
            if (src.Contains("new Function")) tags.Add("newFn");
            if (CountRegex(src, "JSON.parse") > 0) tags.Add($"jsonp{CountRegex(src, "JSON.parse")}");
            if (CountRegex(src, "function ") > 5) tags.Add($"fns{CountRegex(src, "function ")}");
            if (src.Length > 50_000) tags.Add("huge");
            return tags.Count == 0 ? "-" : string.Join(",", tags);
        }

        private static void CollectInlineScripts(Node n, List<string> sink)
        {
            if (n == null) return;
            if (n is Element e && string.Equals(e.LocalName, "script", StringComparison.OrdinalIgnoreCase) && !e.HasAttribute("src"))
            {
                var sb = new StringBuilder();
                for (var k = e.FirstChild; k != null; k = k.NextSibling)
                    if (k is Text t) sb.Append(t.Data);
                var body = sb.ToString();
                if (!string.IsNullOrWhiteSpace(body)) sink.Add(body);
            }
            for (var k = n.FirstChild; k != null; k = k.NextSibling)
                CollectInlineScripts(k, sink);
        }
    }
}
