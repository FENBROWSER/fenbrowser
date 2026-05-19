using FenBrowser.Js.Test262;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: fenjs-test262 --list | --dry-run | --parser-subset | --dashboard | --verify-gates [--root <path>] [--out <path>] [--max <n>] [--expectations <path>] [--in <result.json>] [--previous <result.json>]");
    return 1;
}

var root = "external/test262";
var outPath = "Results/test262/dry-run.json";
var list = false;
var dryRun = false;
var parserSubset = false;
var dashboard = false;
var verifyGates = false;
var max = 200;
string? expectationsPath = null;
string? inputPath = null;
string? previousPath = null;

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
        case "--dashboard":
            dashboard = true;
            break;
        case "--verify-gates":
            verifyGates = true;
            break;
        case "--root" when i + 1 < args.Length:
            root = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;
        case "--max" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed):
            max = parsed;
            i++;
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
    }
}

if (!list && !dryRun && !parserSubset && !dashboard && !verifyGates)
{
    Console.Error.WriteLine("Specify at least one of --list, --dry-run, --parser-subset, --dashboard, or --verify-gates.");
    return 2;
}

if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"test262 root not found: {root}");
    return 3;
}

var runner = new Test262Runner();
return runner.Run(root, list, dryRun, parserSubset, dashboard, verifyGates, outPath, max, expectationsPath, inputPath, previousPath);
