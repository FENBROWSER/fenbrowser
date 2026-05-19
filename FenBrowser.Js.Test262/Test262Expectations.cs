using System.Text.Json;
using System.Text.RegularExpressions;

namespace FenBrowser.Js.Test262;

public sealed class Test262Expectations
{
    public string? MetadataOwner { get; init; }
    public string? MetadataArea { get; init; }
    public string? MetadataCommit { get; init; }
    public IReadOnlyList<Test262ExpectationEntry> Entries { get; init; } = Array.Empty<Test262ExpectationEntry>();

    public static Test262Expectations Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Expectations file not found: {path}", path);
        }

        using var stream = File.OpenRead(path);
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
            foreach (var item in expectations.EnumerateArray())
            {
                var pattern = TryGetString(item, "path");
                var status = TryGetString(item, "status");
                var reason = TryGetString(item, "reason");
                var expires = TryGetString(item, "expiresAtMilestone");
                var itemOwner = TryGetString(item, "owner") ?? owner ?? "unknown";
                var itemArea = TryGetString(item, "area") ?? area ?? "unknown";

                if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(status))
                {
                    continue;
                }

                entries.Add(new Test262ExpectationEntry(
                    pattern,
                    status,
                    reason ?? string.Empty,
                    expires ?? string.Empty,
                    itemOwner,
                    itemArea));
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

    private static Regex BuildRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var pattern = "^" + Regex.Escape(normalized).Replace("\\*", ".*") + "$";
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}
