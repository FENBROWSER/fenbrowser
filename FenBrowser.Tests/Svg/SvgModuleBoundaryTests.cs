using System.Text.RegularExpressions;

namespace FenBrowser.Tests.Svg;

/// <summary>
/// Keeps the first-party SVG module a module while it still lives inside
/// FenBrowser.FenEngine. Code outside FenEngine/Svg reaches the engine only
/// through the ISvgRenderer seam in FenEngine/Adapters, and the module's own
/// dependencies on the rest of FenEngine are frozen to the CSS syntax layer and
/// the font stack. Both lists are exact: widening either one is a design
/// decision to record here, not something to slip in with a feature.
/// </summary>
public sealed partial class SvgModuleBoundaryTests
{
    private const string ModuleDirectory = "FenBrowser.FenEngine/Svg/";
    private const string SeamDirectory = "FenBrowser.FenEngine/Adapters/";

    private static readonly string[] ConsumerProjects =
    {
        "FenBrowser.FenEngine", "FenBrowser.Host", "FenBrowser.Core", "FenBrowser.DevTools", "FenBrowser.WebDriver"
    };

    /// <summary>Files outside the module and seam that may name module types, and which ones.</summary>
    private static readonly Dictionary<string, string[]> AllowedConsumers = new(StringComparer.Ordinal)
    {
        // Same-origin nested resource discovery for image prewarming.
        ["FenBrowser.FenEngine/Rendering/ImageLoader.cs"] = new[] { "SvgResourceDiscovery" },
        // Computed-style resolution of SVG geometry properties for DOM scripting.
        ["FenBrowser.FenEngine/Scripting/BrowserScriptEngineRuntime.cs"] = new[] { "SvgCssLengthEvaluator" },
        // The SVG DOM transform lists parse and compose with the renderer's own grammar.
        ["FenBrowser.FenEngine/Scripting/FenJsSvgDom.cs"] = new[] { "SvgValues", "SvgTransformFunction", "SvgTransformKind" },
        // SVGGeometryElement measures the outline the renderer paints.
        ["FenBrowser.FenEngine/Scripting/FenJsSvgGeometry.cs"] = new[] { "SvgGeometryOutline", "SvgValues", "SvgCssLengthEvaluator" },
    };

    /// <summary>FenEngine namespaces the module may use, and from which module files.</summary>
    private static readonly Dictionary<string, string[]?> AllowedModuleDependencies = new(StringComparer.Ordinal)
    {
        ["FenEngine.Svg"] = null,
        ["FenEngine.Adapters"] = null,
        ["FenEngine.Rendering.Css"] = new[] { "SvgCssCascade.cs", "SvgCssLengthEvaluator.cs", "Shapes.cs" },
        ["FenEngine.Typography"] = new[] { "Text.cs", "TextBidi.cs", "TextPath.cs" },
        ["FenEngine.Layout"] = new[] { "SvgTypefaceResolver.cs" },
        ["FenEngine.Rendering"] = new[] { "SvgTypefaceResolver.cs" },
    };

