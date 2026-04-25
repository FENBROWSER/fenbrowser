using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FenBrowser.Conformance;

public sealed class FullSweepInventory
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public string RepoRoot { get; set; } = string.Empty;
    public FullSweepSummary Summary { get; set; } = new();
    public List<InventoryItem> Items { get; set; } = new();
    public List<ConformanceSignal> Signals { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Full Sweep Inventory");
        sb.AppendLine();
        sb.AppendLine($"- Generated (UTC): `{GeneratedUtc:O}`");
        sb.AppendLine($"- Repo root: `{RepoRoot}`");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- Items: `{Summary.TotalItems}`");
        sb.AppendLine($"- Critical: `{Summary.Critical}`");
        sb.AppendLine($"- High: `{Summary.High}`");
        sb.AppendLine($"- Medium: `{Summary.Medium}`");
        sb.AppendLine($"- Low: `{Summary.Low}`");
        sb.AppendLine();
        sb.AppendLine("## Signals");
        sb.AppendLine();
        sb.AppendLine("| Source | Metric | Value |");
        sb.AppendLine("|---|---|---:|");
        foreach (var signal in Signals)
        {
            sb.AppendLine($"| {Escape(signal.Source)} | {Escape(signal.Metric)} | {Escape(signal.Value)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Top Items");
        sb.AppendLine();
        sb.AppendLine("| Severity | Subsystem | File | Line | Pattern | Snippet |");
        sb.AppendLine("|---|---|---|---:|---|---|");
        foreach (var item in Items
                     .OrderBy(i => SeverityOrder(i.Severity))
                     .ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => i.Line)
                     .Take(500))
        {
            sb.AppendLine($"| {item.Severity} | {Escape(item.Subsystem)} | {Escape(item.File)} | {item.Line} | {Escape(item.Pattern)} | {Escape(item.Snippet)} |");
        }

        return sb.ToString();
    }

    private static string Escape(string value) => (value ?? string.Empty).Replace("|", "\\|");

    private static int SeverityOrder(string severity)
    {
        return severity switch
        {
            "critical" => 0,
            "high" => 1,
            "medium" => 2,
            _ => 3
        };
    }
}

