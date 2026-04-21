using System.Text;
using System.Text.Json;

namespace FenBrowser.Conformance;

public sealed class SpecComplianceMatrix
{
    public string Scope { get; set; } = "all";
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public SpecComplianceSummary Summary { get; set; } = new();
    public List<SpecComplianceCluster> Clusters { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Spec Compliance Matrix");
        sb.AppendLine();
        sb.AppendLine($"- Scope: `{Scope}`");
        sb.AppendLine($"- Generated (UTC): `{GeneratedUtc:O}`");
        sb.AppendLine($"- Total: `{Summary.Total}`");
        sb.AppendLine($"- Passed: `{Summary.Passed}`");
        sb.AppendLine($"- Failed: `{Summary.Failed}`");
        sb.AppendLine($"- Timeout: `{Summary.Timeout}`");
        sb.AppendLine($"- Skipped: `{Summary.Skipped}`");
        sb.AppendLine($"- Pass Rate: `{Summary.PassRate:P1}`");
        sb.AppendLine();
        sb.AppendLine("| Cluster | Total | Passed | Failed | Timeout | Skipped | Pass Rate |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var cluster in Clusters.OrderByDescending(c => c.Total).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| {cluster.Name} | {cluster.Total} | {cluster.Passed} | {cluster.Failed} | {cluster.Timeout} | {cluster.Skipped} | {cluster.PassRate:P1} |");
        }

        return sb.ToString();
    }
}

public sealed class SpecComplianceSummary
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Timeout { get; set; }
    public int Skipped { get; set; }
    public double PassRate => Total == 0 ? 0 : (double)Passed / Total;
}

public sealed class SpecComplianceCluster
{
    public string Name { get; set; } = "unknown";
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Timeout { get; set; }
    public int Skipped { get; set; }
    public double PassRate => Total == 0 ? 0 : (double)Passed / Total;
}

internal sealed record WptResultEntry(string TestPath, string Status);

public static class SpecComplianceMatrixBuilder
{
    public static SpecComplianceMatrix BuildFromWpt(string wptPath, string? scope)
    {
        if (string.IsNullOrWhiteSpace(wptPath))
        {
            throw new ArgumentException("WPT path is required.", nameof(wptPath));
        }

        if (!File.Exists(wptPath))
        {
            throw new FileNotFoundException($"WPT results not found: {wptPath}", wptPath);
        }

        var entries = LoadEntries(wptPath);
        var scopeFilter = NormalizeScope(scope);
        if (!string.Equals(scopeFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            entries = entries
                .Where(e => e.TestPath.Contains(scopeFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var clusters = new Dictionary<string, SpecComplianceCluster>(StringComparer.OrdinalIgnoreCase);
        var summary = new SpecComplianceSummary();

        foreach (var entry in entries)
        {
            var clusterKey = ClusterFor(entry.TestPath);
            if (!clusters.TryGetValue(clusterKey, out var cluster))
            {
                cluster = new SpecComplianceCluster { Name = clusterKey };
                clusters[clusterKey] = cluster;
            }

            ApplyStatus(summary, entry.Status);
            ApplyStatus(cluster, entry.Status);
        }

        return new SpecComplianceMatrix
        {
            Scope = scopeFilter,
            GeneratedUtc = DateTime.UtcNow,
            Summary = summary,
            Clusters = clusters.Values.ToList()
        };
    }

    private static List<WptResultEntry> LoadEntries(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var entries = new List<WptResultEntry>(capacity: 1024);
        ExtractEntries(doc.RootElement, entries);
        return entries;
    }

    private static void ExtractEntries(JsonElement node, List<WptResultEntry> entries)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            string? test = null;
            string? status = null;

            if (node.TryGetProperty("test", out var testProp) && testProp.ValueKind == JsonValueKind.String)
            {
                test = testProp.GetString();
            }
            else if (node.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                var candidate = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(candidate) && candidate.Contains('/', StringComparison.Ordinal))
                {
                    test = candidate;
                }
            }

            if (node.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String)
            {
                status = statusProp.GetString();
            }

            if (!string.IsNullOrWhiteSpace(test) && !string.IsNullOrWhiteSpace(status))
            {
                entries.Add(new WptResultEntry(test!, status!));
            }

            foreach (var property in node.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    ExtractEntries(property.Value, entries);
                }
            }

            return;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                ExtractEntries(item, entries);
            }
        }
    }

    private static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return "all";
        }

        return scope.Trim();
    }

    private static string ClusterFor(string testPath)
    {
        var normalized = testPath.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "unknown";
        }

        if (parts.Length == 1)
        {
            return parts[0];
        }

        return $"{parts[0]}/{parts[1]}";
    }

    private static void ApplyStatus(SpecComplianceSummary summary, string status)
    {
        summary.Total++;
        switch (status.Trim().ToUpperInvariant())
        {
            case "PASS":
            case "OK":
                summary.Passed++;
                break;
            case "TIMEOUT":
                summary.Timeout++;
                break;
            case "SKIP":
            case "NOTRUN":
            case "PRECONDITION_FAILED":
                summary.Skipped++;
                break;
            default:
                summary.Failed++;
                break;
        }
    }

    private static void ApplyStatus(SpecComplianceCluster cluster, string status)
    {
        cluster.Total++;
        switch (status.Trim().ToUpperInvariant())
        {
            case "PASS":
            case "OK":
                cluster.Passed++;
                break;
            case "TIMEOUT":
                cluster.Timeout++;
                break;
            case "SKIP":
            case "NOTRUN":
            case "PRECONDITION_FAILED":
                cluster.Skipped++;
                break;
            default:
                cluster.Failed++;
                break;
        }
    }
}
