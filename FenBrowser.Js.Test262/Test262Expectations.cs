using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace FenBrowser.Js.Test262;

public sealed class Test262Expectations
{
    private static readonly System.Text.RegularExpressions.Regex MilestonePattern = new("^M\\d+(?:\\.\\d+)?$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ParserError",
        "UnsupportedFeature",
        "RuntimeError",
        "Crash",
        "Timeout"
    };

    public string? MetadataOwner { get; init; }
    public string? MetadataArea { get; init; }
    public string? MetadataCommit { get; init; }
    public IReadOnlyList<Test262ExpectationEntry> Entries { get; init; } = Array.Empty<Test262ExpectationEntry>();

    public static Test262Expectations Load(string path)
    {
        if (Directory.Exists(path))
        {
            var fromDirectory = LoadDirectory(path);
            if (fromDirectory.Entries.Count == 0)
            {
                throw new InvalidOperationException($"No valid expectation entries found in directory: {path}");
            }

            return fromDirectory;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Expectations file not found: {path}", path);
        }

        var fromFile = LoadFile(path);
        if (fromFile.Entries.Count == 0)
        {
            throw new InvalidOperationException($"No valid expectation entries found in file: {path}");
        }

        return fromFile;
    }

    private static Test262Expectations LoadDirectory(string directoryPath)
    {
        var files = Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mergedEntries = new List<Test262ExpectationEntry>();
        string? owner = null;
        string? area = null;
        string? commit = null;

        foreach (var file in files)
        {
            var single = LoadFile(file);
            if (!string.IsNullOrWhiteSpace(single.MetadataOwner))
            {
                if (owner is null)
                {
                    owner = single.MetadataOwner;
                }
                else if (!string.Equals(owner, single.MetadataOwner, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Conflicting expectation metadata 'owner' in directory '{directoryPath}': '{owner}' vs '{single.MetadataOwner}' ({file}).");
                }
            }

            if (!string.IsNullOrWhiteSpace(single.MetadataArea))
            {
                if (area is null)
                {
                    area = single.MetadataArea;
                }
                else if (!string.Equals(area, single.MetadataArea, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Conflicting expectation metadata 'area' in directory '{directoryPath}': '{area}' vs '{single.MetadataArea}' ({file}).");
                }
            }

            if (!string.IsNullOrWhiteSpace(single.MetadataCommit))
            {
                if (commit is null)
                {
                    commit = single.MetadataCommit;
                }
                else if (!string.Equals(commit, single.MetadataCommit, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Conflicting expectation metadata 'test262Commit' in directory '{directoryPath}': '{commit}' vs '{single.MetadataCommit}' ({file}).");
                }
            }

            mergedEntries.AddRange(single.Entries);
        }

        return new Test262Expectations
        {
            MetadataOwner = owner,
            MetadataArea = area,
            MetadataCommit = commit,
            Entries = mergedEntries
        };
    }

    private static Test262Expectations LoadFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        string? owner = null;
        string? area = null;
        string? commit = null;
        if (root.TryGetProperty("metadata", out var metadata))
        {
            owner = TryGetString(metadata, "owner");
            area = TryGetString(metadata, "area");
            commit = TryGetString(metadata, "test262Commit");
        }

        var entries = new List<Test262ExpectationEntry>();
        if (root.TryGetProperty("expectations", out var expectations) && expectations.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in expectations.EnumerateArray())
            {
                var pattern = TryGetString(item, "path");
                var status = TryGetString(item, "status");
                var reason = TryGetString(item, "reason");
                var expires = TryGetString(item, "expiresAtMilestone");
                var itemOwner = TryGetString(item, "owner") ?? owner ?? "unknown";
                var itemArea = TryGetString(item, "area") ?? area ?? "unknown";

                if (string.IsNullOrWhiteSpace(pattern))
                {
                    throw new InvalidDataException($"Expectation entry in {filePath} at index {index} is missing required 'path'.");
                }

                if (string.IsNullOrWhiteSpace(status))
                {
                    throw new InvalidDataException($"Expectation entry in {filePath} at index {index} is missing required 'status'.");
                }

                if (!ValidStatuses.Contains(status))
                {
                    throw new InvalidDataException($"Invalid expectation status '{status}' in {filePath} at index {index}.");
                }

                if (string.IsNullOrWhiteSpace(reason) ||
                    string.Equals(reason, "unknown", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(reason, "tbd", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Expectation entry in {filePath} at index {index} is missing required 'reason'.");
                }

                if (string.IsNullOrWhiteSpace(expires) ||
                    string.Equals(expires, "unknown", StringComparison.OrdinalIgnoreCase) ||
                    !MilestonePattern.IsMatch(expires))
                {
                    throw new InvalidDataException($"Expectation entry in {filePath} at index {index} has invalid 'expiresAtMilestone'.");
                }

                if (string.IsNullOrWhiteSpace(itemOwner) || string.Equals(itemOwner, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Expectation entry in {filePath} at index {index} is missing required 'owner'.");
                }

                if (string.IsNullOrWhiteSpace(itemArea) || string.Equals(itemArea, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Expectation entry in {filePath} at index {index} is missing required 'area'.");
                }

                entries.Add(new Test262ExpectationEntry(
                    pattern,
                    status,
                    reason,
                    expires,
                    itemOwner,
                    itemArea));
                index++;
            }
        }

        return new Test262Expectations
        {
            MetadataOwner = owner,
            MetadataArea = area,
            MetadataCommit = commit,
            Entries = entries
        };
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}

public sealed record Test262ExpectationEntry(
    string PathPattern,
    string Status,
    string Reason,
    string ExpiresAtMilestone,
    string Owner,
    string Area)
{
    public bool Matches(string relativePath, string status)
    {
        return string.Equals(Status, status, StringComparison.OrdinalIgnoreCase) &&
               BuildRegex(PathPattern).IsMatch(relativePath);
    }

    private static readonly ConcurrentDictionary<string, System.Text.RegularExpressions.Regex> RegexCache = new(StringComparer.Ordinal);

    private static System.Text.RegularExpressions.Regex BuildRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        return RegexCache.GetOrAdd(normalized, static key =>
        {
            var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(key).Replace("\\*", ".*") + "$";
            return new System.Text.RegularExpressions.Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        });
    }
}