    [Fact]
    public void OnlyTheSeamAndListedConsumersReachIntoTheModule()
    {
        string root = FindRepositoryRoot();
        string[] moduleTypes = ModuleTypes(root);
        var violations = new List<string>();

        foreach (string project in ConsumerProjects)
        {
            foreach (string file in SourceFiles(root, project))
            {
                string relative = Relative(root, file);
                if (relative.StartsWith(ModuleDirectory, StringComparison.Ordinal) ||
                    relative.StartsWith(SeamDirectory, StringComparison.Ordinal))
                {
                    continue;
                }

                string code = StripComments(File.ReadAllText(file));
                AllowedConsumers.TryGetValue(relative, out string[]? allowed);
                foreach (string type in moduleTypes)
                {
                    if (Regex.IsMatch(code, $@"\b{type}\b") && (allowed == null || !allowed.Contains(type)))
                    {
                        violations.Add($"{relative} uses {type}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Reach the SVG engine through ISvgRenderer (FenEngine/Adapters) instead: " +
            string.Join("; ", violations));
    }

    [Fact]
    public void TheModuleDependsOnlyOnItsListedFenEngineNamespaces()
    {
        string root = FindRepositoryRoot();
        var violations = new List<string>();
        HashSet<string> namespaces = FenEngineNamespaces(root);

        foreach (string file in SourceFiles(root, ModuleDirectory.TrimEnd('/')))
        {
            string name = Path.GetFileName(file);
            string code = StripComments(File.ReadAllText(file));
            foreach (Match match in QualifiedFenEngineName().Matches(code))
            {
                // A qualified name ends in type or member names; the dependency is
                // the longest prefix that is a declared FenEngine namespace.
                string? ns = LongestNamespacePrefix(match.Groups["name"].Value, namespaces);
                if (ns == null) continue;
                if (!AllowedModuleDependencies.TryGetValue(ns, out string[]? files))
                {
                    violations.Add($"{name} depends on FenBrowser.{ns}");
                }
                else if (files != null && !files.Contains(name))
                {
                    violations.Add($"{name} depends on FenBrowser.{ns}, which only {string.Join(", ", files)} may use");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "The SVG module must not grow new FenEngine dependencies: " + string.Join("; ", violations.Distinct()));
    }

    [Fact]
    public void TheAllowListsNameThingsThatStillExist()
    {
        string root = FindRepositoryRoot();
        string[] moduleTypes = ModuleTypes(root);

        foreach (var (file, types) in AllowedConsumers)
        {
            Assert.True(File.Exists(Path.Combine(root, file)), $"{file} no longer exists; drop it from the list");
            foreach (string type in types)
            {
                Assert.Contains(type, moduleTypes);
            }
        }
    }

    private static HashSet<string> FenEngineNamespaces(string root) =>
        SourceFiles(root, "FenBrowser.FenEngine")
            .SelectMany(file => NamespaceDeclaration().Matches(File.ReadAllText(file))
                .Select(match => match.Groups["ns"].Value))
            .ToHashSet(StringComparer.Ordinal);

    private static string? LongestNamespacePrefix(string qualified, HashSet<string> namespaces)
    {
        for (string candidate = qualified; candidate.Length > 0;)
        {
            if (namespaces.Contains(candidate)) return candidate;
            int dot = candidate.LastIndexOf('.');
            if (dot < 0) return null;
            candidate = candidate[..dot];
        }
        return null;
    }

    private static string[] ModuleTypes(string root) =>
        SourceFiles(root, ModuleDirectory.TrimEnd('/'))
            .SelectMany(file => TypeDeclaration().Matches(StripComments(File.ReadAllText(file)))
                .Select(match => match.Groups["name"].Value))
            .Where(name => name.StartsWith("Svg", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<string> SourceFiles(string root, string relativeDirectory)
    {
        string directory = Path.Combine(root, relativeDirectory);
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                string relative = Relative(root, file);
                return !relative.Contains("/bin/", StringComparison.Ordinal) &&
                       !relative.Contains("/obj/", StringComparison.Ordinal);
            });
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string StripComments(string code) =>
        Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FenBrowser.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    // Top-level declarations only: nested types are reachable only through their owner.
    [GeneratedRegex(@"^(?: {4})?(?:(?:public|internal|static|sealed|abstract|partial|readonly)\s+)*(?:record\s+(?:struct|class)|class|struct|enum|record|interface)\s+(?<name>[A-Z][A-Za-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"\bFenBrowser\.(?<name>FenEngine(?:\.[A-Za-z_][A-Za-z0-9_]*)*)")]
    private static partial Regex QualifiedFenEngineName();

    [GeneratedRegex(@"^\s*namespace\s+FenBrowser\.(?<ns>FenEngine(?:\.[A-Za-z0-9_]+)*)", RegexOptions.Multiline)]
    private static partial Regex NamespaceDeclaration();
}
