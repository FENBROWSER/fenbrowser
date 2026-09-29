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

internal sealed record WptDocumentClassification(
    string Type,
    IReadOnlyList<WptReference> References,
    bool IsReferenceTarget,
    bool IsSupportFile)
{
    public static WptDocumentClassification Unclassified { get; } = new(
        WptManifestIndex.UnclassifiedType, Array.Empty<WptReference>(),
        IsReferenceTarget: false, IsSupportFile: false);

    public bool IsExcludedFromTestDenominator =>
        IsSupportFile || (IsReferenceTarget && References.Count == 0);
}

internal sealed class WptManifestIndex
{
    internal const int SupportedManifestVersion = 9;
    internal const string UnclassifiedType = "unclassified";
    private const string SupportType = "support";
    private static readonly Uri WptOrigin = new("https://wpt.local/");
    private readonly string _wptRoot;
    private readonly string _manifestPath;
    private readonly int _manifestVersion;
    private readonly Dictionary<string, WptManifestEntry> _entriesByPath;
    private readonly HashSet<string> _referenceTargetPaths;

    private WptManifestIndex(
        string wptRoot,
        string manifestPath,
        int manifestVersion,
        Dictionary<string, WptManifestEntry> entriesByPath,
        HashSet<string> referenceTargetPaths)
    {
        _wptRoot = wptRoot;
        _manifestPath = manifestPath;
        _manifestVersion = manifestVersion;
        _entriesByPath = entriesByPath;
        _referenceTargetPaths = referenceTargetPaths;
    }

