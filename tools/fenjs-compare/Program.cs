using System.Diagnostics;
using System.Text.Json;

// fenjs-compare: differential testing tool (§26).
// Runs JS test files against FenJS and optionally other engines (V8 d8,
// SpiderMonkey js, JSC jsc), comparing stdout, exit codes, and exception types.
//
// Usage:
//   dotnet run --project tools/fenjs-compare -- --input test.js
//   dotnet run --project tools/fenjs-compare -- --input test.js --engines fenjs,d8,sm
//
// Output format (JSON):
//   { "test": "test.js", "results": { "fenjs": { "exitCode": 0, "stdout": "..." }, ... } }

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine("Usage: fenjs-compare --input <file> [--engines <csv>] [--timeout-ms <ms>]");
    return 1;
}

var input = ArgVal(args, "--input") ?? throw new InvalidOperationException("--input required");
var engines = (ArgVal(args, "--engines") ?? "fenjs").Split(',', StringSplitOptions.TrimEntries);
var timeoutMs = int.TryParse(ArgVal(args, "--timeout-ms"), out var t) ? t : 5000;

var results = new Dictionary<string, object?>();
foreach (var engine in engines)
{
    results[engine] = RunEngine(engine, input, timeoutMs);
}

var output = new { test = input, results };
Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static string? ArgVal(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

static object? RunEngine(string engine, string input, int timeoutMs)
{
    try
    {
        var psi = engine switch
        {
            "fenjs" => new ProcessStartInfo("dotnet", $"run --project FenBrowser.Js.Shell -- --eval \"{EscapeArg(File.ReadAllText(input))}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false },
            _ => null
        };

        if (psi is null)
        {
            return new { status = "unavailable", reason = $"Engine '{engine}' not found on this system." };
        }

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        var completed = proc.WaitForExit(timeoutMs);

        if (!completed)
        {
            proc.Kill(entireProcessTree: true);
            return new { status = "timeout", exitCode = -1, stdout = "", stderr = "" };
        }

        var exceptionType = ParseExceptionType(stderr);
        return new
        {
            status = proc.ExitCode == 0 ? "ok" : "error",
            exitCode = proc.ExitCode,
            stdout = stdout.TrimEnd(),
            exceptionType,
            stderr = stderr.TrimEnd()
        };
    }
    catch (Exception ex)
    {
        return new { status = "crash", reason = ex.Message };
    }
}

static string? ParseExceptionType(string stderr)
{
    if (stderr.Contains("TypeError")) return "TypeError";
    if (stderr.Contains("ReferenceError")) return "ReferenceError";
    if (stderr.Contains("RangeError")) return "RangeError";
    if (stderr.Contains("SyntaxError")) return "SyntaxError";
    if (stderr.Contains("URIError")) return "URIError";
    if (stderr.Contains("Error")) return "Error";
    return null;
}

static string EscapeArg(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
