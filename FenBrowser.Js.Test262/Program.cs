using FenBrowser.Js.Test262;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: fenjs-test262 --list | --dry-run | --parser-subset | --runtime-subset | --dashboard | --verify-gates [--root <path>] [--test262 <path>] [--test262-file <file>] [--features <a,b,c>] [--supported-features <a,b,c>] [--out|--output <path>] [--max <n>] [--timeout-ms <n>] [--engine <name>] [--expectations <path>] [--in <result.json>] [--previous <result.json>]");
    return 1;
}

var root = "external/test262";
var outPath = "Results/test262/dry-run.json";
var list = false;
var dryRun = false;
var parserSubset = false;
var runtimeSubset = false;
var dashboard = false;
var verifyGates = false;
var max = 200;
var skip = 0;
var timeoutMs = 2000;
var timeoutGiven = false;
var engine = "FenJS";
string? expectationsPath = null;
string? inputPath = null;
string? previousPath = null;
string? test262Path = null;
string? test262File = null;
var test262Shallow = false;
string? featuresCsv = null;
string? supportedFeaturesCsv = null;
string? progressFilePath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--list":
            list = true;
            break;
        case "--dry-run":
            dryRun = true;
            break;
        case "--parser-subset":
            parserSubset = true;
            break;
        case "--runtime-subset":
            runtimeSubset = true;
            break;
        case "--dashboard":
            dashboard = true;
            break;
        case "--verify-gates":
            verifyGates = true;
            break;
        case "--root" when i + 1 < args.Length:
            root = args[++i];
            break;
        case "--test262" when i + 1 < args.Length:
            test262Path = args[++i];
            break;
        case "--test262-file" when i + 1 < args.Length:
            test262File = args[++i];
            break;
        case "--test262-shallow":
            test262Shallow = true;
            break;
        case "--features" when i + 1 < args.Length:
            featuresCsv = args[++i];
            break;
        case "--supported-features" when i + 1 < args.Length:
            supportedFeaturesCsv = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
        case "--output" when i + 1 < args.Length:
            outPath = args[++i];
            break;
        case "--max" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed):
            max = parsed;
            i++;
            break;
        case "--skip" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedSkip):
            skip = Math.Max(0, parsedSkip);
            i++;
            break;
        case "--timeout-ms" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedTimeout):
            // A second --timeout-ms would silently override the first (a driver's
            // mandatory 2000 ms by a forwarded argument), so it is an error.
            if (timeoutGiven)
            {
                Console.Error.WriteLine("--timeout-ms given more than once.");
                return 2;
            }
            timeoutGiven = true;
            timeoutMs = parsedTimeout;
            i++;
            break;
        case "--engine" when i + 1 < args.Length:
            engine = args[++i];
            break;
        case "--expectations" when i + 1 < args.Length:
            expectationsPath = args[++i];
            break;
        case "--in" when i + 1 < args.Length:
            inputPath = args[++i];
            break;
        case "--previous" when i + 1 < args.Length:
            previousPath = args[++i];
            break;
        case "--progress-file" when i + 1 < args.Length:
            progressFilePath = args[++i];
            break;
    }
}

if (!list && !dryRun && !parserSubset && !runtimeSubset && !dashboard && !verifyGates)
{
    Console.Error.WriteLine("Specify at least one of --list, --dry-run, --parser-subset, --runtime-subset, --dashboard, or --verify-gates.");
    return 2;
}

if ((list || dryRun || parserSubset || runtimeSubset) && !Directory.Exists(root))
{
    Console.Error.WriteLine($"test262 root not found: {root}");
    return 3;
}

// Auto-derive progress file path when TEST262_PROGRESS=1 env var is set.
if (progressFilePath == null &&
    string.Equals(Environment.GetEnvironmentVariable("TEST262_PROGRESS"), "1", StringComparison.OrdinalIgnoreCase))
{
    progressFilePath = Path.ChangeExtension(outPath, null) + "_progress.jsonl";
}

var runner = new Test262Runner();
var exitCode = runner.Run(root, list, dryRun, parserSubset, runtimeSubset, dashboard, verifyGates, outPath, max, timeoutMs, engine, expectationsPath, inputPath, previousPath, test262Path, test262File, featuresCsv, supportedFeaturesCsv, test262Shallow, skip, progressFilePath);

// test262 is the largest and most varied body of JavaScript this engine has,
// so it is the honest place to ask how much of a real workload the
// register-window loop can run and what is holding the rest back.
if (FenBrowser.Js.Interpreter2.Interp2Options.Log)
{
    Console.Error.Write(FenBrowser.Js.Interpreter2.Interp2Stats.Report());
}

return exitCode;
