namespace FenBrowser.Js.Test262;

public sealed class Test262Frontmatter
{
    public static Dictionary<string, string> Parse(string source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = source.IndexOf("/*---", StringComparison.Ordinal);
        if (start < 0)
        {
            return result;
        }

        var end = source.IndexOf("---*/", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return result;
        }

        var block = source[(start + 5)..end];
        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            result[key] = value;
        }

        return result;
    }
}
