using System.Text.Json;
using FenBrowser.FenEngine.Testing;

namespace FenBrowser.WPT;

public sealed class WPTRegressionPack
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "dom";
    public string Description { get; set; } = string.Empty;
    public List<string> Selectors { get; set; } = new();

    public static WPTRegressionPack Load(string repoRoot, string packSelector)
    {
        var packPath = ResolvePackPath(repoRoot, packSelector);
        var json = File.ReadAllText(packPath);
        var pack = JsonSerializer.Deserialize<WPTRegressionPack>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (pack == null)
        {
            throw new InvalidOperationException($"Failed to deserialize regression pack '{packPath}'.");
        }

        if (string.IsNullOrWhiteSpace(pack.Name))
        {
            pack.Name = Path.GetFileNameWithoutExtension(packPath);
        }

        if (pack.Selectors.Count == 0)
        {
            throw new InvalidOperationException($"Regression pack '{pack.Name}' does not contain any selectors.");
        }

        return pack;
    }

    public IReadOnlyList<string> ResolveTests(string wptRootPath)
    {
        if (string.IsNullOrWhiteSpace(wptRootPath) || !Directory.Exists(wptRootPath))
        {
            throw new DirectoryNotFoundException($"WPT root path not found: {wptRootPath}");
        }

        var allTests = new List<string>();
        allTests.AddRange(Directory.GetFiles(wptRootPath, "*.html", SearchOption.AllDirectories));
        allTests.AddRange(Directory.GetFiles(wptRootPath, "*.htm", SearchOption.AllDirectories));
        allTests = allTests
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return !name.StartsWith("_", StringComparison.Ordinal) &&
                       !name.StartsWith(".", StringComparison.Ordinal);
            })
            .ToList();

        var byRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var absolutePath in allTests)
        {
            var relativePath = NormalizeSelector(Path.GetRelativePath(wptRootPath, absolutePath));
            byRelativePath[relativePath] = absolutePath;

            var fileName = Path.GetFileName(absolutePath);
            if (!byFileName.TryGetValue(fileName, out var matches))
            {
                matches = new List<string>();
                byFileName[fileName] = matches;
            }

            matches.Add(absolutePath);
        }

        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selector in Selectors)
        {
            var normalized = NormalizeSelector(selector);
            string matchedPath;
            if (normalized.Contains('/'))
            {
                if (!byRelativePath.TryGetValue(normalized, out matchedPath!))
                {
                    throw new InvalidOperationException(
                        $"Regression pack '{Name}' selector '{selector}' did not resolve to a WPT test file.");
                }
            }
            else
            {
                if (!byFileName.TryGetValue(normalized, out var matches) || matches.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Regression pack '{Name}' selector '{selector}' did not resolve to a WPT test file.");
                }

                if (matches.Count > 1)
                {
                    var duplicatePaths = string.Join(", ",
                        matches.Select(path => NormalizeSelector(Path.GetRelativePath(wptRootPath, path))).Take(5));
                    throw new InvalidOperationException(
                        $"Regression pack '{Name}' selector '{selector}' is ambiguous across multiple WPT files: {duplicatePaths}");
                }

                matchedPath = matches[0];
            }

            if (seen.Add(matchedPath))
            {
                resolved.Add(matchedPath);
            }
        }

        return resolved;
    }

    public HashSet<string> GetArtifactFileNames()
    {
        return Selectors
            .Select(selector => Path.GetFileName(NormalizeSelector(selector)))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public string CreateVersionedArtifactFileName(DateTime utcNow)
    {
        return $"wpt_{SanitizeFileSegment(Category)}_regression_{SanitizeFileSegment(Name)}_{utcNow:yyyyMMdd_HHmmss}.json";
    }

    public string CreateLatestArtifactFileName()
    {
        return $"wpt_{SanitizeFileSegment(Category)}_regression_{SanitizeFileSegment(Name)}_latest.json";
    }

    public static IReadOnlyList<string> ListBuiltInPackPaths(string repoRoot)
    {
        var packRoot = GetBuiltInPackRoot(repoRoot);
        if (!Directory.Exists(packRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(packRoot, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolvePackPath(string repoRoot, string packSelector)
    {
        if (Path.IsPathRooted(packSelector))
        {
            if (!File.Exists(packSelector))
            {
                throw new FileNotFoundException($"Regression pack not found: {packSelector}", packSelector);
            }

            return packSelector;
        }

        var repoRelative = Path.Combine(repoRoot, packSelector);
        if (File.Exists(repoRelative))
        {
            return repoRelative;
        }

        var builtInRoot = GetBuiltInPackRoot(repoRoot);
        var fileName = packSelector.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? packSelector
            : packSelector + ".json";
        var builtInPath = Path.Combine(builtInRoot, fileName);
        if (File.Exists(builtInPath))
        {
            return builtInPath;
        }

        throw new FileNotFoundException($"Regression pack not found: {packSelector}", builtInPath);
    }

    private static string GetBuiltInPackRoot(string repoRoot)
    {
        return Path.Combine(repoRoot, "FenBrowser.WPT", "RegressionPacks");
    }

    private static string NormalizeSelector(string value)
    {
        return value.Replace('\\', '/').Trim().TrimStart('/');
    }

    private static string SanitizeFileSegment(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return new string(chars).Trim('_');
    }
}

public static class WPTRegressionArtifactBuilder
{
    public static string BuildFilteredArtifact(string sourceArtifactPath, WPTRegressionPack pack)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(sourceArtifactPath));
        var root = document.RootElement;

        if (!root.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Source artifact '{sourceArtifactPath}' does not contain a results array.");
        }

        var selectedNames = pack.GetArtifactFileNames();
        var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filteredResults = new List<object>();

        foreach (var item in resultsElement.EnumerateArray())
        {
            var file = GetString(item, "file");
            if (string.IsNullOrWhiteSpace(file) || !selectedNames.Contains(file))
            {
                continue;
            }

            matchedNames.Add(file);
            filteredResults.Add(new
            {
                file,
                success = GetBool(item, "success"),
                harnessCompleted = GetBool(item, "harnessCompleted"),
                timedOut = GetBool(item, "timedOut"),
                completionSignal = GetString(item, "completionSignal"),
                passCount = GetInt(item, "passCount"),
                failCount = GetInt(item, "failCount"),
                totalCount = GetInt(item, "totalCount"),
                error = GetNullableString(item, "error"),
                durationMs = GetLong(item, "durationMs")
            });
        }

        var missing = selectedNames
            .Where(name => !matchedNames.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Source artifact '{sourceArtifactPath}' is missing regression-pack tests: {string.Join(", ", missing)}");
        }

        var total = filteredResults.Count;
        var passed = filteredResults.Count(item => GetProperty<bool>(item, "success"));
        var failed = total - passed;
        var timedOut = filteredResults.Count(item => GetProperty<bool>(item, "timedOut"));
        var totalAssertions = filteredResults.Sum(item => GetProperty<int>(item, "totalCount"));
        var passedAssertions = filteredResults.Sum(item => GetProperty<int>(item, "passCount"));
        var failedAssertions = filteredResults.Sum(item => GetProperty<int>(item, "failCount"));
        var durationMs = filteredResults.Sum(item => GetProperty<long>(item, "durationMs"));

        var artifact = new
        {
            timestamp = GetNullableString(root, "timestamp") ?? DateTime.UtcNow.ToString("o"),
            chunk = (int?)null,
            category = pack.Category,
            pack = pack.Name,
            packDescription = pack.Description,
            sourceArtifact = Path.GetFileName(sourceArtifactPath),
            total,
            passed,
            failed,
            timedOut,
            passRate = total > 0 ? (double)passed / total * 100 : 0,
            totalAssertions,
            passedAssertions,
            failedAssertions,
            durationMs,
            results = filteredResults
        };

        return JsonSerializer.Serialize(artifact, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string? GetNullableString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) &&
               value.GetBoolean();
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
    }

    private static long GetLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;
    }

    private static T GetProperty<T>(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(propertyName);
        if (property == null)
        {
            return default!;
        }

        return (T)(property.GetValue(source) ?? default(T)!);
    }
}
