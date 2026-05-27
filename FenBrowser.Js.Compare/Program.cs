using System.Diagnostics;
using System.Globalization;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Compare;

// Tier 6 #30 — differential testing tool + benchmark suite.
//
// Subcommands:
//   compare <file.js>            — run a single script through FenJS and Node, diff stdout
//   compare-dir <dir>            — run every *.js in <dir>, summarise pass/fail
//   bench [--iter N]             — run the built-in microbenchmark suite
//   bench-vs-node [--iter N]     — same suite, also timing Node side-by-side
//
// Node.js is required for compare and bench-vs-node. Set FENJS_NODE_PATH
// to override the auto-detected `node` binary on PATH.
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();
        return args[0] switch
        {
            "compare" => args.Length >= 2 ? CompareFile(args[1]) : Usage(),
            "compare-dir" => args.Length >= 2 ? CompareDir(args[1]) : Usage(),
            "bench" => RunBench(ParseIter(args), withNode: false),
            "bench-vs-node" => RunBench(ParseIter(args), withNode: true),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("fenjs-compare <command> [args]");
        Console.Error.WriteLine("  compare <file.js>            — diff FenJS vs Node on one file");
        Console.Error.WriteLine("  compare-dir <dir>            — diff all *.js in directory");
        Console.Error.WriteLine("  bench [--iter N]             — run microbenchmark suite");
        Console.Error.WriteLine("  bench-vs-node [--iter N]     — also time Node side-by-side");
        return 2;
    }

    private static int ParseIter(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--iter" && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;
        return 1;
    }

    // ----- Differential testing ---------------------------------------

    private static int CompareFile(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"file not found: {path}");
            return 1;
        }

        var source = File.ReadAllText(path);
        var result = DiffOne(path, source);
        Console.WriteLine(FormatResult(result));
        return result.Match ? 0 : 1;
    }

    private static int CompareDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"directory not found: {dir}");
            return 1;
        }

        var files = Directory.GetFiles(dir, "*.js", SearchOption.AllDirectories);
        var pass = 0; var fail = 0; var skip = 0;
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var result = DiffOne(file, source);
            if (result.Skipped) { skip++; continue; }
            if (result.Match) { pass++; } else { fail++; Console.WriteLine(FormatResult(result)); }
        }

        Console.WriteLine($"\n{files.Length} files: {pass} match, {fail} differ, {skip} skipped");
        return fail == 0 ? 0 : 1;
    }

    private sealed record DiffResult(string Path, bool Match, bool Skipped, string FenOutput, string NodeOutput, string? Reason);

    private static DiffResult DiffOne(string path, string source)
    {
        // Compare the value of the last expression. Node side wraps the
        // script in an IIFE and writes the return value via process.stdout
        // so we get a single comparable line regardless of trailing
        // newlines or console.log usage. Scripts should `return <expr>;`
        // on their final line.
        var nodeWrapped = $"const __r=(function(){{{source}}})();process.stdout.write(__r===undefined?'undefined':__r===null?'null':String(__r));";
        var node = NodeRunner.TryRun(nodeWrapped, timeoutMs: 5000);
        if (node is null)
            return new DiffResult(path, Match: false, Skipped: true, "", "", "node unavailable");

        var fenWrapped = $"(function(){{{source}}})();";
        var fenjs = FenJsRunner.Run(fenWrapped, timeoutMs: 5000);
        var normalisedFen = Normalise(fenjs);
        var normalisedNode = Normalise(node);
        var match = string.Equals(normalisedFen, normalisedNode, StringComparison.Ordinal);
        return new DiffResult(path, match, Skipped: false, fenjs, node, null);
    }

    private static string Normalise(string output) =>
        output.Replace("\r\n", "\n").TrimEnd();

    private static string FormatResult(DiffResult r)
    {
        if (r.Skipped) return $"SKIP  {r.Path}  ({r.Reason})";
        if (r.Match) return $"MATCH {r.Path}";
        return $"DIFF  {r.Path}\n  fenjs : {Truncate(r.FenOutput)}\n  node  : {Truncate(r.NodeOutput)}";
    }

    private static string Truncate(string s)
    {
        s = s.Replace("\r\n", " | ").Replace("\n", " | ");
        return s.Length <= 200 ? s : s.Substring(0, 200) + "…";
    }

    // ----- Benchmark suite --------------------------------------------

    private static int RunBench(int iter, bool withNode)
    {
        Console.WriteLine($"FenJS microbenchmark suite (iterations={iter})\n");
        var fmt = withNode
            ? "{0,-30}  {1,12}  {2,12}  {3,8}"
            : "{0,-30}  {1,12}";
        Console.WriteLine(withNode
            ? string.Format(CultureInfo.InvariantCulture, fmt, "benchmark", "fenjs (ms)", "node (ms)", "ratio")
            : string.Format(CultureInfo.InvariantCulture, fmt, "benchmark", "fenjs (ms)"));
        Console.WriteLine(new string('-', withNode ? 70 : 45));

        foreach (var b in Benchmarks)
        {
            string fenStr;
            double fenMs = 0;
            try { fenMs = TimeFenJs(b.Source, iter); fenStr = fenMs.ToString("F1", CultureInfo.InvariantCulture); }
            catch (Exception ex) { fenStr = "ERROR: " + ex.GetType().Name; }

            if (withNode)
            {
                var nodeMs = TimeNode(b.Source, iter);
                var ratio = nodeMs > 0 && fenMs > 0 ? (fenMs / nodeMs).ToString("F2", CultureInfo.InvariantCulture) + "x" : "—";
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, fmt,
                    b.Name, fenStr,
                    nodeMs.ToString("F1", CultureInfo.InvariantCulture), ratio));
            }
            else
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, fmt, b.Name, fenStr));
            }
        }

        return 0;
    }

    private sealed record Benchmark(string Name, string Source);

    private static readonly Benchmark[] Benchmarks =
    {
        new("arith-loop-100k", "var s = 0; for (var i = 0; i < 100000; i++) s += i * 3 - 1; s;"),
        new("string-concat-1k", "var s = ''; for (var i = 0; i < 1000; i++) s += 'x'; s.length;"),
        new("array-push-10k", "var a = []; for (var i = 0; i < 10000; i++) a.push(i); a.length;"),
        new("object-set-1k", "var o = {}; for (var i = 0; i < 1000; i++) o['k' + i] = i; Object.keys(o).length;"),
        new("function-call-100k", "function f(x) { return x + 1; } var s = 0; for (var i = 0; i < 100000; i++) s = f(s); s;"),
        new("recursive-fib-25", "function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); } fib(25);"),
        new("closure-capture-10k", "function mk(i) { return function() { return i; }; } var s = 0; for (var i = 0; i < 10000; i++) s += mk(i)(); s;"),
        new("array-reduce-10k", "var a = []; for (var i = 0; i < 10000; i++) a.push(i); a.reduce(function(p,c){return p+c;}, 0);"),
    };

    private static double TimeFenJs(string source, int iter)
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText(source));
        var realm = new JsRealm();
        // Warm-up — let JIT tier up before measuring.
        for (var w = 0; w < 5; w++) realm.Execute(fn);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iter; i++) realm.Execute(fn);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iter;
    }

    private static double TimeNode(string source, int iter)
    {
        // Wrap each iteration inside node to amortise process start.
        var wrapped = $"const N={iter};const _t=process.hrtime.bigint();for(let i=0;i<N;i++){{{source}}};const _e=process.hrtime.bigint();process.stdout.write(((Number(_e-_t)/1e6)/N).toString());";
        var output = NodeRunner.TryRun(wrapped, timeoutMs: 30000) ?? "0";
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : 0;
    }
}

