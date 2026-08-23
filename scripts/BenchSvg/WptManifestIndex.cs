using System.Text.Json;

internal sealed class WptManifestIndex
{
    private readonly string _wptRoot;
    private readonly Dictionary<string, string> _typesByPath;

    private WptManifestIndex(string wptRoot, Dictionary<string, string> typesByPath)
    {
        _wptRoot = wptRoot;
        _typesByPath = typesByPath;
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

            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var typeProperty in items.EnumerateObject())
            {
                if (typeProperty.Value.ValueKind != JsonValueKind.Object ||
                    !typeProperty.Value.TryGetProperty("svg", out var svgTree))
                    continue;
                Walk(svgTree, "svg", typeProperty.Name, index);
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
        string relative = Path.GetRelativePath(_wptRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        return _typesByPath.TryGetValue(relative, out string? type) ? type : "unclassified";
    }

    public string RootDirectory => _wptRoot;

    private static void Walk(
        JsonElement node,
        string prefix,
        string type,
        Dictionary<string, string> index)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        foreach (var property in node.EnumerateObject())
        {
            string path = prefix + "/" + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Array)
                index.TryAdd(path, type);
            else
                Walk(property.Value, path, type, index);
        }
    }

    private static string Bound(string value) => value[..Math.Min(256, value.Length)];
}
