using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;

internal sealed record SvgCorpusOptions(
    string CorpusDirectory,
    string OutputDirectory,
    int MaxFiles,
    long MaxFileBytes,
    int PerFileTimeoutMs,
    string CorpusKind,
    string? ManifestPath,
    IReadOnlyList<string> IncludePrefixes,
    bool Resume,
    bool Gate)
{
    private const int DefaultMaxFiles = 500;
    private const int HardMaxFiles = 10_000;
    private const long DefaultMaxFileBytes = 8L * 1024 * 1024;
    private const long HardMaxFileBytes = 32L * 1024 * 1024;
    private const int DefaultPerFileTimeoutMs = 5_000;
    private const int HardPerFileTimeoutMs = 60_000;

    public static SvgCorpusOptions Parse(string[] args, int directoryIndex, string defaultOutputDirectory)
    {
        string corpus = Path.GetFullPath(args[directoryIndex]);
        if (!Directory.Exists(corpus)) throw new ArgumentException($"SVG corpus directory does not exist: {corpus}");

        int maxFiles = ReadInt(args, "--max-files", DefaultMaxFiles, 1, HardMaxFiles);
        long maxFileBytes = ReadLong(
            args, "--max-file-bytes", DefaultMaxFileBytes, 1, HardMaxFileBytes);
        int timeoutMs = ReadInt(
            args, "--per-file-timeout-ms", DefaultPerFileTimeoutMs, 100, HardPerFileTimeoutMs);
        string output = ReadString(args, "--output") ?? defaultOutputDirectory;
        string corpusKind = ReadString(args, "--corpus-kind") ?? "generic";
        if (corpusKind is not ("generic" or "wpt" or "captured-site"))
            throw new ArgumentException("--corpus-kind must be generic, wpt, or captured-site");
        string? manifest = ReadString(args, "--manifest");
        var includePrefixes = ReadPrefixes(args, "--include-prefix");
        if (corpusKind == "captured-site" && string.IsNullOrWhiteSpace(manifest))
            throw new ArgumentException("captured-site corpus requires --manifest");
        if (corpusKind == "captured-site" && includePrefixes.Count != 0)
            throw new ArgumentException("captured-site corpus cannot be filtered by --include-prefix");
        return new SvgCorpusOptions(
            corpus,
            Path.GetFullPath(output),
            maxFiles,
            maxFileBytes,
            timeoutMs,
            corpusKind,
            manifest == null ? null : Path.GetFullPath(manifest),
            includePrefixes,
            args.Contains("--resume", StringComparer.Ordinal),
            args.Contains("--gate", StringComparer.Ordinal));
    }

    private static string? ReadString(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{name} requires a value");
        return args[index + 1];
    }

    private static int ReadInt(string[] args, string name, int fallback, int min, int max)
    {
        string? raw = ReadString(args, name);
        if (raw == null) return fallback;
        if (!int.TryParse(raw, out int value) || value < min || value > max)
            throw new ArgumentException($"{name} must be between {min} and {max}");
        return value;
    }

    private static long ReadLong(string[] args, string name, long fallback, long min, long max)
    {
        string? raw = ReadString(args, name);
        if (raw == null) return fallback;
        if (!long.TryParse(raw, out long value) || value < min || value > max)
            throw new ArgumentException($"{name} must be between {min} and {max}");
        return value;
    }

    private static IReadOnlyList<string> ReadPrefixes(string[] args, string name)
    {
        var prefixes = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.Ordinal)) continue;
            if (++i >= args.Length) throw new ArgumentException($"{name} requires a value");
            string prefix = args[i].Replace('\\', '/').Trim('/');
            if (prefix.Length == 0 || Path.IsPathRooted(prefix) ||
                prefix.Split('/').Any(part => part is "" or "." or ".."))
                throw new ArgumentException($"{name} contains an invalid relative path prefix");
            if (!prefixes.Contains(prefix, StringComparer.Ordinal)) prefixes.Add(prefix);
        }
        return prefixes;
    }

    public bool Includes(string fullPath)
    {
        if (IncludePrefixes.Count == 0) return true;
        string relative = Path.GetRelativePath(CorpusDirectory, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        return IncludePrefixes.Any(prefix =>
            relative.Equals(prefix, StringComparison.Ordinal) ||
            relative.StartsWith(prefix + "/", StringComparison.Ordinal));
    }
}

