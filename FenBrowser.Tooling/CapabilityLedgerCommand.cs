using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FenBrowser.Tooling;

internal static class CapabilityLedgerCommand
{
    private static readonly Regex HeaderRegex = new(
        @"^//\s*(?<key>SpecRef|CapabilityId|Determinism|FallbackPolicy):\s*(?<value>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static CapabilityLedgerResult Run(string[] args)
    {
        var commandArgs = ParseArguments(args);
        var repositoryRoot = ResolveRepositoryRoot();
        var matrixPath = Path.Combine(repositoryRoot, "docs", "COMPLIANCE_MATRIX.md");
        var governanceMapPath = Path.Combine(repositoryRoot, "docs", "spec_governance_map.json");

        if (!File.Exists(matrixPath))
        {
            throw new FileNotFoundException($"Missing compliance matrix: {matrixPath}");
        }

        if (!File.Exists(governanceMapPath))
        {
            throw new FileNotFoundException($"Missing governance map: {governanceMapPath}");
        }

        var matrixCapabilities = ParseComplianceMatrix(matrixPath);
        var governanceMap = LoadGovernanceMap(governanceMapPath);
        var governedFiles = ReadGovernedFileHeaders(repositoryRoot, governanceMap.GovernedFiles);
        var sourceBindingsByCapability = governedFiles
            .Where(entry => !string.IsNullOrWhiteSpace(entry.CapabilityId))
            .GroupBy(entry => entry.CapabilityId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var requiredCapabilitySet = new HashSet<string>(governanceMap.RequiredCapabilityIds, StringComparer.Ordinal);
        var matrixCapabilitySet = new HashSet<string>(matrixCapabilities.Select(x => x.CapabilityId), StringComparer.Ordinal);
        var liveArtifactEvidence = EvaluateLiveArtifactEvidence(repositoryRoot, commandArgs.LogsDirectory);

        var ledgerEntries = new List<CapabilityLedgerEntry>(matrixCapabilities.Count);
        foreach (var capability in matrixCapabilities)
        {
            sourceBindingsByCapability.TryGetValue(capability.CapabilityId, out var bindings);
            bindings ??= new List<GovernedFileHeader>();

            var reconciliation = DetermineReconciliation(bindings);
            ledgerEntries.Add(new CapabilityLedgerEntry
            {
                CapabilityId = capability.CapabilityId,
                Subsystem = capability.Subsystem,
                SpecReference = capability.SpecReference,
                Owner = capability.Owner,
                Status = capability.Status,
                Severity = capability.Severity,
                VerificationTarget = capability.VerificationTarget,
                GovernanceRequired = requiredCapabilitySet.Contains(capability.CapabilityId),
                SourceBindingCount = bindings.Count,
                Reconciliation = reconciliation,
                PromotionContract = new CapabilityPromotionContract
                {
                    OwnerPath = capability.Owner,
                    SpecReference = capability.SpecReference,
                    GateTests = capability.VerificationTarget,
                    LiveArtifactEvidenceId = liveArtifactEvidence.EvidenceId
                },
                SourceBindings = bindings.Select(ToSourceBinding).ToList()
            });
        }

        var matrixCapabilityIdsWithoutGovernedHeader = matrixCapabilities
            .Select(x => x.CapabilityId)
            .Where(id => !sourceBindingsByCapability.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var requiredCapabilityIdsWithoutMatrixEntry = governanceMap.RequiredCapabilityIds
            .Where(id => !matrixCapabilitySet.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var requiredCapabilityIdsWithoutSourceBinding = governanceMap.RequiredCapabilityIds
            .Where(id => !sourceBindingsByCapability.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var statusCounts = ledgerEntries
            .GroupBy(x => x.Status, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var implicitCompleteViolations = CollectImplicitCompleteViolations(ledgerEntries, liveArtifactEvidence);
        var failureGatePassed = implicitCompleteViolations.Count == 0 &&
            (!commandArgs.RequireLiveEvidence || liveArtifactEvidence.HasMinimumEvidence);

        var summary = new CapabilityLedgerSummary
        {
            TotalCapabilities = ledgerEntries.Count,
            GovernedFilesDeclared = governanceMap.GovernedFiles.Count,
            GovernedFilesFound = governedFiles.Count(x => x.FileExists),
            GovernedFilesMissing = governedFiles.Count(x => !x.FileExists),
            CapabilitiesWithSourceBindings = ledgerEntries.Count(x => x.SourceBindingCount > 0),
            StatusCounts = statusCounts,
            CompleteCapabilities = ledgerEntries.Count(x => IsCompleteStatus(x.Status)),
            ImplicitCompleteViolationCount = implicitCompleteViolations.Count
        };

        var generatedAtUtc = DateTime.UtcNow;
        var report = new CapabilityLedgerReport
        {
            GeneratedAtUtc = generatedAtUtc.ToString("O"),
            RepositoryRoot = repositoryRoot,
            MatrixPath = matrixPath,
            GovernanceMapPath = governanceMapPath,
            Capabilities = ledgerEntries,
            GovernedFiles = governedFiles,
            MatrixCapabilityIdsWithoutGovernedHeader = matrixCapabilityIdsWithoutGovernedHeader,
            RequiredCapabilityIdsWithoutMatrixEntry = requiredCapabilityIdsWithoutMatrixEntry,
            RequiredCapabilityIdsWithoutSourceBinding = requiredCapabilityIdsWithoutSourceBinding,
            ImplicitCompleteViolations = implicitCompleteViolations,
            LiveArtifactEvidence = liveArtifactEvidence,
            FailureGatePassed = failureGatePassed,
            Summary = summary
        };

        if (commandArgs.RequireLiveEvidence && !liveArtifactEvidence.HasMinimumEvidence)
        {
            report.ImplicitCompleteViolations.Add("Missing required live artifact evidence under logs/.");
        }

        var outputDirectory = Path.Combine(repositoryRoot, "Results");
        Directory.CreateDirectory(outputDirectory);

        var stamp = generatedAtUtc.ToString("yyyyMMdd_HHmmss");
        var snapshotPath = Path.Combine(outputDirectory, $"capability_ledger_{stamp}.json");
        var latestPath = !string.IsNullOrWhiteSpace(commandArgs.OutputPath)
            ? Path.GetFullPath(commandArgs.OutputPath)
            : Path.Combine(outputDirectory, "capability_ledger_latest.json");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        Directory.CreateDirectory(Path.GetDirectoryName(latestPath)!);
        File.WriteAllText(snapshotPath, json, new UTF8Encoding(false));
        File.WriteAllText(latestPath, json, new UTF8Encoding(false));

        return new CapabilityLedgerResult
        {
            SnapshotPath = snapshotPath,
            LatestPath = latestPath,
            CapabilityCount = ledgerEntries.Count,
            FailureGatePassed = report.FailureGatePassed,
            LiveArtifactEvidenceId = liveArtifactEvidence.EvidenceId
        };
    }

    private static CapabilityLedgerArguments ParseArguments(string[] args)
    {
        var parsed = new CapabilityLedgerArguments();
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--require-live-evidence", StringComparison.OrdinalIgnoreCase))
            {
                parsed.RequireLiveEvidence = true;
                continue;
            }

            if (arg.StartsWith("--logs-dir=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.LogsDirectory = arg.Substring("--logs-dir=".Length);
                continue;
            }

            if (string.Equals(arg, "--logs-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for --logs-dir");
                }

                parsed.LogsDirectory = args[++i];
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown capability-ledger option: {arg}");
            }

            if (string.IsNullOrWhiteSpace(parsed.OutputPath))
            {
                parsed.OutputPath = arg;
                continue;
            }

            throw new ArgumentException($"Unexpected capability-ledger argument: {arg}");
        }

        return parsed;
    }

    private static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var docsPath = Path.Combine(current.FullName, "docs", "COMPLIANCE_MATRIX.md");
            var mapPath = Path.Combine(current.FullName, "docs", "spec_governance_map.json");
            if (File.Exists(docsPath) && File.Exists(mapPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not resolve repository root from current working directory.");
    }

    private static List<MatrixCapabilityRow> ParseComplianceMatrix(string matrixPath)
    {
        var rows = new List<MatrixCapabilityRow>();

        foreach (var raw in File.ReadAllLines(matrixPath))
        {
            var line = raw.Trim();
            if (!line.StartsWith("|", StringComparison.Ordinal) || line.StartsWith("| ---", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Trim('|').Split('|').Select(static x => x.Trim()).ToArray();
            if (cells.Length < 7)
            {
                continue;
            }

            if (cells[0].Equals("Capability ID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Regex.IsMatch(cells[0], "^[A-Z0-9\\-]+$"))
            {
                continue;
            }

            rows.Add(new MatrixCapabilityRow
            {
                CapabilityId = cells[0],
                Subsystem = cells[1],
                SpecReference = cells[2],
                Owner = cells[3],
                Status = cells[4],
                Severity = cells[5],
                VerificationTarget = cells[6]
            });
        }

        return rows;
    }

    private static GovernanceMap LoadGovernanceMap(string path)
    {
        var json = File.ReadAllText(path);
        var map = JsonSerializer.Deserialize<GovernanceMap>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        return map ?? new GovernanceMap();
    }

    private static List<GovernedFileHeader> ReadGovernedFileHeaders(string repositoryRoot, IReadOnlyList<string> governedFiles)
    {
        var results = new List<GovernedFileHeader>(governedFiles.Count);

        foreach (var relativePath in governedFiles)
        {
            var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(repositoryRoot, normalizedRelative);
            if (!File.Exists(fullPath))
            {
                results.Add(new GovernedFileHeader
                {
                    RelativePath = relativePath,
                    FileExists = false
                });
                continue;
            }

            var headLines = File.ReadLines(fullPath).Take(40);
            var head = string.Join('\n', headLines);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in HeaderRegex.Matches(head))
            {
                values[match.Groups["key"].Value] = match.Groups["value"].Value.Trim();
            }

            values.TryGetValue("SpecRef", out var specRef);
            values.TryGetValue("CapabilityId", out var capabilityId);
            values.TryGetValue("Determinism", out var determinism);
            values.TryGetValue("FallbackPolicy", out var fallbackPolicy);

            results.Add(new GovernedFileHeader
            {
                RelativePath = relativePath,
                FileExists = true,
                CapabilityId = capabilityId,
                SpecRef = specRef,
                Determinism = determinism,
                FallbackPolicy = fallbackPolicy,
                HeaderComplete = !string.IsNullOrWhiteSpace(specRef)
                    && !string.IsNullOrWhiteSpace(capabilityId)
                    && !string.IsNullOrWhiteSpace(determinism)
                    && !string.IsNullOrWhiteSpace(fallbackPolicy)
            });
        }

        return results;
    }

    private static SourceBindingEntry ToSourceBinding(GovernedFileHeader header)
    {
        return new SourceBindingEntry
        {
            RelativePath = header.RelativePath,
            SpecRef = header.SpecRef ?? string.Empty,
            Determinism = header.Determinism ?? string.Empty,
            FallbackPolicy = header.FallbackPolicy ?? string.Empty,
            HeaderComplete = header.HeaderComplete
        };
    }

    private static string DetermineReconciliation(IReadOnlyList<GovernedFileHeader> bindings)
    {
        if (bindings.Count == 0)
        {
            return "MissingSourceBinding";
        }

        if (bindings.Any(x => !x.HeaderComplete))
        {
            return "IncompleteSourceHeader";
        }

        return "Mapped";
    }

    private static bool IsCompleteStatus(string status)
    {
        return string.Equals(status?.Trim(), "Complete", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CollectImplicitCompleteViolations(
        IReadOnlyList<CapabilityLedgerEntry> entries,
        LiveArtifactEvidence liveArtifactEvidence)
    {
        var violations = new List<string>();

        foreach (var entry in entries)
        {
            if (!IsCompleteStatus(entry.Status))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.SpecReference))
            {
                violations.Add($"{entry.CapabilityId}: Complete without SpecReference.");
            }

            if (string.IsNullOrWhiteSpace(entry.Owner))
            {
                violations.Add($"{entry.CapabilityId}: Complete without Owner path.");
            }

            if (string.IsNullOrWhiteSpace(entry.VerificationTarget))
            {
                violations.Add($"{entry.CapabilityId}: Complete without VerificationTarget gate.");
            }

            if (entry.SourceBindingCount <= 0 || !string.Equals(entry.Reconciliation, "Mapped", StringComparison.Ordinal))
            {
                violations.Add($"{entry.CapabilityId}: Complete without mapped governed source binding.");
            }

            if (!liveArtifactEvidence.HasMinimumEvidence)
            {
                violations.Add($"{entry.CapabilityId}: Complete without live artifact evidence.");
            }
        }

        return violations;
    }

    private static LiveArtifactEvidence EvaluateLiveArtifactEvidence(string repositoryRoot, string logsOverride)
    {
        var logsDirectory = string.IsNullOrWhiteSpace(logsOverride)
            ? Path.Combine(repositoryRoot, "logs")
            : Path.GetFullPath(logsOverride);

        var checks = new List<LiveArtifactCheck>
        {
            CreateArtifactCheck(logsDirectory, "debug_screenshot.png", required: true),
            CreateArtifactCheck(logsDirectory, "dom_dump.txt", required: true),
            CreateArtifactCheck(logsDirectory, "js_debug.log", required: true)
        };

        var fenLogs = Directory.Exists(logsDirectory)
            ? Directory.GetFiles(logsDirectory, "fenbrowser_*.log", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        if (fenLogs.Length > 0)
        {
            var latestFenLog = fenLogs
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .First();

            checks.Add(new LiveArtifactCheck
            {
                Name = "fenbrowser-log-latest",
                RelativePath = latestFenLog.Name,
                FullPath = latestFenLog.FullName,
                Required = false,
                Exists = true,
                SizeBytes = latestFenLog.Length,
                LastWriteUtc = latestFenLog.LastWriteTimeUtc.ToString("O")
            });
        }
        else
        {
            checks.Add(new LiveArtifactCheck
            {
                Name = "fenbrowser-log-latest",
                RelativePath = "fenbrowser_*.log",
                FullPath = Path.Combine(logsDirectory, "fenbrowser_*.log"),
                Required = false,
                Exists = false
            });
        }

        var requiredChecks = checks.Where(x => x.Required).ToList();
        var hasMinimumEvidence = requiredChecks.All(x => x.Exists && x.SizeBytes > 0);

        var evidenceId = string.Empty;
        if (hasMinimumEvidence)
        {
            var material = string.Join(
                "|",
                requiredChecks
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .Select(x => $"{x.Name}:{x.RelativePath}:{x.SizeBytes}:{x.LastWriteUtc}"));
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            var hash = Convert.ToHexString(hashBytes).Substring(0, 12).ToLowerInvariant();
            var latestWrite = requiredChecks
                .Where(x => DateTime.TryParse(x.LastWriteUtc, out _))
                .Select(x => DateTime.Parse(x.LastWriteUtc))
                .DefaultIfEmpty(DateTime.UtcNow)
                .Max();
            evidenceId = $"live-{latestWrite:yyyyMMddHHmmss}-{hash}";
        }

        return new LiveArtifactEvidence
        {
            LogsDirectory = logsDirectory,
            EvidenceId = evidenceId,
            HasMinimumEvidence = hasMinimumEvidence,
            RequiredArtifactCount = requiredChecks.Count,
            PresentRequiredArtifactCount = requiredChecks.Count(x => x.Exists && x.SizeBytes > 0),
            Artifacts = checks
        };
    }

    private static LiveArtifactCheck CreateArtifactCheck(string logsDirectory, string fileName, bool required)
    {
        var fullPath = Path.Combine(logsDirectory, fileName);
        var exists = File.Exists(fullPath);
        if (!exists)
        {
            return new LiveArtifactCheck
            {
                Name = fileName,
                RelativePath = fileName,
                FullPath = fullPath,
                Required = required,
                Exists = false
            };
        }

        var info = new FileInfo(fullPath);
        return new LiveArtifactCheck
        {
            Name = fileName,
            RelativePath = fileName,
            FullPath = info.FullName,
            Required = required,
            Exists = true,
            SizeBytes = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc.ToString("O")
        };
    }

    private sealed class MatrixCapabilityRow
    {
        public string CapabilityId { get; set; } = string.Empty;
        public string Subsystem { get; set; } = string.Empty;
        public string SpecReference { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string VerificationTarget { get; set; } = string.Empty;
    }

    private sealed class GovernanceMap
    {
        public List<string> GovernedFiles { get; set; } = new();
        public List<string> RequiredCapabilityIds { get; set; } = new();
    }

    private sealed class CapabilityLedgerArguments
    {
        public string OutputPath { get; set; } = string.Empty;
        public bool RequireLiveEvidence { get; set; }
        public string LogsDirectory { get; set; } = string.Empty;
    }

    private sealed class CapabilityLedgerReport
    {
        public string GeneratedAtUtc { get; set; } = string.Empty;
        public string RepositoryRoot { get; set; } = string.Empty;
        public string MatrixPath { get; set; } = string.Empty;
        public string GovernanceMapPath { get; set; } = string.Empty;
        public List<CapabilityLedgerEntry> Capabilities { get; set; } = new();
        public List<GovernedFileHeader> GovernedFiles { get; set; } = new();
        public List<string> MatrixCapabilityIdsWithoutGovernedHeader { get; set; } = new();
        public List<string> RequiredCapabilityIdsWithoutMatrixEntry { get; set; } = new();
        public List<string> RequiredCapabilityIdsWithoutSourceBinding { get; set; } = new();
        public List<string> ImplicitCompleteViolations { get; set; } = new();
        public LiveArtifactEvidence LiveArtifactEvidence { get; set; } = new();
        public bool FailureGatePassed { get; set; }
        public CapabilityLedgerSummary Summary { get; set; } = new();
    }

    private sealed class CapabilityLedgerSummary
    {
        public int TotalCapabilities { get; set; }
        public int GovernedFilesDeclared { get; set; }
        public int GovernedFilesFound { get; set; }
        public int GovernedFilesMissing { get; set; }
        public int CapabilitiesWithSourceBindings { get; set; }
        public int CompleteCapabilities { get; set; }
        public int ImplicitCompleteViolationCount { get; set; }
        public Dictionary<string, int> StatusCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CapabilityLedgerEntry
    {
        public string CapabilityId { get; set; } = string.Empty;
        public string Subsystem { get; set; } = string.Empty;
        public string SpecReference { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string VerificationTarget { get; set; } = string.Empty;
        public bool GovernanceRequired { get; set; }
        public int SourceBindingCount { get; set; }
        public string Reconciliation { get; set; } = string.Empty;
        public CapabilityPromotionContract PromotionContract { get; set; } = new();
        public List<SourceBindingEntry> SourceBindings { get; set; } = new();
    }

    private sealed class CapabilityPromotionContract
    {
        public string OwnerPath { get; set; } = string.Empty;
        public string SpecReference { get; set; } = string.Empty;
        public string GateTests { get; set; } = string.Empty;
        public string LiveArtifactEvidenceId { get; set; } = string.Empty;
    }

    private sealed class SourceBindingEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public string SpecRef { get; set; } = string.Empty;
        public string Determinism { get; set; } = string.Empty;
        public string FallbackPolicy { get; set; } = string.Empty;
        public bool HeaderComplete { get; set; }
    }

    private sealed class LiveArtifactEvidence
    {
        public string LogsDirectory { get; set; } = string.Empty;
        public string EvidenceId { get; set; } = string.Empty;
        public bool HasMinimumEvidence { get; set; }
        public int RequiredArtifactCount { get; set; }
        public int PresentRequiredArtifactCount { get; set; }
        public List<LiveArtifactCheck> Artifacts { get; set; } = new();
    }

    private sealed class LiveArtifactCheck
    {
        public string Name { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool Required { get; set; }
        public bool Exists { get; set; }
        public long SizeBytes { get; set; }
        public string LastWriteUtc { get; set; } = string.Empty;
    }

    private sealed class GovernedFileHeader
    {
        public string RelativePath { get; set; } = string.Empty;
        public bool FileExists { get; set; }
        public string CapabilityId { get; set; } = string.Empty;
        public string SpecRef { get; set; } = string.Empty;
        public string Determinism { get; set; } = string.Empty;
        public string FallbackPolicy { get; set; } = string.Empty;
        public bool HeaderComplete { get; set; }
    }
}

internal sealed class CapabilityLedgerResult
{
    public string SnapshotPath { get; set; } = string.Empty;
    public string LatestPath { get; set; } = string.Empty;
    public int CapabilityCount { get; set; }
    public bool FailureGatePassed { get; set; }
    public string LiveArtifactEvidenceId { get; set; } = string.Empty;
}
