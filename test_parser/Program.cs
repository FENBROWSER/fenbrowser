using System.Text;
using System.Text.RegularExpressions;

namespace FenBrowser.VolumeReferenceParser;

internal static class Program
{
    private static readonly Regex HeadingLineRangeRegex =
        new(@"`(?<token>[^`]+\.cs)`\s*\(Lines\s*(?<start>\d+)\s*-\s*(?<end>\d+)\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BacktickCodeRefRegex =
        new(@"`(?<token>[^`]+\.cs(?::\d+(?:-\d+)?)?)`",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ParenthesizedPathRegex =
        new(@"\((?<token>[A-Za-z0-9_./\\-]+\.cs)\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static int Main(string[] args)
    {
        var options = ParseOptions(args);
        if (!Directory.Exists(options.RepoRoot))
        {
            Console.Error.WriteLine($"[volume-ref] Repo root not found: {options.RepoRoot}");
            return 2;
        }

        if (!Directory.Exists(options.DocsRoot))
        {
            Console.Error.WriteLine($"[volume-ref] Docs path not found: {options.DocsRoot}");
            return 2;
        }

        var volumes = Directory.GetFiles(options.DocsRoot, "VOLUME_*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (volumes.Length == 0)
        {
            Console.Error.WriteLine($"[volume-ref] No VOLUME_*.md files found in: {options.DocsRoot}");
            return 2;
        }

        var sourceIndex = BuildSourceIndex(options.RepoRoot);
        var lineCountCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ReferenceResult>(capacity: 2048);

        foreach (var volumePath in volumes)
        {
            ParseVolumeReferences(volumePath, options.RepoRoot, sourceIndex, lineCountCache, results);
        }

        var summary = BuildSummary(results);
        var report = BuildTextReport(results, summary);
        Console.WriteLine(report);

        if (!string.IsNullOrWhiteSpace(options.WritePath))
        {
            var outputPath = Path.IsPathRooted(options.WritePath)
                ? options.WritePath
                : Path.GetFullPath(Path.Combine(options.RepoRoot, options.WritePath));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, BuildMarkdownReport(results, summary), Encoding.UTF8);
            Console.WriteLine($"[volume-ref] Wrote markdown report: {outputPath}");
        }

        return summary.ErrorCount > 0 ? 1 : 0;
    }

    private static CliOptions ParseOptions(string[] args)
    {
        var repoRoot = Directory.GetCurrentDirectory();
        string? docsRoot = null;
        string? writePath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--repo", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                repoRoot = Path.GetFullPath(args[++i]);
            }
            else if (string.Equals(arg, "--docs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                docsRoot = args[++i];
            }
            else if (string.Equals(arg, "--write", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                writePath = args[++i];
            }
        }

        var resolvedDocsRoot = string.IsNullOrWhiteSpace(docsRoot)
            ? Path.Combine(repoRoot, "docs")
            : (Path.IsPathRooted(docsRoot) ? docsRoot : Path.GetFullPath(Path.Combine(repoRoot, docsRoot)));

        return new CliOptions(repoRoot, resolvedDocsRoot, writePath);
    }

    private static SourceIndex BuildSourceIndex(string repoRoot)
    {
        var byFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var allSourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (ShouldSkipPath(repoRoot, filePath))
            {
                continue;
            }

            var relative = NormalizeRelativePath(repoRoot, filePath);
            allSourceFiles.Add(relative);

            var fileName = Path.GetFileName(filePath);
            if (!byFileName.TryGetValue(fileName, out var list))
            {
                list = new List<string>();
                byFileName[fileName] = list;
            }

            list.Add(relative);
        }

        foreach (var pair in byFileName)
        {
            pair.Value.Sort(StringComparer.OrdinalIgnoreCase);
        }

        return new SourceIndex(byFileName, allSourceFiles);
    }

    private static bool ShouldSkipPath(string repoRoot, string absolutePath)
    {
        var relative = NormalizeRelativePath(repoRoot, absolutePath);
        var lower = relative.ToLowerInvariant();
        return lower.Contains("/bin/")
            || lower.Contains("/obj/")
            || lower.StartsWith(".git/", StringComparison.Ordinal)
            || lower.StartsWith(".vs/", StringComparison.Ordinal)
            || lower.StartsWith("artifacts/", StringComparison.Ordinal)
            || lower.StartsWith("test_parser/", StringComparison.Ordinal);
    }

    private static void ParseVolumeReferences(
        string volumePath,
        string repoRoot,
        SourceIndex sourceIndex,
        Dictionary<string, int> lineCountCache,
        List<ReferenceResult> output)
    {
        var lines = File.ReadAllLines(volumePath);
        var volumeRelative = NormalizeRelativePath(repoRoot, volumePath);
        var seenOnVolume = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNo = i + 1;
            var headingTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in HeadingLineRangeRegex.Matches(line))
            {
                if (!m.Success)
                {
                    continue;
                }

                var token = m.Groups["token"].Value;
                if (token.Contains('*', StringComparison.Ordinal))
                {
                    continue;
                }

                headingTokens.Add(token);
                var start = int.Parse(m.Groups["start"].Value);
                var end = int.Parse(m.Groups["end"].Value);
                AddReference(seenOnVolume, output, ResolveReference(volumeRelative, lineNo, token, start, end, sourceIndex, lineCountCache, repoRoot));
            }

            foreach (Match m in BacktickCodeRefRegex.Matches(line))
            {
                if (!m.Success)
                {
                    continue;
                }

                var token = m.Groups["token"].Value;
                if (TryParseToken(token, out var pathToken, out var start, out var end))
                {
                    if (pathToken.Contains('*', StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!start.HasValue && headingTokens.Contains(pathToken))
                    {
                        continue;
                    }

                    AddReference(seenOnVolume, output, ResolveReference(volumeRelative, lineNo, pathToken, start, end, sourceIndex, lineCountCache, repoRoot));
                }
            }

            foreach (Match m in ParenthesizedPathRegex.Matches(line))
            {
                if (!m.Success)
                {
                    continue;
                }

                var token = m.Groups["token"].Value;
                if (token.Contains('*', StringComparison.Ordinal))
                {
                    continue;
                }

                AddReference(seenOnVolume, output, ResolveReference(volumeRelative, lineNo, token, null, null, sourceIndex, lineCountCache, repoRoot));
            }
        }
    }

    private static void AddReference(HashSet<string> dedupe, List<ReferenceResult> output, ReferenceResult result)
    {
        var key = $"{result.VolumePath}|{result.VolumeLine}|{result.TokenPath}|{result.StartLine}|{result.EndLine}";
        if (dedupe.Add(key))
        {
            output.Add(result);
        }
    }

    private static bool TryParseToken(string token, out string pathToken, out int? startLine, out int? endLine)
    {
        pathToken = token.Trim();
        startLine = null;
        endLine = null;

        var colonIndex = pathToken.LastIndexOf(':');
        if (colonIndex < 0 || colonIndex == pathToken.Length - 1)
        {
            return true;
        }

        var linePart = pathToken[(colonIndex + 1)..];
        if (!Regex.IsMatch(linePart, @"^\d+(?:-\d+)?$", RegexOptions.CultureInvariant))
        {
            return true;
        }

        pathToken = pathToken[..colonIndex];
        if (linePart.Contains('-', StringComparison.Ordinal))
        {
            var split = linePart.Split('-', 2);
            startLine = int.Parse(split[0]);
            endLine = int.Parse(split[1]);
        }
        else
        {
            startLine = int.Parse(linePart);
            endLine = startLine;
        }

        return true;
    }

    private static ReferenceResult ResolveReference(
        string volumeRelativePath,
        int volumeLine,
        string tokenPath,
        int? startLine,
        int? endLine,
        SourceIndex sourceIndex,
        Dictionary<string, int> lineCountCache,
        string repoRoot)
    {
        var resolved = ResolvePathToken(tokenPath, volumeRelativePath, sourceIndex, repoRoot, out var ambiguity);
        if (resolved is null)
        {
            return new ReferenceResult(
                volumeRelativePath,
                volumeLine,
                tokenPath,
                startLine,
                endLine,
                null,
                0,
                ambiguity is null ? ReferenceStatus.MissingFile : ReferenceStatus.AmbiguousFile,
                ambiguity);
        }

        var absolute = Path.GetFullPath(Path.Combine(repoRoot, resolved));
        if (!lineCountCache.TryGetValue(resolved, out var actualLineCount))
        {
            actualLineCount = File.ReadLines(absolute).Count();
            lineCountCache[resolved] = actualLineCount;
        }

        if (startLine.HasValue && endLine.HasValue)
        {
            if (startLine.Value <= 0 || endLine.Value <= 0 || endLine.Value < startLine.Value)
            {
                return new ReferenceResult(
                    volumeRelativePath,
                    volumeLine,
                    tokenPath,
                    startLine,
                    endLine,
                    resolved,
                    actualLineCount,
                    ReferenceStatus.InvalidRangeFormat,
                    "Invalid line range order/value.");
            }

            if (endLine.Value > actualLineCount)
            {
                return new ReferenceResult(
                    volumeRelativePath,
                    volumeLine,
                    tokenPath,
                    startLine,
                    endLine,
                    resolved,
                    actualLineCount,
                    ReferenceStatus.OutOfRange,
                    $"Claimed end line {endLine.Value} exceeds actual line count {actualLineCount}.");
            }
        }

        return new ReferenceResult(
            volumeRelativePath,
            volumeLine,
            tokenPath,
            startLine,
            endLine,
            resolved,
            actualLineCount,
            ReferenceStatus.Ok,
            null);
    }

    private static string? ResolvePathToken(
        string tokenPath,
        string volumeRelativePath,
        SourceIndex sourceIndex,
        string repoRoot,
        out string? ambiguity)
    {
        ambiguity = null;
        var normalized = tokenPath.Trim().Replace('\\', '/');
        if (!normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (normalized.Contains('/'))
        {
            var direct = NormalizePathSeparators(normalized);
            if (sourceIndex.AllSourceFiles.Contains(direct))
            {
                return direct;
            }

            var suffixMatches = sourceIndex.AllSourceFiles
                .Where(p => p.EndsWith(direct, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (suffixMatches.Count == 1)
            {
                return suffixMatches[0];
            }

            if (suffixMatches.Count > 1)
            {
                var preferred = TryDisambiguateByVolume(volumeRelativePath, suffixMatches);
                if (preferred is not null)
                {
                    return preferred;
                }

                ambiguity = "Multiple file matches: " + string.Join(", ", suffixMatches);
                return null;
            }

            var directAbs = Path.GetFullPath(Path.Combine(repoRoot, direct));
            if (File.Exists(directAbs))
            {
                return NormalizeRelativePath(repoRoot, directAbs);
            }

            return null;
        }

        if (sourceIndex.ByFileName.TryGetValue(normalized, out var matches))
        {
            if (matches.Count == 1)
            {
                return matches[0];
            }

            var preferred = TryDisambiguateByVolume(volumeRelativePath, matches);
            if (preferred is not null)
            {
                return preferred;
            }

            ambiguity = "Multiple file matches: " + string.Join(", ", matches);
            return null;
        }

        return null;
    }

    private static string? TryDisambiguateByVolume(string volumePath, IReadOnlyCollection<string> candidates)
    {
        var upperVolume = volumePath.ToUpperInvariant();
        string[] preferredPrefixes;

        if (upperVolume.Contains("VOLUME_II_"))
        {
            preferredPrefixes = ["FenBrowser.Core/"];
        }
        else if (upperVolume.Contains("VOLUME_III_"))
        {
            preferredPrefixes = ["FenBrowser.FenEngine/"];
        }
        else if (upperVolume.Contains("VOLUME_IV_"))
        {
            preferredPrefixes = ["FenBrowser.Host/"];
        }
        else if (upperVolume.Contains("VOLUME_V_DEVTOOLS"))
        {
            preferredPrefixes = ["FenBrowser.DevTools/"];
        }
        else if (upperVolume.Contains("VOLUME_VI_"))
        {
            preferredPrefixes = ["FenBrowser.WebDriver/", "FenBrowser.Tests/", "FenBrowser.FenEngine/Testing/", "FenBrowser.Test262/"];
        }
        else
        {
            preferredPrefixes = Array.Empty<string>();
        }

        foreach (var prefix in preferredPrefixes)
        {
            var scoped = candidates
                .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (scoped.Count == 1)
            {
                return scoped[0];
            }
        }

        return null;
    }

    private static string NormalizeRelativePath(string repoRoot, string path)
    {
        var relative = Path.GetRelativePath(repoRoot, path);
        return NormalizePathSeparators(relative);
    }

    private static string NormalizePathSeparators(string path) => path.Replace('\\', '/');

    private static Summary BuildSummary(IReadOnlyCollection<ReferenceResult> results)
    {
        var ok = results.Count(r => r.Status == ReferenceStatus.Ok);
        var missing = results.Count(r => r.Status == ReferenceStatus.MissingFile);
        var ambiguous = results.Count(r => r.Status == ReferenceStatus.AmbiguousFile);
        var invalidRange = results.Count(r => r.Status == ReferenceStatus.InvalidRangeFormat);
        var outOfRange = results.Count(r => r.Status == ReferenceStatus.OutOfRange);
        var errors = missing + ambiguous + invalidRange + outOfRange;
        return new Summary(results.Count, ok, missing, ambiguous, invalidRange, outOfRange, errors);
    }

    private static string BuildTextReport(IReadOnlyCollection<ReferenceResult> results, Summary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[volume-ref] Volume reference validation report");
        sb.AppendLine($"[volume-ref] Total={summary.Total} OK={summary.OkCount} Errors={summary.ErrorCount}");
        sb.AppendLine($"[volume-ref] Missing={summary.MissingCount} Ambiguous={summary.AmbiguousCount} InvalidRange={summary.InvalidRangeCount} OutOfRange={summary.OutOfRangeCount}");

        foreach (var group in results
                     .Where(r => r.Status != ReferenceStatus.Ok)
                     .OrderBy(r => r.VolumePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.VolumeLine)
                     .GroupBy(r => r.VolumePath))
        {
            sb.AppendLine($"[volume-ref] {group.Key}");
            foreach (var item in group)
            {
                var claimed = item.StartLine.HasValue
                    ? $"{item.TokenPath}:{item.StartLine}-{item.EndLine}"
                    : item.TokenPath;
                var resolved = item.ResolvedPath is null
                    ? "<unresolved>"
                    : $"{item.ResolvedPath} (actual lines: {item.ActualLineCount})";
                sb.AppendLine($"  - line {item.VolumeLine}: {item.Status} | claimed={claimed} | resolved={resolved}");
                if (!string.IsNullOrWhiteSpace(item.Details))
                {
                    sb.AppendLine($"    details: {item.Details}");
                }
            }
        }

        if (summary.ErrorCount == 0)
        {
            sb.AppendLine("[volume-ref] All volume references resolved and line ranges are valid.");
        }

        return sb.ToString();
    }

    private static string BuildMarkdownReport(IReadOnlyCollection<ReferenceResult> results, Summary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Volume Reference Validation Report");
        sb.AppendLine();
        sb.AppendLine($"- Total references: **{summary.Total}**");
        sb.AppendLine($"- OK: **{summary.OkCount}**");
        sb.AppendLine($"- Errors: **{summary.ErrorCount}**");
        sb.AppendLine($"- Missing: **{summary.MissingCount}**");
        sb.AppendLine($"- Ambiguous: **{summary.AmbiguousCount}**");
        sb.AppendLine($"- Invalid range: **{summary.InvalidRangeCount}**");
        sb.AppendLine($"- Out of range: **{summary.OutOfRangeCount}**");
        sb.AppendLine();

        var failed = results
            .Where(r => r.Status != ReferenceStatus.Ok)
            .OrderBy(r => r.VolumePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.VolumeLine)
            .ToList();

        if (failed.Count == 0)
        {
            sb.AppendLine("All references passed.");
            return sb.ToString();
        }

        sb.AppendLine("| Volume | Doc Line | Token | Status | Resolved | Details |");
        sb.AppendLine("|---|---:|---|---|---|---|");
        foreach (var item in failed)
        {
            var resolved = item.ResolvedPath ?? "<unresolved>";
            var details = item.Details ?? string.Empty;
            sb.AppendLine($"| {EscapePipe(item.VolumePath)} | {item.VolumeLine} | {EscapePipe(item.TokenPath)} | {item.Status} | {EscapePipe(resolved)} | {EscapePipe(details)} |");
        }

        return sb.ToString();
    }

    private static string EscapePipe(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    private sealed record CliOptions(string RepoRoot, string DocsRoot, string? WritePath);

    private sealed record SourceIndex(
        Dictionary<string, List<string>> ByFileName,
        HashSet<string> AllSourceFiles);

    private sealed record ReferenceResult(
        string VolumePath,
        int VolumeLine,
        string TokenPath,
        int? StartLine,
        int? EndLine,
        string? ResolvedPath,
        int ActualLineCount,
        ReferenceStatus Status,
        string? Details);

    private enum ReferenceStatus
    {
        Ok,
        MissingFile,
        AmbiguousFile,
        InvalidRangeFormat,
        OutOfRange
    }

    private sealed record Summary(
        int Total,
        int OkCount,
        int MissingCount,
        int AmbiguousCount,
        int InvalidRangeCount,
        int OutOfRangeCount,
        int ErrorCount);
}