internal static class SvgCorpusRunner
{
    private const double MinimumAlphaIntersectionOverUnion = 0.80;
    private const double MaximumMeanRgbDifference = 16.0;
    private static readonly Regex StopColorPattern = new(
        "stop-color\\s*(?:=|:)\\s*['\\\"]?(?<color>#[0-9a-fA-F]{3,8}|[a-zA-Z]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    public static int Run(
        SvgCorpusOptions options,
        ISvgRenderer firstParty,
        ISvgRenderer hybrid,
        ISvgRenderer legacy)
    {
        bool manifestValidated = false;
        Directory.CreateDirectory(options.OutputDirectory);
        string checkpointPath = Path.Combine(options.OutputDirectory, "corpus-checkpoint.jsonl");
        string rendererBuildId = typeof(SvgCorpusRunner).Assembly.ManifestModule.ModuleVersionId.ToString("D");
        var checkpoint = options.Resume
            ? LoadCheckpoint(checkpointPath, rendererBuildId)
            : new Dictionary<string, SvgCorpusCheckpoint>(StringComparer.Ordinal);
        RewriteCheckpoint(checkpointPath, checkpoint.Values);
        int reusedEntries = 0;
        WptManifestIndex? wptIndex = options.CorpusKind == "wpt"
            ? WptManifestIndex.LoadRequired(options.CorpusDirectory)
            : null;
        string[] allFiles;
        if (options.ManifestPath != null)
        {
            allFiles = LoadValidatedManifest(options, out manifestValidated);
        }
        else
        {
            allFiles = Directory.EnumerateFiles(
                options.CorpusDirectory,
                "*.svg",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseInsensitive
                })
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        }
        allFiles = allFiles.Where(options.Includes).ToArray();
        var selected = allFiles.Take(options.MaxFiles + 1).ToArray();
        bool selectionTruncated = selected.Length > options.MaxFiles;
        var files = selected.Take(options.MaxFiles).ToArray();

