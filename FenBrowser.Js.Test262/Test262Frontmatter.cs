namespace FenBrowser.Js.Test262;

public sealed record Test262NegativeMetadata(string? Phase, string? Type);

public sealed record Test262FrontmatterMetadata(
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> Includes,
    Test262NegativeMetadata? Negative,
    string? Esid,
    string? Description,
    string? Info,
    string? Locale);

public static class Test262Frontmatter
{
    public static Test262FrontmatterMetadata Parse(string source)
    {
        var start = source.IndexOf("/*---", StringComparison.Ordinal);
        if (start < 0)
        {
            return Empty();
        }

        var end = source.IndexOf("---*/", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return Empty();
        }

        var block = source[(start + 5)..end];
        var lines = block.Split('\n');

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var multi = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? currentMulti = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (currentMulti is not null)
            {
                if (line.StartsWith("  ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal))
                {
                    multi[currentMulti].Add(trimmed);
                    continue;
                }

                currentMulti = null;
            }

            var idx = trimmed.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim();
            if (string.Equals(key, "negative", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "info", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
            {
                currentMulti = key;
                if (!multi.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    multi[key] = list;
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (value != "|" && value != ">")
                    {
                        list.Add(value);
                    }
                }

                continue;
            }

            values[key] = value;
        }

        var features = ParseInlineList(values, "features");
        var flags = ParseInlineList(values, "flags");
        var includes = ParseInlineList(values, "includes");
        var negative = ParseNegative(values, multi);
        var info = ParseMulti(values, multi, "info");
        var description = ParseMulti(values, multi, "description") ?? TryGet(values, "description");

        return new Test262FrontmatterMetadata(
            features,
            flags,
            includes,
            negative,
            TryGet(values, "esid"),
            description,
            info,
            TryGet(values, "locale"));
    }

    private static Test262FrontmatterMetadata Empty() =>
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, null, null, null, null);

    private static IReadOnlyList<string> ParseInlineList(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var value = raw.Trim();
        if (value.StartsWith('[') && value.EndsWith(']') && value.Length >= 2)
        {
            value = value[1..^1];
        }

        if (value.Length == 0)
        {
            return Array.Empty<string>();
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Trim('\'', '"'))
            .Where(s => s.Length > 0)
            .ToArray();
    }

    private static Test262NegativeMetadata? ParseNegative(Dictionary<string, string> values, Dictionary<string, List<string>> multi)
    {
        if (values.TryGetValue("negative", out var inlineNegative))
        {
            if (string.IsNullOrWhiteSpace(inlineNegative))
            {
                return null;
            }

            return new Test262NegativeMetadata(null, inlineNegative.Trim());
        }

        if (!multi.TryGetValue("negative", out var lines) || lines.Count == 0)
        {
            return null;
        }

        string? phase = null;
        string? type = null;
        foreach (var line in lines)
        {
            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key.Equals("phase", StringComparison.OrdinalIgnoreCase))
            {
                phase = value;
            }
            else if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                type = value;
            }
        }

        if (phase is null && type is null)
        {
            return null;
        }

        return new Test262NegativeMetadata(phase, type);
    }

    private static string? ParseMulti(Dictionary<string, string> values, Dictionary<string, List<string>> multi, string key)
    {
        if (values.TryGetValue(key, out var inline) && !string.IsNullOrWhiteSpace(inline) && inline != "|" && inline != ">")
        {
            return inline;
        }

        if (!multi.TryGetValue(key, out var lines) || lines.Count == 0)
        {
            return null;
        }

        return string.Join('\n', lines);
    }

    private static string? TryGet(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;
}
