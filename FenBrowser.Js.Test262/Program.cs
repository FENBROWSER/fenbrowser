using FenBrowser.Js.Test262;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: fenjs-test262 --list | --dry-run | --parser-subset [--root <path>] [--out <path>] [--max <n>] [--expectations <path>]");
    return 1;
}

var root = "external/test262";
var outPath = "Results/test262/dry-run.json";
var list = false;
var dryRun = false;
var parserSubset = false;
var max = 200;
string? expectationsPath = null;

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
    }
}

if (!list && !dryRun && !parserSubset)
{
    Console.Error.WriteLine("Specify at least one of --list, --dry-run, or --parser-subset.");
    return 2;
}

if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"test262 root not found: {root}");
    return 3;
}

var runner = new Test262Runner();
return runner.Run(root, list, dryRun, parserSubset, outPath, max, expectationsPath);
