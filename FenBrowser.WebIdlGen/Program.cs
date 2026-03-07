using System;
using System.IO;
using System.Linq;
using FenBrowser.Core.WebIDL;

// ── WebIDL Binding Generator Tool ────────────────────────────────────────────
// Usage:
//   webidlgen --idl <dir-or-glob> --out <output-dir> [--ns <namespace>]
//
// Reads all *.idl files from <dir-or-glob>, parses them with WebIdlParser,
// generates C# binding source with WebIdlBindingGenerator, and writes the
// resulting *.g.cs files to <output-dir>.
//
// Integrated into FenBrowser.FenEngine.csproj via a BeforeCompile target:
//   <Target Name="GenerateWebIdlBindings" BeforeTargets="BeforeBuild">
//     <Exec Command="dotnet run --project FenBrowser.WebIdlGen -- --idl ... --out ..." />
//   </Target>
// ─────────────────────────────────────────────────────────────────────────────

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("Usage: webidlgen --idl <idl-dir> --out <output-dir> [--ns <namespace>]");
    return 1;
}

string idlDir = null, outDir = null, ns = "FenBrowser.FenEngine.Bindings.Generated";

for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--idl": idlDir = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--ns":  ns    = args[++i]; break;
    }
}

if (string.IsNullOrEmpty(idlDir) || string.IsNullOrEmpty(outDir))
{
    Console.Error.WriteLine("ERROR: --idl and --out are required.");
    return 1;
}

if (!Directory.Exists(idlDir))
{
    Console.Error.WriteLine($"ERROR: IDL directory not found: {idlDir}");
    return 1;
}

Directory.CreateDirectory(outDir);

var idlFiles = Directory.GetFiles(idlDir, "*.idl", SearchOption.AllDirectories);
if (idlFiles.Length == 0)
{
    Console.Error.WriteLine($"WARNING: No .idl files found in {idlDir}");
    return 0;
}

Console.WriteLine($"[webidlgen] Found {idlFiles.Length} IDL file(s) in {idlDir}");

var parser = new WebIdlParser();
var parseResults = idlFiles
    .Select(f =>
    {
        var src = File.ReadAllText(f);
        return parser.Parse(src);
    })
    .ToList();

// Merge all parse results into one by combining into first result
var allErrors = parseResults.SelectMany(r => r.Errors).ToList();
var allDefs = parseResults.SelectMany(r => r.Definitions).ToList();
// Re-parse as single merged IDL string is not practical; use a wrapper
var merged = new IdlParseResult();
foreach (var d in allDefs) merged.Definitions.Add(d);
foreach (var e in allErrors) merged.Errors.Add(e);

if (merged.Errors.Count > 0)
{
    foreach (var err in merged.Errors)
        Console.Error.WriteLine($"  PARSE ERROR: {err}");
    Console.Error.WriteLine($"[webidlgen] {merged.Errors.Count} parse error(s). Aborting.");
    return 1;
}

var opts = new BindingGeneratorOptions
{
    Namespace = ns,
    EmitBrandChecks = true,
    EmitSameObjectCaching = true,
    EmitExposedChecks = true,
    EmitCEReactions = true,
};

var generator = new WebIdlBindingGenerator(opts);
var files = generator.Generate(merged);

int written = 0;
foreach (var file in files)
{
    var path = Path.Combine(outDir, file.FileName);
    var existing = File.Exists(path) ? File.ReadAllText(path) : null;
    if (existing != file.SourceCode)
    {
        File.WriteAllText(path, file.SourceCode);
        Console.WriteLine($"  wrote {file.FileName}");
        written++;
    }
}

Console.WriteLine($"[webidlgen] Done. {written}/{files.Count} file(s) updated.");
return 0;