public sealed class FullSweepSummary
{
    public int TotalItems { get; set; }
    public int Critical { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
}

public sealed class InventoryItem
{
    public string Severity { get; set; } = "low";
    public string Subsystem { get; set; } = "unknown";
    public string File { get; set; } = string.Empty;
    public int Line { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
}

public sealed class ConformanceSignal
{
    public string Source { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public static class FullSweepInventoryBuilder
{
    private sealed record PatternRule(string Name, Regex Regex, string Severity, string SubsystemHint);

    private static readonly PatternRule[] Rules =
    {
        new("NotImplementedException", new Regex(@"\bNotImplementedException\b", RegexOptions.Compiled), "critical", "runtime"),
        new("TODO/FIXME", new Regex(@"\b(TODO|FIXME)\b", RegexOptions.Compiled), "high", "unknown"),
        new("Stub/Placeholder", new Regex(@"\b(stub|placeholder)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "high", "unknown"),
        new("Undefined Return", new Regex(@"=>\s*null\s*;|return\s+null\s*;", RegexOptions.Compiled), "medium", "unknown"),
        new("Always false", new Regex(@"=>\s*false\s*;|return\s+false\s*;", RegexOptions.Compiled), "low", "unknown"),
    };

    private static readonly string[] TargetRoots =
    {
        "FenBrowser.Core",
        "FenBrowser.FenEngine",
        "FenBrowser.Host"
    };

    public static FullSweepInventory Build(string repoRoot, string? wptPath = null)
    {
        var inventory = new FullSweepInventory
        {
            RepoRoot = repoRoot
        };

        foreach (var root in TargetRoots)
        {
            var abs = Path.Combine(repoRoot, root);
            if (!Directory.Exists(abs))
            {
                continue;
            }

            ScanRoot(abs, repoRoot, inventory.Items);
        }

        PopulateSummary(inventory);
        AddConformanceSignals(inventory, wptPath);
        return inventory;
    }

    private static void ScanRoot(string rootPath, string repoRoot, List<InventoryItem> items)
    {
        foreach (var file in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
        {
            if (IsIgnored(file))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                foreach (var rule in Rules)
                {
                    if (!rule.Regex.IsMatch(line))
                    {
                        continue;
                    }

                    items.Add(new InventoryItem
                    {
                        Severity = ClassifySeverity(rule, file, line),
                        Subsystem = ClassifySubsystem(file, rule.SubsystemHint),
                        File = Path.GetRelativePath(repoRoot, file).Replace('\\', '/'),
                        Line = i + 1,
                        Pattern = rule.Name,
                        Snippet = line.Trim()
                    });
                }
            }
        }
    }

    private static bool IsIgnored(string file)
    {
        var lower = file.Replace('\\', '/').ToLowerInvariant();
        return lower.Contains("/bin/") ||
               lower.Contains("/obj/") ||
               lower.Contains("/generated/") ||
               lower.EndsWith(".g.cs", StringComparison.Ordinal);
    }

    private static string ClassifySeverity(PatternRule rule, string file, string line)
    {
        var lowerFile = file.Replace('\\', '/').ToLowerInvariant();
        var lowerLine = line.ToLowerInvariant();

        if (rule.Name == "Always false" || rule.Name == "Undefined Return")
        {
            if (lowerFile.Contains("/tests/") || lowerLine.Contains("if (") || lowerLine.Contains("== null"))
            {
                return "low";
            }
        }

        if (lowerFile.Contains("/core/parser") || lowerFile.Contains("/core/lexer") ||
            lowerFile.Contains("/dom/") || lowerFile.Contains("/layout/") ||
            lowerFile.Contains("/rendering/css/") || lowerFile.Contains("/core/fenruntime"))
        {
            return rule.Severity switch
            {
                "low" => "medium",
                _ => rule.Severity
            };
        }

        return rule.Severity;
    }

    private static string ClassifySubsystem(string file, string hint)
    {
        var lower = file.Replace('\\', '/').ToLowerInvariant();
        if (lower.Contains("/core/parsing/") || lower.Contains("/core/lexer") || lower.Contains("/core/parser"))
            return "html-parser-js-parser";
        if (lower.Contains("/dom/"))
            return "dom-tree-node";
        if (lower.Contains("/rendering/css/") || lower.Contains("/core/css/"))
            return "css";
        if (lower.Contains("/layout/"))
            return "layout";
        if (lower.Contains("/core/fenruntime") || lower.Contains("/scripting/"))
            return "js-runtime";
        if (lower.Contains("/host/"))
            return "host-integration";
        return hint;
    }

    private static void PopulateSummary(FullSweepInventory inventory)
    {
        inventory.Summary.TotalItems = inventory.Items.Count;
        inventory.Summary.Critical = inventory.Items.Count(i => i.Severity == "critical");
        inventory.Summary.High = inventory.Items.Count(i => i.Severity == "high");
        inventory.Summary.Medium = inventory.Items.Count(i => i.Severity == "medium");
        inventory.Summary.Low = inventory.Items.Count(i => i.Severity == "low");
    }

    private static void AddConformanceSignals(FullSweepInventory inventory, string? wptPath)
    {
        AddWptSignal(inventory, wptPath);
    }

    private static void AddWptSignal(FullSweepInventory inventory, string? wptPath)
    {
        var path = ResolveResultPath(inventory.RepoRoot, wptPath, "Results/wpt_results_latest.json");
        if (path == null || !File.Exists(path))
            return;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        AddSignal(inventory, "WPT", "path", path);
        AddSignal(inventory, "WPT", "total", ReadInt(root, "total").ToString());
        AddSignal(inventory, "WPT", "passed", ReadInt(root, "passed").ToString());
        AddSignal(inventory, "WPT", "failed", ReadInt(root, "failed").ToString());
        AddSignal(inventory, "WPT", "passRate", ReadDouble(root, "passRate").ToString("F2"));
    }

    private static string? ResolveResultPath(string repoRoot, string? explicitPath, string fallbackRelative)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.IsPathRooted(explicitPath)
                ? explicitPath
                : Path.GetFullPath(Path.Combine(repoRoot, explicitPath));
        }

        return Path.Combine(repoRoot, fallbackRelative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void AddSignal(FullSweepInventory inventory, string source, string metric, string value)
    {
        inventory.Signals.Add(new ConformanceSignal
        {
            Source = source,
            Metric = metric,
            Value = value
        });
    }

    private static int ReadInt(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : 0;
    }

    private static double ReadDouble(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble()
            : 0.0;
    }
}