        var entries = new List<SvgCorpusEntry>(files.Length);
        var runClock = Stopwatch.StartNew();
        using var checkpointWriter = new StreamWriter(
            new FileStream(checkpointPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                16 * 1024, FileOptions.WriteThrough))
        { AutoFlush = true };
        for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            string file = files[fileIndex];
            string relative = Path.GetRelativePath(options.CorpusDirectory, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            var info = new FileInfo(file);
            if (info.Length > options.MaxFileBytes)
            {
                entries.Add(new SvgCorpusEntry
                {
                    Path = relative,
                    Bytes = info.Length,
                    Classification = "skipped-oversize",
                    WptType = wptIndex?.Classify(file)
                });
                continue;
            }

            string sourceSha256;
            try
            {
                using var sourceStream = File.OpenRead(file);
                sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceStream)).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entries.Add(new SvgCorpusEntry
                {
                    Path = relative,
                    Bytes = info.Length,
                    Classification = "read-failure",
                    FailureReasonCode = "source-read-failure",
                    Error = Bound(ex.Message),
                    WptType = wptIndex?.Classify(file)
                });
                continue;
            }

            SvgCorpusEntry entry;
            if (checkpoint.TryGetValue(relative, out var saved) && IsReusable(saved.Entry) &&
                saved.SourceSha256.Equals(sourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                entry = saved.Entry;
                reusedEntries++;
            }
            else
            {
                entry = EvaluateIsolated(
                    relative, info.Length, file, options.PerFileTimeoutMs,
                    wptIndex?.RootDirectory);
                entry.SourceSha256 = sourceSha256;
                if (IsReusable(entry)) checkpointWriter.WriteLine(JsonSerializer.Serialize(new SvgCorpusCheckpoint
                {
                    RendererBuildId = rendererBuildId,
                    SourceSha256 = sourceSha256,
                    Entry = entry
                }));
            }
            entry.WptType = wptIndex?.Classify(file);
            entries.Add(entry);
            int completed = fileIndex + 1;
            if (completed == files.Length || completed % 25 == 0)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    probe = "svg-corpus-progress",
                    completed,
                    total = files.Length,
                    elapsedMs = runClock.ElapsedMilliseconds,
                    latest = entry.Classification
                }));
            }
        }

        var summary = new SvgCorpusSummary
        {
            SchemaVersion = 2,
            CorpusRoot = options.CorpusDirectory,
            CorpusKind = options.CorpusKind,
            SelectionPrefixes = options.IncludePrefixes.ToList(),
            Resumed = options.Resume,
            ReusedEntries = reusedEntries,
            RendererBuildId = rendererBuildId,
            ManifestValidated = manifestValidated,
            SelectedFiles = files.Length,
            SelectionTruncated = selectionTruncated,
            EvaluatedFiles = entries.Count(entry => entry.Classification != "skipped-oversize" && entry.Classification != "read-failure"),
            FirstPartySupported = entries.Count(entry => entry.Classification == "first-party"),
            CompatibilityFallbacks = entries.Count(entry => entry.Classification == "legacy-fallback"),
            ResourceRejections = entries.Count(entry => entry.Classification == "resource-rejection"),
            FirstPartyFailures = entries.Count(entry => entry.Classification == "first-party-failure"),
            HybridFailures = entries.Count(entry => entry.Classification == "hybrid-failure"),
            WorkerTimeouts = entries.Count(entry => entry.Classification == "worker-timeout"),
            WorkerFailures = entries.Count(entry => entry.Classification == "worker-failure"),
            ReadFailures = entries.Count(entry => entry.Classification == "read-failure"),
            SkippedOversize = entries.Count(entry => entry.Classification == "skipped-oversize"),
            ComparablePairs = entries.Count(entry => entry.PixelComparable),
            ComparableParityPasses = entries.Count(entry => entry.PixelParity),
            AcceptedReferenceDefects = entries.Count(entry => entry.ReferenceDefect != null),
            Entries = entries,
            ReasonCounts = BuildReasonCounts(entries),
            WptTypeCounts = entries
                .Where(entry => entry.WptType != null)
                .GroupBy(entry => entry.WptType!, StringComparer.Ordinal)
                .Select(group => new SvgTypeCount { Type = group.Key, Count = group.Count() })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Type, StringComparer.Ordinal)
                .ToList()
        };

        Directory.CreateDirectory(options.OutputDirectory);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        WriteAllTextAtomic(
            Path.Combine(options.OutputDirectory, "corpus-report.json"),
            JsonSerializer.Serialize(summary, jsonOptions));
        WriteMarkdown(options.OutputDirectory, summary);

        bool routingOk = summary.SelectedFiles > 0 && summary.FirstPartyFailures == 0 &&
                         summary.HybridFailures == 0 && summary.WorkerTimeouts == 0 &&
                         summary.WorkerFailures == 0 && summary.ReadFailures == 0;
        bool parityOk = summary.ComparableParityPasses + summary.AcceptedReferenceDefects == summary.ComparablePairs;
        bool gateOk = routingOk && parityOk && summary.ResourceRejections == 0 &&
                      summary.CompatibilityFallbacks == 0 && summary.SkippedOversize == 0 &&
                      !summary.SelectionTruncated &&
                      (options.CorpusKind != "captured-site" || summary.ManifestValidated);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            probe = "svg-corpus",
            ok = options.Gate ? gateOk : routingOk,
            selected = summary.SelectedFiles,
            corpusKind = summary.CorpusKind,
            manifestValidated = summary.ManifestValidated,
            truncated = summary.SelectionTruncated,
            prefixes = summary.SelectionPrefixes,
            resumed = summary.Resumed,
            reused = summary.ReusedEntries,
            evaluated = summary.EvaluatedFiles,
            firstParty = summary.FirstPartySupported,
            fallback = summary.CompatibilityFallbacks,
            resourceRejections = summary.ResourceRejections,
            firstPartyFailures = summary.FirstPartyFailures,
            hybridFailures = summary.HybridFailures,
            workerTimeouts = summary.WorkerTimeouts,
            workerFailures = summary.WorkerFailures,
            parity = $"{summary.ComparableParityPasses}/{summary.ComparablePairs}",
            acceptedReferenceDefects = summary.AcceptedReferenceDefects,
            reasons = summary.ReasonCounts.Count,
            report = Path.Combine(options.OutputDirectory, "corpus-report.json")
        }));

        return options.Gate && !gateOk ? 1 : 0;
    }

    private static bool IsReusable(SvgCorpusEntry entry) =>
        entry.Classification is not ("worker-timeout" or "worker-failure" or "read-failure");

    private static Dictionary<string, SvgCorpusCheckpoint> LoadCheckpoint(
        string path,
        string rendererBuildId)
    {
        var entries = new Dictionary<string, SvgCorpusCheckpoint>(StringComparer.Ordinal);
        if (!File.Exists(path)) return entries;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<SvgCorpusCheckpoint>(line);
                if (item?.Entry == null || item.RendererBuildId != rendererBuildId ||
                    string.IsNullOrWhiteSpace(item.Entry.Path) || string.IsNullOrWhiteSpace(item.SourceSha256))
                    continue;
                entries[item.Entry.Path] = item;
            }
            catch (JsonException)
            {
                // A process interruption may leave one partial trailing record.
            }
        }
        return entries;
    }

    private static void RewriteCheckpoint(
        string path,
        IEnumerable<SvgCorpusCheckpoint> entries)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temporary, append: false))
            {
                foreach (var entry in entries.OrderBy(item => item.Entry.Path, StringComparer.Ordinal))
                    writer.WriteLine(JsonSerializer.Serialize(entry));
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string[] LoadValidatedManifest(
        SvgCorpusOptions options,
        out bool validated)
    {
        validated = false;
        if (!File.Exists(options.ManifestPath))
            throw new ArgumentException($"corpus manifest does not exist: {options.ManifestPath}");
        SvgCorpusManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SvgCorpusManifest>(File.ReadAllText(options.ManifestPath)) ??
                       throw new InvalidDataException("manifest is empty");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            throw new ArgumentException($"invalid corpus manifest: {Bound(ex.Message)}");
        }
        if (manifest.SchemaVersion != 1 || manifest.CorpusKind != options.CorpusKind || manifest.Files.Count == 0)
            throw new ArgumentException("manifest schema, corpus kind, or file inventory is invalid");
        if (options.CorpusKind == "captured-site")
        {
            if (manifest.CapturedAtUtc == default || manifest.CapturedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
                manifest.Sites.Count == 0 ||
                manifest.Sites.Select(site => site.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Sites.Count ||
                manifest.Sites.Any(site => site.Status != "captured" || site.AssetFailures != 0 ||
                                           site.SelectionTruncated || site.SvgCount < 0 ||
                                           !IsPublicProvenanceUri(site.SourceUrl) || !IsPublicProvenanceUri(site.FinalUrl)))
                throw new ArgumentException("captured-site provenance or capture status is incomplete");
        }

        var manifestPaths = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<string>(manifest.Files.Count);
        var siteIds = manifest.Sites.Select(site => site.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in manifest.Files)
        {
            string relative = (item.Path ?? string.Empty).Replace('\\', '/');
            if (relative.Length == 0 || Path.IsPathRooted(relative) ||
                relative.Split('/').Any(part => part == "..") ||
                !manifestPaths.Add(relative))
                throw new ArgumentException("manifest contains an invalid or duplicate relative path");
            string full = Path.GetFullPath(Path.Combine(options.CorpusDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            string roundTrip = Path.GetRelativePath(options.CorpusDirectory, full).Replace(Path.DirectorySeparatorChar, '/');
            if (!roundTrip.Equals(relative, StringComparison.Ordinal) || !File.Exists(full))
                throw new ArgumentException($"manifest file is missing or escapes corpus root: {relative}");
            using var stream = File.OpenRead(full);
            string digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!digest.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"manifest SHA-256 mismatch: {relative}");
            if (options.CorpusKind == "captured-site" &&
                (!siteIds.Contains(item.SiteId) || !IsPublicProvenanceUri(item.SourceUrl)))
                throw new ArgumentException($"manifest file provenance is invalid: {relative}");
            resolved.Add(full);
        }
        if (options.CorpusKind == "captured-site" && manifest.Sites.Any(site =>
                manifest.Files.Count(file => file.SiteId == site.Id) != site.SvgCount))
            throw new ArgumentException("captured-site manifest counts do not match its file inventory");

        var actual = Directory.EnumerateFiles(options.CorpusDirectory, "*.svg", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive
            })
            .Select(path => Path.GetRelativePath(options.CorpusDirectory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(manifestPaths))
            throw new ArgumentException("manifest inventory does not exactly match corpus SVG files");

        validated = true;
        return resolved.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static bool IsPublicProvenanceUri(string raw) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrWhiteSpace(uri.Host);

    public static int RunWorker(string inputPath, string outputPath, string? wptRoot)
    {
        try
        {
            var info = new FileInfo(inputPath);
            string source = File.ReadAllText(inputPath);
            LocalWptSvgResourceResolver? wptResolver = wptRoot == null
                ? null
                : new LocalWptSvgResourceResolver(wptRoot);
            var request = new SvgRenderRequest(source, SvgRenderLimits.Default)
            {
                BaseUri = wptResolver?.CreateDocumentUri(inputPath) ??
                          new Uri(Path.GetFullPath(inputPath)),
                ResourceResolver = wptResolver
            };
            ISvgRenderer firstParty = new FenSvgRenderer();
            ISvgRenderer legacy = new SvgSkiaRenderer();
            ISvgRenderer hybrid = new HybridSvgRenderer(firstParty, legacy);
            var entry = Evaluate(
                Path.GetFileName(inputPath), info.Length, request, firstParty, hybrid, legacy);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(entry));
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(new SvgCorpusEntry
                {
                    Path = Path.GetFileName(inputPath),
                    Classification = "worker-failure",
                    Error = Bound(ex.Message)
                }));
            }
            catch { }
            return 2;
        }
    }

    private static SvgCorpusEntry EvaluateIsolated(
        string relativePath,
        long bytes,
        string inputPath,
        int timeoutMs,
        string? wptRoot)
    {
        string outputPath = Path.Combine(Path.GetTempPath(), $"fen-svg-worker-{Guid.NewGuid():N}.json");
        try
        {
            using var process = new Process
            {
                StartInfo = CreateWorkerStartInfo(inputPath, outputPath, wptRoot)
            };
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit();
                return new SvgCorpusEntry
                {
                    Path = relativePath,
                    Bytes = bytes,
                    Classification = "worker-timeout",
                    Error = $"per-document wall timeout exceeded ({timeoutMs}ms)"
                };
            }
            if (!File.Exists(outputPath))
            {
                return new SvgCorpusEntry
                {
                    Path = relativePath,
                    Bytes = bytes,
                    Classification = "worker-failure",
                    Error = $"worker exited {process.ExitCode} without a result"
                };
            }
            var entry = JsonSerializer.Deserialize<SvgCorpusEntry>(File.ReadAllText(outputPath)) ??
                        new SvgCorpusEntry { Classification = "worker-failure", Error = "worker result was empty" };
            entry.Path = relativePath;
            entry.Bytes = bytes;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new SvgCorpusEntry
            {
                Path = relativePath,
                Bytes = bytes,
                Classification = "worker-failure",
                Error = Bound(ex.Message)
            };
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
    }

    private static ProcessStartInfo CreateWorkerStartInfo(
        string inputPath,
        string outputPath,
        string? wptRoot)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("process path unavailable");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string name = Path.GetFileNameWithoutExtension(executable);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SvgCorpusRunner).Assembly.Location);
        start.ArgumentList.Add("--corpus-worker");
        start.ArgumentList.Add(inputPath);
        start.ArgumentList.Add(outputPath);
        if (wptRoot != null)
        {
            start.ArgumentList.Add("--wpt-root");
            start.ArgumentList.Add(wptRoot);
        }
        return start;
    }

    private static SvgCorpusEntry Evaluate(
        string relativePath,
        long bytes,
        SvgRenderRequest request,
        ISvgRenderer firstParty,
        ISvgRenderer hybrid,
        ISvgRenderer legacy)
    {
        using var fen = TimedRender(firstParty, request);
        using var routed = TimedRender(hybrid, request);
        using var reference = TimedRender(legacy, request);

        string classification = !routed.Result.Success
            ? "hybrid-failure"
            : fen.Result.HadResourceRejection
                ? "resource-rejection"
            : fen.Result.Success && !fen.Result.RequiresFallback
                ? "first-party"
                : routed.Result.UsedLegacyFallback
                    ? "legacy-fallback"
                    : "first-party-failure";

        bool comparable = classification == "first-party" && reference.Result.Success &&
                          fen.Result.Bitmap != null && reference.Result.Bitmap != null;
        double alphaIou = 0;
        double meanRgbDifference = 0;
        long firstPartyForegroundPixels = 0;
        long legacyForegroundPixels = 0;
        bool parity = false;
        if (comparable)
        {
            ComparePixels(
                fen.Result.Bitmap!, reference.Result.Bitmap!,
                out alphaIou, out meanRgbDifference,
                out firstPartyForegroundPixels, out legacyForegroundPixels);
            parity = alphaIou >= MinimumAlphaIntersectionOverUnion &&
                     meanRgbDifference <= MaximumMeanRgbDifference;
        }
        string? referenceDefect = comparable && !parity &&
                                  IsLegacyChromaticGradientLoss(
                                      request.Content, fen.Result.Bitmap!, reference.Result.Bitmap!)
            ? "legacy-chromatic-gradient-loss"
            : null;

        return new SvgCorpusEntry
        {
            Path = relativePath,
            Bytes = bytes,
            Classification = classification,
            ProducingBackend = routed.Result.Backend.ToString(),
            FirstPartyMilliseconds = fen.ElapsedMilliseconds,
            HybridMilliseconds = routed.ElapsedMilliseconds,
            LegacyMilliseconds = reference.ElapsedMilliseconds,
            WarningCount = fen.Result.Warnings.Count,
            WarningMessages = fen.Result.Warnings.Take(8).Select(message => Bound(message) ?? string.Empty).ToList(),
            FallbackReasonCodes = fen.Result.FallbackReasonCodes.Distinct(StringComparer.Ordinal).ToList(),
            ResourceRejectionReasonCodes = fen.Result.ResourceRejectionReasonCodes.Distinct(StringComparer.Ordinal).ToList(),
            FailureReasonCode = classification is "first-party-failure" or "hybrid-failure"
                ? ClassifyFailure(routed.Result.ErrorMessage ?? fen.Result.ErrorMessage)
                : null,
            Error = Bound(routed.Result.ErrorMessage ?? fen.Result.ErrorMessage),
            PixelComparable = comparable,
            PixelParity = parity,
            ReferenceDefect = referenceDefect,
            AlphaIntersectionOverUnion = Math.Round(alphaIou, 4),
            MeanRgbDifference = Math.Round(meanRgbDifference, 4),
            FirstPartyForegroundPixels = firstPartyForegroundPixels,
            LegacyForegroundPixels = legacyForegroundPixels
        };
    }

    private static List<SvgReasonCount> BuildReasonCounts(IEnumerable<SvgCorpusEntry> entries)
    {
        var reasons = new Dictionary<(string Kind, string Code), int>();
        static void Add(Dictionary<(string Kind, string Code), int> target, string kind, string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            var key = (kind, code);
            target[key] = target.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        foreach (var entry in entries)
        {
            foreach (string code in entry.FallbackReasonCodes) Add(reasons, "fallback", code);
            foreach (string code in entry.ResourceRejectionReasonCodes) Add(reasons, "resource-rejection", code);
            Add(reasons, "failure", entry.FailureReasonCode);
            Add(reasons, "reference-defect", entry.ReferenceDefect);
            if (entry.Classification is "worker-timeout" or "worker-failure" or "read-failure" or "skipped-oversize")
                Add(reasons, "execution", entry.Classification);
        }
        return reasons
            .Select(pair => new SvgReasonCount
            {
                Kind = pair.Key.Kind,
                Code = pair.Key.Code,
                Count = pair.Value
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToList();
    }

    private static string ClassifyFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "render-failure";
        if (error.Contains("time", StringComparison.OrdinalIgnoreCase)) return "render-timeout";
        if (error.Contains("DOCTYPE", StringComparison.OrdinalIgnoreCase)) return "doctype-rejected";
        if (error.Contains("parse", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("markup", StringComparison.OrdinalIgnoreCase)) return "parse-failure";
        if (error.Contains("raster", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("pixel", StringComparison.OrdinalIgnoreCase)) return "raster-failure";
        if (error.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("budget", StringComparison.OrdinalIgnoreCase)) return "admission-failure";
        return "render-failure";
    }

    private static TimedSvgResult TimedRender(ISvgRenderer renderer, SvgRenderRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = renderer.Render(request);
        stopwatch.Stop();
        return new TimedSvgResult(result, stopwatch.ElapsedMilliseconds);
    }

    private static void ComparePixels(
        SKBitmap first,
        SKBitmap second,
        out double alphaIou,
        out double meanRgbDifference,
        out long firstForegroundPixels,
        out long secondForegroundPixels)
    {
        long intersection = 0;
        long union = 0;
        long rgbDifference = 0;
        long foregroundChannels = 0;
        firstForegroundPixels = 0;
        secondForegroundPixels = 0;
        int width = Math.Max(first.Width, second.Width);
        int height = Math.Max(first.Height, second.Height);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            SKColor a = x < first.Width && y < first.Height ? first.GetPixel(x, y) : SKColors.Transparent;
            SKColor b = x < second.Width && y < second.Height ? second.GetPixel(x, y) : SKColors.Transparent;
            bool aVisible = a.Alpha > 0;
            bool bVisible = b.Alpha > 0;
            if (aVisible) firstForegroundPixels++;
            if (bVisible) secondForegroundPixels++;
            if (aVisible && bVisible) intersection++;
            if (aVisible || bVisible)
            {
                union++;
                rgbDifference += Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
                foregroundChannels += 3;
            }
        }
        alphaIou = union == 0 ? 1.0 : (double)intersection / union;
        meanRgbDifference = foregroundChannels == 0 ? 0.0 : (double)rgbDifference / foregroundChannels;
    }

    private static bool IsLegacyChromaticGradientLoss(string source, SKBitmap first, SKBitmap legacy)
    {
        if (!source.Contains("Gradient", StringComparison.OrdinalIgnoreCase) ||
            !DeclaresChromaticStop(source))
            return false;

        static bool HasChromaticPixel(SKBitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor color = bitmap.GetPixel(x, y);
                if (color.Alpha > 0 &&
                    Math.Max(color.Red, Math.Max(color.Green, color.Blue)) -
                    Math.Min(color.Red, Math.Min(color.Green, color.Blue)) >= 16)
                    return true;
            }
            return false;
        }

        return HasChromaticPixel(first) && !HasChromaticPixel(legacy);
    }

    private static bool DeclaresChromaticStop(string source)
    {
        try
        {
            foreach (Match match in StopColorPattern.Matches(source))
            {
                if (SKColor.TryParse(match.Groups["color"].Value, out var color) &&
                    Math.Max(color.Red, Math.Max(color.Green, color.Blue)) -
                    Math.Min(color.Red, Math.Min(color.Green, color.Blue)) >= 16)
                    return true;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        return false;
    }

    private static void WriteMarkdown(string outputDirectory, SvgCorpusSummary summary)
    {
        var lines = new List<string>
        {
            "# SVG Corpus Report",
            "",
            $"Corpus: `{summary.CorpusRoot}`",
            "",
            $"- Corpus kind: {summary.CorpusKind}",
            $"- Manifest validated: {(summary.ManifestValidated ? "yes" : "no")}",
            $"- Selected: {summary.SelectedFiles}",
            $"- Selection truncated by --max-files: {(summary.SelectionTruncated ? "yes" : "no")}",
            $"- Selection prefixes: {(summary.SelectionPrefixes.Count == 0 ? "all" : string.Join(", ", summary.SelectionPrefixes))}",
            $"- Resume enabled / entries reused: {(summary.Resumed ? "yes" : "no")} / {summary.ReusedEntries}",
            $"- Renderer build id: {summary.RendererBuildId}",
            $"- Evaluated: {summary.EvaluatedFiles}",
            $"- First-party supported: {summary.FirstPartySupported}",
            $"- Compatibility fallbacks: {summary.CompatibilityFallbacks}",
            $"- Embedded resource rejections: {summary.ResourceRejections}",
            $"- First-party failures: {summary.FirstPartyFailures}",
            $"- Hybrid failures: {summary.HybridFailures}",
            $"- Worker timeouts / failures: {summary.WorkerTimeouts} / {summary.WorkerFailures}",
            $"- Comparable pixel parity: {summary.ComparableParityPasses}/{summary.ComparablePairs}",
            $"- Accepted, detected legacy reference defects: {summary.AcceptedReferenceDefects}",
            $"- Read failures / oversize skips: {summary.ReadFailures} / {summary.SkippedOversize}",
            "",
            "## Reason counts",
            "",
            "| kind | code | count |",
            "|---|---|---:|"
        };
        foreach (var reason in summary.ReasonCounts)
            lines.Add($"| {Escape(reason.Kind)} | {Escape(reason.Code)} | {reason.Count} |");
        if (summary.WptTypeCounts.Count != 0)
        {
            lines.AddRange(new[] { "", "## WPT item types", "", "| type | count |", "|---|---:|" });
            foreach (var type in summary.WptTypeCounts)
                lines.Add($"| {Escape(type.Type)} | {type.Count} |");
        }
        lines.AddRange(new[]
        {
            "",
            "## Files",
            "",
            "| file | classification | reasons | producer | fen ms | hybrid ms | legacy ms | parity | reference defect | alpha IoU | RGB mean diff | fen fg | legacy fg |",
            "|---|---|---|---|---:|---:|---:|---|---|---:|---:|---:|---:|"
        });
        foreach (var entry in summary.Entries)
        {
            string reasons = string.Join(",", entry.FallbackReasonCodes
                .Concat(entry.ResourceRejectionReasonCodes)
                .Concat(entry.FailureReasonCode == null ? Array.Empty<string>() : new[] { entry.FailureReasonCode }));
            lines.Add($"| {Escape(entry.Path)} | {entry.Classification} | {Escape(reasons.Length == 0 ? "-" : reasons)} | {entry.ProducingBackend ?? "-"} | " +
                      $"{entry.FirstPartyMilliseconds} | {entry.HybridMilliseconds} | {entry.LegacyMilliseconds} | " +
                      $"{(entry.PixelComparable ? (entry.PixelParity ? "pass" : "FAIL") : "-")} | " +
                      $"{entry.ReferenceDefect ?? "-"} | " +
                      $"{entry.AlphaIntersectionOverUnion:0.####} | {entry.MeanRgbDifference:0.####} | " +
                      $"{entry.FirstPartyForegroundPixels} | {entry.LegacyForegroundPixels} |");
        }
        WriteAllTextAtomic(
            Path.Combine(outputDirectory, "corpus-report.md"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static void WriteAllTextAtomic(string path, string content)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Escape(string value) => value.Replace("|", "\\|");
    private static string? Bound(string? value) => value == null ? null : value[..Math.Min(256, value.Length)];

    private sealed class TimedSvgResult : IDisposable
    {
        public TimedSvgResult(SvgRenderResult result, long elapsedMilliseconds)
        {
            Result = result;
            ElapsedMilliseconds = elapsedMilliseconds;
        }
        public SvgRenderResult Result { get; }
        public long ElapsedMilliseconds { get; }
        public void Dispose() => Result.Dispose();
    }
}

internal sealed class SvgCorpusSummary
{
    public int SchemaVersion { get; set; }
    public string CorpusRoot { get; set; } = string.Empty;
    public string CorpusKind { get; set; } = string.Empty;
    public List<string> SelectionPrefixes { get; set; } = new();
    public bool Resumed { get; set; }
    public int ReusedEntries { get; set; }
    public string RendererBuildId { get; set; } = string.Empty;
    public bool ManifestValidated { get; set; }
    public int SelectedFiles { get; set; }
    public bool SelectionTruncated { get; set; }
    public int EvaluatedFiles { get; set; }
    public int FirstPartySupported { get; set; }
    public int CompatibilityFallbacks { get; set; }
    public int ResourceRejections { get; set; }
    public int FirstPartyFailures { get; set; }
    public int HybridFailures { get; set; }
    public int WorkerTimeouts { get; set; }
    public int WorkerFailures { get; set; }
    public int ReadFailures { get; set; }
    public int SkippedOversize { get; set; }
    public int ComparablePairs { get; set; }
    public int ComparableParityPasses { get; set; }
    public int AcceptedReferenceDefects { get; set; }
    public List<SvgReasonCount> ReasonCounts { get; set; } = new();
    public List<SvgTypeCount> WptTypeCounts { get; set; } = new();
    public List<SvgCorpusEntry> Entries { get; set; } = new();
}

internal sealed class SvgReasonCount
{
    public string Kind { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int Count { get; set; }
}

internal sealed class SvgCorpusCheckpoint
{
    public string RendererBuildId { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public SvgCorpusEntry Entry { get; set; } = new();
}

internal sealed class SvgTypeCount
{
    public string Type { get; set; } = string.Empty;
    public int Count { get; set; }
}

internal sealed class SvgCorpusManifest
{
    public int SchemaVersion { get; set; }
    public string CorpusKind { get; set; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; set; }
    public List<SvgCorpusManifestSite> Sites { get; set; } = new();
    public List<SvgCorpusManifestFile> Files { get; set; } = new();
}

internal sealed class SvgCorpusManifestSite
{
    public string Id { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string FinalUrl { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int AssetFailures { get; set; }
    public int SvgCount { get; set; }
    public bool SelectionTruncated { get; set; }
}

internal sealed class SvgCorpusManifestFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
}

internal sealed class SvgCorpusEntry
{
    public string Path { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string SourceSha256 { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty;
    public string? WptType { get; set; }
    public string? ProducingBackend { get; set; }
    public long FirstPartyMilliseconds { get; set; }
    public long HybridMilliseconds { get; set; }
    public long LegacyMilliseconds { get; set; }
    public int WarningCount { get; set; }
    public List<string> WarningMessages { get; set; } = new();
    public List<string> FallbackReasonCodes { get; set; } = new();
    public List<string> ResourceRejectionReasonCodes { get; set; } = new();
    public string? FailureReasonCode { get; set; }
    public string? Error { get; set; }
    public bool PixelComparable { get; set; }
    public bool PixelParity { get; set; }
    public string? ReferenceDefect { get; set; }
    public double AlphaIntersectionOverUnion { get; set; }
    public double MeanRgbDifference { get; set; }
    public long FirstPartyForegroundPixels { get; set; }
    public long LegacyForegroundPixels { get; set; }
}
