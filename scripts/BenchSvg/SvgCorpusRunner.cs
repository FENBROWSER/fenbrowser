using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
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
        if (corpusKind == "captured-site" && string.IsNullOrWhiteSpace(manifest))
            throw new ArgumentException("captured-site corpus requires --manifest");
        return new SvgCorpusOptions(
            corpus,
            Path.GetFullPath(output),
            maxFiles,
            maxFileBytes,
            timeoutMs,
            corpusKind,
            manifest == null ? null : Path.GetFullPath(manifest),
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
}

internal static class SvgCorpusRunner
{
    private const double MinimumAlphaIntersectionOverUnion = 0.80;
    private const double MaximumMeanRgbDifference = 16.0;

    public static int Run(
        SvgCorpusOptions options,
        ISvgRenderer firstParty,
        ISvgRenderer hybrid,
        ISvgRenderer legacy)
    {
        bool manifestValidated = false;
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
        var selected = allFiles.Take(options.MaxFiles + 1).ToArray();
        bool selectionTruncated = selected.Length > options.MaxFiles;
        var files = selected.Take(options.MaxFiles).ToArray();

        var entries = new List<SvgCorpusEntry>(files.Length);
        var runClock = Stopwatch.StartNew();
        for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            string file = files[fileIndex];
            string relative = Path.GetRelativePath(options.CorpusDirectory, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            var info = new FileInfo(file);
            if (info.Length > options.MaxFileBytes)
            {
                entries.Add(new SvgCorpusEntry { Path = relative, Bytes = info.Length, Classification = "skipped-oversize" });
                continue;
            }

            var entry = EvaluateIsolated(relative, info.Length, file, options.PerFileTimeoutMs);
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
            SchemaVersion = 1,
            CorpusRoot = options.CorpusDirectory,
            CorpusKind = options.CorpusKind,
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
            Entries = entries
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
        bool parityOk = summary.ComparableParityPasses == summary.ComparablePairs;
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
            evaluated = summary.EvaluatedFiles,
            firstParty = summary.FirstPartySupported,
            fallback = summary.CompatibilityFallbacks,
            resourceRejections = summary.ResourceRejections,
            firstPartyFailures = summary.FirstPartyFailures,
            hybridFailures = summary.HybridFailures,
            workerTimeouts = summary.WorkerTimeouts,
            workerFailures = summary.WorkerFailures,
            parity = $"{summary.ComparableParityPasses}/{summary.ComparablePairs}",
            report = Path.Combine(options.OutputDirectory, "corpus-report.json")
        }));

        return options.Gate && !gateOk ? 1 : 0;
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

        var manifestPaths = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<string>(manifest.Files.Count);
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
            resolved.Add(full);
        }

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

    public static int RunWorker(string inputPath, string outputPath)
    {
        try
        {
            var info = new FileInfo(inputPath);
            string source = File.ReadAllText(inputPath);
            ISvgRenderer firstParty = new FenSvgRenderer();
            ISvgRenderer legacy = new SvgSkiaRenderer();
            ISvgRenderer hybrid = new HybridSvgRenderer(firstParty, legacy);
            var entry = Evaluate(Path.GetFileName(inputPath), info.Length, source, firstParty, hybrid, legacy);
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
        int timeoutMs)
    {
        string outputPath = Path.Combine(Path.GetTempPath(), $"fen-svg-worker-{Guid.NewGuid():N}.json");
        try
        {
            using var process = new Process { StartInfo = CreateWorkerStartInfo(inputPath, outputPath) };
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

    private static ProcessStartInfo CreateWorkerStartInfo(string inputPath, string outputPath)
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
        return start;
    }

    private static SvgCorpusEntry Evaluate(
        string relativePath,
        long bytes,
        string source,
        ISvgRenderer firstParty,
        ISvgRenderer hybrid,
        ISvgRenderer legacy)
    {
        using var fen = TimedRender(firstParty, source);
        using var routed = TimedRender(hybrid, source);
        using var reference = TimedRender(legacy, source);

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
                          fen.Result.Bitmap != null && reference.Result.Bitmap != null &&
                          fen.Result.Bitmap.Width == reference.Result.Bitmap.Width &&
                          fen.Result.Bitmap.Height == reference.Result.Bitmap.Height;
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
            Error = Bound(routed.Result.ErrorMessage ?? fen.Result.ErrorMessage),
            PixelComparable = comparable,
            PixelParity = parity,
            AlphaIntersectionOverUnion = Math.Round(alphaIou, 4),
            MeanRgbDifference = Math.Round(meanRgbDifference, 4),
            FirstPartyForegroundPixels = firstPartyForegroundPixels,
            LegacyForegroundPixels = legacyForegroundPixels
        };
    }

    private static TimedSvgResult TimedRender(ISvgRenderer renderer, string source)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = renderer.Render(source);
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
        for (int y = 0; y < first.Height; y++)
        for (int x = 0; x < first.Width; x++)
        {
            SKColor a = first.GetPixel(x, y);
            SKColor b = second.GetPixel(x, y);
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
            $"- Evaluated: {summary.EvaluatedFiles}",
            $"- First-party supported: {summary.FirstPartySupported}",
            $"- Compatibility fallbacks: {summary.CompatibilityFallbacks}",
            $"- Embedded resource rejections: {summary.ResourceRejections}",
            $"- First-party failures: {summary.FirstPartyFailures}",
            $"- Hybrid failures: {summary.HybridFailures}",
            $"- Worker timeouts / failures: {summary.WorkerTimeouts} / {summary.WorkerFailures}",
            $"- Comparable pixel parity: {summary.ComparableParityPasses}/{summary.ComparablePairs}",
            $"- Read failures / oversize skips: {summary.ReadFailures} / {summary.SkippedOversize}",
            "",
            "| file | classification | producer | fen ms | hybrid ms | legacy ms | parity | alpha IoU | RGB mean diff | fen fg | legacy fg |",
            "|---|---|---|---:|---:|---:|---|---:|---:|---:|---:|"
        };
        foreach (var entry in summary.Entries)
        {
            lines.Add($"| {Escape(entry.Path)} | {entry.Classification} | {entry.ProducingBackend ?? "-"} | " +
                      $"{entry.FirstPartyMilliseconds} | {entry.HybridMilliseconds} | {entry.LegacyMilliseconds} | " +
                      $"{(entry.PixelComparable ? (entry.PixelParity ? "pass" : "FAIL") : "-")} | " +
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
    public List<SvgCorpusEntry> Entries { get; set; } = new();
}

internal sealed class SvgCorpusManifest
{
    public int SchemaVersion { get; set; }
    public string CorpusKind { get; set; } = string.Empty;
    public List<SvgCorpusManifestFile> Files { get; set; } = new();
}

internal sealed class SvgCorpusManifestFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class SvgCorpusEntry
{
    public string Path { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Classification { get; set; } = string.Empty;
    public string? ProducingBackend { get; set; }
    public long FirstPartyMilliseconds { get; set; }
    public long HybridMilliseconds { get; set; }
    public long LegacyMilliseconds { get; set; }
    public int WarningCount { get; set; }
    public string? Error { get; set; }
    public bool PixelComparable { get; set; }
    public bool PixelParity { get; set; }
    public double AlphaIntersectionOverUnion { get; set; }
    public double MeanRgbDifference { get; set; }
    public long FirstPartyForegroundPixels { get; set; }
    public long LegacyForegroundPixels { get; set; }
}