    public static WptManifestIndex LoadRequired(string corpusDirectory)
    {
        string corpus = Path.GetFullPath(corpusDirectory);
        if (!Directory.Exists(corpus))
            throw new ArgumentException($"WPT corpus directory does not exist: {corpus}");

        var current = new DirectoryInfo(corpus);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "MANIFEST.json")))
            current = current.Parent;
        if (current == null)
            throw new ArgumentException("WPT corpus must be inside a checkout containing MANIFEST.json");

        string wptRoot = current.FullName;
        string manifestPath = Path.Combine(wptRoot, "MANIFEST.json");
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                MaxDepth = 256,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("WPT manifest root must be an object");
            if (!root.TryGetProperty("version", out var versionProperty) ||
                versionProperty.ValueKind != JsonValueKind.Number ||
                !versionProperty.TryGetInt32(out int manifestVersion))
                throw new InvalidDataException("WPT manifest does not declare a version");
            if (manifestVersion != SupportedManifestVersion)
                throw new InvalidDataException(
                    $"WPT manifest version {manifestVersion} is not supported (expected {SupportedManifestVersion})");
            if (root.TryGetProperty("url_base", out var urlBase) &&
                (urlBase.ValueKind != JsonValueKind.String ||
                 urlBase.GetString() != "/"))
                throw new InvalidDataException("WPT manifest declares an unsupported url_base");
            if (!root.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("WPT manifest does not contain an items object");

            var index = new Dictionary<string, WptManifestEntry>(StringComparer.Ordinal);
            var types = new List<string>();
            foreach (var typeProperty in items.EnumerateObject())
            {
                if (typeProperty.Value.ValueKind == JsonValueKind.Object &&
                    typeProperty.Value.TryGetProperty("svg", out var svgTree) &&
                    svgTree.ValueKind == JsonValueKind.Object &&
                    typeProperty.Name.Length != 0 &&
                    typeProperty.Name.IndexOf('/') < 0)
                    types.Add(typeProperty.Name);
            }
            types.Sort(StringComparer.Ordinal);
            foreach (string type in types)
                Walk(items.GetProperty(type).GetProperty("svg"), "svg", type, wptRoot, index);

            var referenceTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in index.Values)
                foreach (string target in entry.ReferenceTargets)
                    referenceTargets.Add(target);
            foreach (string target in referenceTargets)
                if (!index.ContainsKey(target))
                    index[target] = new WptManifestEntry(
                        SupportType, Array.Empty<WptReference>(), Array.Empty<string>());
            return new WptManifestIndex(wptRoot, manifestPath, manifestVersion, index, referenceTargets);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            throw new ArgumentException($"invalid WPT manifest: {Bound(ex.Message)}");
        }
    }

    public string RootDirectory => _wptRoot;

    public string ManifestPath => _manifestPath;

    public int ManifestVersion => _manifestVersion;

    public int IndexedItemCount => _entriesByPath.Count;

    public WptDocumentClassification Classify(string fullPath)
    {
        string relative = ToRelativePath(fullPath);
        if (!_entriesByPath.TryGetValue(relative, out var entry)) return WptDocumentClassification.Unclassified;
        return new WptDocumentClassification(
            entry.Type,
            entry.References,
            _referenceTargetPaths.Contains(relative),
            entry.Type == SupportType);
    }

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
            if (property.Name.Length == 0) continue;
            string path = prefix + "/" + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Walk(property.Value, path, type, wptRoot, index);
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Array) continue;
            if (property.Value.GetArrayLength() == 0 ||
                property.Value[0].ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(property.Value[0].GetString()))
                throw new InvalidDataException($"WPT manifest item has no content hash: {path}");

            var references = new List<WptReference>();
            var targets = new List<string>();
            ParseEntry(path, wptRoot, property.Value, references, targets);
            var entry = new WptManifestEntry(
                type,
                references.DistinctBy(reference => (
                    reference.ManifestUrl,
                    reference.IsMatch,
                    reference.MaximumChannelDifference,
                    reference.MaximumDifferingPixels)).ToArray(),
                targets.Distinct(StringComparer.Ordinal).ToArray());
            if (index.TryGetValue(path, out var existing))
            {
                if (entry.References.Count > existing.References.Count) index[path] = entry;
                continue;
            }
            index.Add(path, entry);
        }
    }

    private static void ParseEntry(
        string path,
        string wptRoot,
        JsonElement leaf,
        List<WptReference> references,
        List<string> referenceTargets)
    {
        int count = leaf.GetArrayLength();
        for (int index = 1; index < count; index++)
        {
            var variant = leaf[index];
            if (variant.ValueKind != JsonValueKind.Array || variant.GetArrayLength() < 2) continue;
            if (variant[1].ValueKind != JsonValueKind.Array) continue;
            if (!VariantAppliesToDocument(variant[0], path)) continue;
            var metadata = variant.GetArrayLength() >= 3 ? variant[2] : default;

            foreach (var reference in variant[1].EnumerateArray())
            {
                if (reference.ValueKind != JsonValueKind.Array || reference.GetArrayLength() < 2 ||
                    reference[0].ValueKind != JsonValueKind.String ||
                    reference[1].ValueKind != JsonValueKind.String)
                    continue;
                string raw = reference[0].GetString() ?? string.Empty;
                string relation = reference[1].GetString() ?? string.Empty;
                if (relation is not ("==" or "!=")) continue;
                if (raw.Length == 0) continue;
                bool isMatch = relation == "==";
                ReadFuzzyThresholds(metadata, raw, isMatch, out int channel, out int pixels);
                if (raw.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    references.Add(new WptReference(
                        raw, isMatch, null, IsBlank: true, channel, pixels));
                    continue;
                }
                string? resolved = ResolveReferencePath(wptRoot, path, raw, out string? relativeTarget);
                if (relativeTarget != null) referenceTargets.Add(relativeTarget);
                references.Add(new WptReference(
                    raw, isMatch, resolved, IsBlank: false, channel, pixels));
            }
        }
    }

    private static bool VariantAppliesToDocument(JsonElement url, string path)
    {
        if (url.ValueKind == JsonValueKind.Null) return true;
        if (url.ValueKind != JsonValueKind.String) return false;
        string? raw = url.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (raw.IndexOfAny(['?', '#']) >= 0) return false;
        return raw.TrimStart('/').Equals(path, StringComparison.Ordinal);
    }

    private static void ReadFuzzyThresholds(
        JsonElement metadata,
        string referenceUrl,
        bool isMatch,
        out int maximumChannelDifference,
        out int maximumDifferingPixels)
    {
        maximumChannelDifference = 0;
        maximumDifferingPixels = 0;
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("fuzzy", out var fuzzy) ||
            fuzzy.ValueKind != JsonValueKind.Array)
            return;

        int globalChannel = 0;
        int globalPixels = 0;
        int scopedChannel = 0;
        int scopedPixels = 0;
        bool hasGlobal = false;
        bool hasScoped = false;
        foreach (var rule in fuzzy.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Array || rule.GetArrayLength() < 2 ||
                rule[1].ValueKind != JsonValueKind.Array || rule[1].GetArrayLength() < 2)
                continue;
            if (!TryReadMaximum(rule[1][0], out int channel) ||
                !TryReadMaximum(rule[1][1], out int pixels))
                continue;
            if (!FuzzyRuleApplies(rule[0], referenceUrl, isMatch)) continue;
            if (FuzzyRuleIsReferenceScoped(rule[0]))
            {
                hasScoped = true;
                scopedChannel = Math.Max(scopedChannel, channel);
                scopedPixels = Math.Max(scopedPixels, pixels);
            }
            else
            {
                hasGlobal = true;
                globalChannel = Math.Max(globalChannel, channel);
                globalPixels = Math.Max(globalPixels, pixels);
            }
        }

        if (hasScoped)
        {
            maximumChannelDifference = scopedChannel;
            maximumDifferingPixels = scopedPixels;
        }
        else if (hasGlobal)
        {
            maximumChannelDifference = globalChannel;
            maximumDifferingPixels = globalPixels;
        }
    }

    private static bool FuzzyRuleApplies(JsonElement key, string referenceUrl, bool isMatch)
    {
        if (key.ValueKind == JsonValueKind.Null) return true;
        if (key.ValueKind == JsonValueKind.String)
            return key.GetString()?.Equals(referenceUrl, StringComparison.Ordinal) == true;
        if (key.ValueKind != JsonValueKind.Array || key.GetArrayLength() < 3) return false;
        if (key[1].ValueKind != JsonValueKind.String || key[2].ValueKind != JsonValueKind.String) return false;
        if (key[1].GetString()?.Equals(referenceUrl, StringComparison.Ordinal) != true) return false;
        string? relation = key[2].GetString();
        return relation == null || (isMatch ? relation == "==" : relation == "!=");
    }

    private static bool FuzzyRuleIsReferenceScoped(JsonElement key) => key.ValueKind != JsonValueKind.Null;

    private static bool TryReadMaximum(JsonElement range, out int maximum)
    {
        maximum = 0;
        return range.ValueKind == JsonValueKind.Array && range.GetArrayLength() >= 2 &&
               range[1].TryGetInt32(out maximum) && maximum >= 0;
    }

    private static string? ResolveReferencePath(
        string wptRoot,
        string testPath,
        string raw,
        out string? relativeTarget)
    {
        relativeTarget = null;
        try
        {
            Uri testUri = new(WptOrigin, testPath);
            if (!Uri.TryCreate(testUri, raw, out var resolved) || resolved == null) return null;
            if (resolved.Scheme != WptOrigin.Scheme || resolved.Host != WptOrigin.Host ||
                resolved.Port != WptOrigin.Port || !string.IsNullOrEmpty(resolved.UserInfo) ||
                !string.IsNullOrEmpty(resolved.Query) || !string.IsNullOrEmpty(resolved.Fragment))
                return null;

            string decoded = Uri.UnescapeDataString(resolved.AbsolutePath).TrimStart('/');
            if (decoded.Length == 0 || decoded.IndexOfAny(['\\', '\0']) >= 0 ||
                !Path.GetExtension(decoded).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                return null;

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(wptRoot));
            string fullPath = Path.GetFullPath(Path.Combine(
                root, decoded.Replace('/', Path.DirectorySeparatorChar)));
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
                !File.Exists(fullPath))
                return null;
            _ = new LocalWptSvgResourceResolver(root).CreateDocumentUri(fullPath);
            relativeTarget = decoded;
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or
                                   NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string Bound(string value) => value[..Math.Min(256, value.Length)];

    private sealed record WptManifestEntry(
        string Type,
        IReadOnlyList<WptReference> References,
        IReadOnlyList<string> ReferenceTargets);
}
