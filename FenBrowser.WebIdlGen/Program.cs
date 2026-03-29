using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FenBrowser.Core.WebIDL;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("Usage: webidlgen --idl <idl-dir> --out <output-dir> [--ns <namespace>] [--verify]");
    return 1;
}

string idlDir = null;
string outDir = null;
string ns = "FenBrowser.FenEngine.Bindings.Generated";
bool verifyOnly = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--idl" when i + 1 < args.Length:
            idlDir = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outDir = args[++i];
            break;
        case "--ns" when i + 1 < args.Length:
            ns = args[++i];
            break;
        case "--verify":
            verifyOnly = true;
            break;
    }
}

if (string.IsNullOrWhiteSpace(idlDir) || string.IsNullOrWhiteSpace(outDir))
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

var idlFiles = Directory
    .GetFiles(idlDir, "*.idl", SearchOption.AllDirectories)
    .OrderBy(path => Path.GetRelativePath(idlDir, path), StringComparer.Ordinal)
    .ToArray();

if (idlFiles.Length == 0)
{
    Console.Error.WriteLine($"WARNING: No .idl files found in {idlDir}");
    return 0;
}

Console.WriteLine($"[webidlgen] Found {idlFiles.Length} IDL file(s) in {idlDir}");

var parser = new WebIdlParser();
var merged = new IdlParseResult();

foreach (var file in idlFiles)
{
    var source = File.ReadAllText(file);
    var parseResult = parser.Parse(source);
    var relativePath = NormalizePath(Path.GetRelativePath(idlDir, file));

    foreach (var definition in parseResult.Definitions)
    {
        merged.Definitions.Add(definition);
    }

    foreach (var error in parseResult.Errors)
    {
        merged.Errors.Add($"{relativePath}: {error}");
    }
}

if (merged.Errors.Count > 0)
{
    foreach (var err in merged.Errors)
    {
        Console.Error.WriteLine($"  PARSE ERROR: {err}");
    }

    Console.Error.WriteLine($"[webidlgen] {merged.Errors.Count} parse error(s). Aborting.");
    return 1;
}

var generator = new WebIdlBindingGenerator(new BindingGeneratorOptions
{
    Namespace = ns,
    EmitBrandChecks = true,
    EmitSameObjectCaching = true,
    EmitExposedChecks = true,
    EmitCEReactions = true
});

var files = generator.Generate(merged)
    .OrderBy(file => file.FileName, StringComparer.Ordinal)
    .ToList();

var expectedOutputs = new HashSet<string>(files.Select(file => file.FileName), StringComparer.Ordinal);
var existingOutputs = Directory.GetFiles(outDir, "*.g.cs", SearchOption.TopDirectoryOnly)
    .Select(Path.GetFileName)
    .Where(name => !string.IsNullOrWhiteSpace(name))
    .ToHashSet(StringComparer.Ordinal);

var changedFiles = new List<string>();
var removedFiles = new List<string>();

foreach (var file in files)
{
    var path = Path.Combine(outDir, file.FileName);
    var existing = File.Exists(path) ? File.ReadAllText(path) : null;
    if (!string.Equals(existing, file.SourceCode, StringComparison.Ordinal))
    {
        changedFiles.Add(file.FileName);
        if (!verifyOnly)
        {
            File.WriteAllText(path, file.SourceCode, Encoding.UTF8);
            Console.WriteLine($"  wrote {file.FileName}");
        }
    }
}

foreach (var staleFile in existingOutputs.Where(name => !expectedOutputs.Contains(name)).OrderBy(name => name, StringComparer.Ordinal))
{
    removedFiles.Add(staleFile);
    if (!verifyOnly)
    {
        File.Delete(Path.Combine(outDir, staleFile));
        Console.WriteLine($"  removed stale {staleFile}");
    }
}

var manifest = new WebIdlManifest
{
    Namespace = ns,
    Inputs = idlFiles.Select(path => new ManifestInput
    {
        RelativePath = NormalizePath(Path.GetRelativePath(idlDir, path)),
        Sha256 = ComputeSha256(File.ReadAllText(path))
    }).ToList(),
    Outputs = files.Select(file => new ManifestOutput
    {
        FileName = file.FileName,
        Sha256 = ComputeSha256(file.SourceCode)
    }).ToList()
};

var manifestPath = Path.Combine(outDir, "webidl-bindings-manifest.json");
var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
var existingManifest = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : null;
if (!string.Equals(existingManifest, manifestJson, StringComparison.Ordinal))
{
    changedFiles.Add(Path.GetFileName(manifestPath));
    if (!verifyOnly)
    {
        File.WriteAllText(manifestPath, manifestJson, Encoding.UTF8);
        Console.WriteLine("  wrote webidl-bindings-manifest.json");
    }
}

if (verifyOnly)
{
    if (changedFiles.Count > 0 || removedFiles.Count > 0)
    {
        foreach (var changed in changedFiles.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"  VERIFY FAILED: {changed} is out of date");
        }

        foreach (var removed in removedFiles)
        {
            Console.Error.WriteLine($"  VERIFY FAILED: stale generated file present: {removed}");
        }

        Console.Error.WriteLine("[webidlgen] Verification failed.");
        return 2;
    }

    Console.WriteLine("[webidlgen] Verification passed.");
    return 0;
}

Console.WriteLine($"[webidlgen] Done. {changedFiles.Distinct(StringComparer.Ordinal).Count()}/{files.Count + 1} output(s) updated.");
return 0;

static string NormalizePath(string path) => path.Replace('\\', '/');

static string ComputeSha256(string value)
{
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed class WebIdlManifest
{
    public string Namespace { get; set; } = string.Empty;
    public List<ManifestInput> Inputs { get; set; } = new();
    public List<ManifestOutput> Outputs { get; set; } = new();
}

internal sealed class ManifestInput
{
    public string RelativePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class ManifestOutput
{
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}
