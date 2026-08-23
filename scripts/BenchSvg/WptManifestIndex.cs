using System.Text.Json;

internal sealed record WptReference(
    string ManifestUrl,
    bool IsMatch,
    string? FullPath,
    bool IsBlank,
    int MaximumChannelDifference,
    int MaximumDifferingPixels)
{
    public bool IsResolved => IsBlank || FullPath != null;
}

internal sealed class WptManifestIndex
{
    private static readonly Uri WptOrigin = new("https://wpt.local/");
    private readonly string _wptRoot;
    private readonly Dictionary<string, WptManifestEntry> _entriesByPath;

    private WptManifestIndex(string wptRoot, Dictionary<string, WptManifestEntry> entriesByPath)
    {
        _wptRoot = wptRoot;
        _entriesByPath = entriesByPath;
    }

    public static WptManifestIndex LoadRequired(string corpusDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(corpusDirectory));
        while (current != null && !File.Exists(Path.Combine(current.FullName, "MANIFEST.json")))
            current = current.Parent;
        if (current == null)
            throw new ArgumentException("WPT corpus must be inside a checkout containing MANIFEST.json");

        try
        {
            string manifestPath = Path.Combine(current.FullName, "MANIFEST.json");
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                MaxDepth = 256,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("WPT manifest does not contain an items object");

            var index = new Dictionary<string, WptManifestEntry>(StringComparer.Ordinal);
            foreach (var typeProperty in items.EnumerateObject())
            {
                if (typeProperty.Value.ValueKind != JsonValueKind.Object ||
                    !typeProperty.Value.TryGetProperty("svg", out var svgTree))
                    continue;
                Walk(svgTree, "svg", typeProperty.Name, current.FullName, index);
            }
            return new WptManifestIndex(current.FullName, index);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            throw new ArgumentException($"invalid WPT manifest: {Bound(ex.Message)}");
        }
    }

    public string Classify(string fullPath)
    {
        string relative = ToRelativePath(fullPath);
        return _entriesByPath.TryGetValue(relative, out var entry) ? entry.Type : "unclassified";
    }

    public IReadOnlyList<WptReference> GetReferences(string fullPath)
    {
        string relative = ToRelativePath(fullPath);
        return _entriesByPath.TryGetValue(relative, out var entry)
            ? entry.References
            : Array.Empty<WptReference>();
    }

    public string RootDirectory => _wptRoot;

    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(_wptRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static void Walk(
        JsonElement node,
        string prefix,
        string type,
        string wptRoot,
        Dictionary<string, WptManifestEntry> index)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        foreach (var property in node.EnumerateObject())
        {
            string path = prefix + "/" + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Array)
                index.TryAdd(path, ParseEntry(path, type, wptRoot, property.Value));
            else
                Walk(property.Value, path, type, wptRoot, index);
        }
    }

    private static WptManifestEntry ParseEntry(
        string path,
        string type,
        string wptRoot,
        JsonElement leaf)
    {
        var references = new List<WptReference>();
        foreach (var variant in leaf.EnumerateArray().Skip(1))
        {
            if (variant.ValueKind != JsonValueKind.Array || variant.GetArrayLength() < 2 ||
                variant[0].ValueKind != JsonValueKind.String ||
                variant[1].ValueKind != JsonValueKind.Array)
                continue;

            string? testUrl = variant[0].GetString();
            if (!UrlMatchesPath(testUrl, path)) continue;
            foreach (var reference in variant[1].EnumerateArray())
            {
                if (reference.ValueKind != JsonValueKind.Array || reference.GetArrayLength() < 2 ||
                    reference[0].ValueKind != JsonValueKind.String ||
                    reference[1].ValueKind != JsonValueKind.String)
                    continue;
                string raw = reference[0].GetString() ?? string.Empty;
                string relation = reference[1].GetString() ?? string.Empty;
                if (relation is not ("==" or "!=")) continue;
                ReadFuzzyThresholds(
                    variant.GetArrayLength() >= 3 ? variant[2] : default,
                    raw,
                    out int maximumChannelDifference,
                    out int maximumDifferingPixels);
                references.Add(ResolveReference(
                    wptRoot,
                    testUrl!,
                    raw,
                    relation == "==",
                    maximumChannelDifference,
                    maximumDifferingPixels));
            }
        }
        return new WptManifestEntry(type, references
            .DistinctBy(item => (
                item.ManifestUrl,
                item.IsMatch,
                item.MaximumChannelDifference,
                item.MaximumDifferingPixels))
            .ToArray());
    }

    private static bool UrlMatchesPath(string? testUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(testUrl)) return false;
        if (testUrl.IndexOfAny(['?', '#']) >= 0) return false;
        string normalized = testUrl.TrimStart('/');
        return normalized.Equals(path, StringComparison.Ordinal);
    }

    private static void ReadFuzzyThresholds(
        JsonElement metadata,
        string referenceUrl,
        out int maximumChannelDifference,
        out int maximumDifferingPixels)
    {
        maximumChannelDifference = 0;
        maximumDifferingPixels = 0;
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("fuzzy", out var fuzzy) ||
            fuzzy.ValueKind != JsonValueKind.Array)
            return;

        foreach (var rule in fuzzy.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Array || rule.GetArrayLength() < 2 ||
                rule[1].ValueKind != JsonValueKind.Array || rule[1].GetArrayLength() < 2)
                continue;
            bool applies = rule[0].ValueKind == JsonValueKind.Null ||
                           (rule[0].ValueKind == JsonValueKind.String &&
                            rule[0].GetString()?.Equals(referenceUrl, StringComparison.Ordinal) == true);
            if (!applies) continue;
            if (TryReadMaximum(rule[1][0], out int channel) &&
                TryReadMaximum(rule[1][1], out int pixels))
            {
                maximumChannelDifference = Math.Max(maximumChannelDifference, channel);
                maximumDifferingPixels = Math.Max(maximumDifferingPixels, pixels);
            }
        }
    }

    private static bool TryReadMaximum(JsonElement range, out int maximum)
    {
        maximum = 0;
        return range.ValueKind == JsonValueKind.Array && range.GetArrayLength() >= 2 &&
               range[1].TryGetInt32(out maximum) && maximum >= 0;
    }

    private static WptReference ResolveReference(
        string wptRoot,
        string testUrl,
        string raw,
        bool isMatch,
        int maximumChannelDifference,
        int maximumDifferingPixels)
    {
        if (raw.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return new WptReference(
                raw, isMatch, null, IsBlank: true,
                maximumChannelDifference, maximumDifferingPixels);

        try
        {
            Uri testUri = new(WptOrigin, testUrl.TrimStart('/'));
            Uri resolved = new(testUri, raw);
            if (resolved.Scheme != WptOrigin.Scheme || resolved.Host != WptOrigin.Host ||
                resolved.Port != WptOrigin.Port || !string.IsNullOrEmpty(resolved.UserInfo) ||
                !string.IsNullOrEmpty(resolved.Query) || !string.IsNullOrEmpty(resolved.Fragment))
                return Unresolved();

            string decoded = Uri.UnescapeDataString(resolved.AbsolutePath).TrimStart('/');
            if (decoded.IndexOfAny(['\\', '\0']) >= 0 ||
                !Path.GetExtension(decoded).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                return Unresolved();

            string fullPath = Path.GetFullPath(Path.Combine(
                wptRoot, decoded.Replace('/', Path.DirectorySeparatorChar)));
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(wptRoot));
            string rootPrefix = root + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullPath.StartsWith(rootPrefix, comparison) || !File.Exists(fullPath))
                return Unresolved();

            _ = new LocalWptSvgResourceResolver(root).CreateDocumentUri(fullPath);
            return new WptReference(
                raw, isMatch, fullPath, IsBlank: false,
                maximumChannelDifference, maximumDifferingPixels);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or
                                   NotSupportedException or PathTooLongException)
        {
            return Unresolved();
        }

        WptReference Unresolved() => new(
            raw, isMatch, null, IsBlank: false,
            maximumChannelDifference, maximumDifferingPixels);
    }

    private static string Bound(string value) => value[..Math.Min(256, value.Length)];

    private sealed record WptManifestEntry(string Type, IReadOnlyList<WptReference> References);
}