internal static class NodeRunner
{
    private static readonly string NodePath =
        Environment.GetEnvironmentVariable("FENJS_NODE_PATH") ?? "node";

    public static string? TryRun(string source, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = NodePath,
                ArgumentList = { "-e", source },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { /* ignore */ } return null; }
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            return p.ExitCode == 0 ? stdout : "ERROR: " + stderr;
        }
        catch
        {
            return null;
        }
    }
}

internal static class FenJsRunner
{
    public static string Run(string source, int timeoutMs)
    {
        try
        {
            var compiler = new BytecodeCompiler();
            var fn = compiler.CompileScript(new SourceText(source));
            var realm = new JsRealm();
            realm.Interpreter.WallClockTimeoutMs = timeoutMs;
            var result = realm.Execute(fn);
            return ValueToString(result);
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static string ValueToString(FenBrowser.Js.Runtime.JsValue v) => v.Tag switch
    {
        FenBrowser.Js.Runtime.JsValueTag.Undefined => "undefined",
        FenBrowser.Js.Runtime.JsValueTag.Null => "null",
        FenBrowser.Js.Runtime.JsValueTag.Boolean => v.AsBoolean() ? "true" : "false",
        FenBrowser.Js.Runtime.JsValueTag.Int32 => v.AsInt32().ToString(CultureInfo.InvariantCulture),
        FenBrowser.Js.Runtime.JsValueTag.Number => v.AsNumber().ToString("R", CultureInfo.InvariantCulture),
        FenBrowser.Js.Runtime.JsValueTag.String => v.AsString(),
        _ => "[" + v.Tag + "]",
    };
}
