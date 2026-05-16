using System;
using System.Diagnostics;
using FenBrowser.FenEngine.Core;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Engine
{
    public class JsEngineBenchmarks
    {
        private readonly ITestOutputHelper _out;

        public JsEngineBenchmarks(ITestOutputHelper output)
        {
            _out = output;
        }

        private double TimeMs(string label, string script, int repeats = 1)
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(script);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < repeats; i++)
            {
                runtime.ExecuteSimple(script);
            }
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds / repeats;
            _out.WriteLine($"{label,-32} {ms,10:F2} ms");
            return ms;
        }

        [Fact(DisplayName = "JS Engine Baseline Benchmarks")]
        public void Run_All_Benchmarks()
        {
            _out.WriteLine("=== FenEngine JS Baseline ===");
            _out.WriteLine($"{"Benchmark",-32} {"Time",10}");
            _out.WriteLine(new string('-', 46));

            TimeMs("empty",
                "var x = 0;");

            // Both shapes shown: realistic JS lives inside functions where slot-based locals
            // and the superinstruction peephole (IncrementLocalByConst, LocalLessThanConst) apply.
            TimeMs("tight-loop-1M (script)",
                "var s = 0; for (var i = 0; i < 1000000; i++) { s = s + 1; }");
            TimeMs("tight-loop-1M (function)",
                "function run(){ var s = 0; for (var i = 0; i < 1000000; i++) { s = s + 1; } return s; } run();");

            TimeMs("arith-loop-1M",
                "var s = 0; for (var i = 0; i < 1000000; i++) { s = s + i * 2 - 1; }");

            TimeMs("fib-30-recursive",
                "function fib(n){ if (n<2) return n; return fib(n-1)+fib(n-2); } var r = fib(30);");

            TimeMs("fib-25-recursive",
                "function fib(n){ if (n<2) return n; return fib(n-1)+fib(n-2); } var r = fib(25);");

            TimeMs("property-access-100k",
                "var o = {a:1, b:2, c:3, d:4, e:5}; var s = 0; " +
                "for (var i = 0; i < 100000; i++) { s = s + o.a + o.b + o.c + o.d + o.e; }");

            TimeMs("property-write-100k",
                "var o = {a:0}; for (var i = 0; i < 100000; i++) { o.a = i; }");

            TimeMs("function-call-100k",
                "function add(a,b){ return a+b; } var s = 0; " +
                "for (var i = 0; i < 100000; i++) { s = add(s, i); }");

            TimeMs("array-push-50k",
                "var a = []; for (var i = 0; i < 50000; i++) { a.push(i); }");

            TimeMs("array-sum-100k",
                "var a = []; for (var i = 0; i < 100000; i++) { a[i] = i; } " +
                "var s = 0; for (var j = 0; j < 100000; j++) { s = s + a[j]; }");

            TimeMs("string-concat-10k",
                "var s = ''; for (var i = 0; i < 10000; i++) { s = s + 'x'; }");

            TimeMs("object-create-10k",
                "for (var i = 0; i < 10000; i++) { var o = { x: i, y: i+1, z: i+2 }; }");

            _out.WriteLine(new string('-', 46));
            _out.WriteLine("Reference (Node/V8 typical on this hardware, ms):");
            _out.WriteLine("  tight-loop-1M:      ~2-5 ms");
            _out.WriteLine("  fib-30-recursive:   ~10-20 ms");
            _out.WriteLine("  property-access-100k: ~1-3 ms");
        }
    }
}
