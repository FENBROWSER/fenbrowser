using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FenBrowser.Tests.Architecture;

public class SpecGovernanceTests
{
    private static readonly string[] GovernedFiles =
    [
        "FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs",
        "FenBrowser.FenEngine/Core/EventLoop/MicrotaskQueue.cs",
        "FenBrowser.FenEngine/DOM/NodeListWrapper.cs",
        "FenBrowser.FenEngine/DOM/DomMutationQueue.cs",
        "FenBrowser.FenEngine/Rendering/Css/CssSyntaxParser.cs",
        "FenBrowser.FenEngine/Rendering/Css/CascadeEngine.cs",
        "FenBrowser.FenEngine/Rendering/Css/SelectorMatcher.cs",
        "FenBrowser.FenEngine/Layout/MinimalLayoutComputer.cs",
        "FenBrowser.FenEngine/Rendering/Css/CssFlexLayout.cs",
        "FenBrowser.FenEngine/Layout/GridLayoutComputer.cs",
        "FenBrowser.FenEngine/Rendering/PaintTree/NewPaintTreeBuilder.cs",
        "FenBrowser.FenEngine/Core/FenRuntime.cs",
        "FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs",
        "FenBrowser.Core/WebIDL/WebIdlParser.cs",
        "FenBrowser.Core/Network/Handlers/HttpHandler.cs",
        "FenBrowser.Core/Security/CspPolicy.cs",
        "FenBrowser.Core/Storage/BrowserCookieJar.cs",
        "FenBrowser.Host/ProcessIsolation/RendererIpc.cs",
        "FenBrowser.Host/ProcessIsolation/BrokeredProcessIsolationCoordinator.cs",
        "FenBrowser.Core/Accessibility/AccessibilityTreeBuilder.cs",
        "FenBrowser.DevTools/Domains/RuntimeDomain.cs",
        "FenBrowser.FenEngine/Testing/WPTTestRunner.cs",
        "FenBrowser.Test262/Program.cs"
    ];

    private static readonly string[] RequiredGovernedCapabilityIds =
    [
        "EVENTLOOP-MACROTASK-01",
        "EVENTLOOP-MICROTASK-01",
        "DOM-NODELIST-LIVE-01",
        "DOM-MUTATION-OBSERVER-01",
        "CSS-PARSER-RECOVERY-01",
        "CSS-CASCADE-ORDER-01",
        "CSS-SELECTOR-MATCH-01",
        "LAYOUT-FORMATTING-CONTEXT-01",
        "LAYOUT-FLEX-ALGO-01",
        "LAYOUT-GRID-TRACKS-01",
        "PAINT-STACKING-ORDER-01",
        "JS-BUILTINS-ES2024-01",
        "JS-RUNTIME-EXCEPTION-01",
        "WEBIDL-GENERATION-ORDER-01",
        "FETCH-CORS-POLICY-01",
        "SECURITY-CSP-ENFORCEMENT-01",
        "SECURITY-COOKIE-MODEL-01",
        "PROCESS-IPC-HANDSHAKE-01",
        "PROCESS-SANDBOX-FAILCLOSED-01",
        "A11Y-TREE-ROLE-MAP-01",
        "DEVTOOLS-RUNTIME-SIGNAL-01",
        "VERIFY-WPT-TRUTH-01",
        "VERIFY-TEST262-TRUTH-01"
    ];

    private static readonly HashSet<string> AllowedDeterminism =
        new(StringComparer.OrdinalIgnoreCase) { "strict", "best-effort" };

    private static readonly HashSet<string> AllowedFallbackPolicy =
        new(StringComparer.OrdinalIgnoreCase) { "clean-unsupported", "spec-defined" };

    [Fact]
    public void ComplianceMatrix_DefinesUniqueCapabilityIds()
    {
        var matrixPath = Path.Combine(GetRepoRoot(), "docs", "COMPLIANCE_MATRIX.md");
        Assert.True(File.Exists(matrixPath), $"Missing matrix file: {matrixPath}");

        var ids = ParseCapabilityIds(matrixPath);
        Assert.True(ids.Count > 0, "No capability IDs found in docs/COMPLIANCE_MATRIX.md.");

        var duplicateIds = ids
            .GroupBy(x => x, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(duplicateIds.Count == 0, "Duplicate capability IDs found:\n" + string.Join("\n", duplicateIds));
    }

    [Fact]
    public void GovernedFiles_HaveRequiredSpecHeaders_AndIdsExistInMatrix()
    {
        var root = GetRepoRoot();
        var matrixPath = Path.Combine(root, "docs", "COMPLIANCE_MATRIX.md");
        var matrixIds = new HashSet<string>(ParseCapabilityIds(matrixPath), StringComparer.Ordinal);

        var failures = new List<string>();
        foreach (var relativePath in GovernedFiles)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                failures.Add($"{relativePath}: file missing");
                continue;
            }

            var source = File.ReadAllText(fullPath);
            if (!TryReadHeaderValue(source, "SpecRef", out var specRef) || string.IsNullOrWhiteSpace(specRef))
            {
                failures.Add($"{relativePath}: missing SpecRef header");
            }

            if (!TryReadHeaderValue(source, "CapabilityId", out var capabilityId) || string.IsNullOrWhiteSpace(capabilityId))
            {
                failures.Add($"{relativePath}: missing CapabilityId header");
            }
            else if (!matrixIds.Contains(capabilityId))
            {
                failures.Add($"{relativePath}: CapabilityId '{capabilityId}' not found in COMPLIANCE_MATRIX");
            }

            if (!TryReadHeaderValue(source, "Determinism", out var determinism) || !AllowedDeterminism.Contains(determinism))
            {
                failures.Add($"{relativePath}: invalid Determinism header");
            }

            if (!TryReadHeaderValue(source, "FallbackPolicy", out var fallback) || !AllowedFallbackPolicy.Contains(fallback))
            {
                failures.Add($"{relativePath}: invalid FallbackPolicy header");
            }
        }

        Assert.True(failures.Count == 0, "Spec governance failures:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void RequiredCapabilityIds_AreMappedToGovernedSourceFiles()
    {
        var root = GetRepoRoot();
        var mapped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var relativePath in GovernedFiles)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var source = File.ReadAllText(fullPath);
            if (TryReadHeaderValue(source, "CapabilityId", out var capabilityId) && !string.IsNullOrWhiteSpace(capabilityId))
            {
                mapped.Add(capabilityId);
            }
        }

        var missing = RequiredGovernedCapabilityIds
            .Where(id => !mapped.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, "Required capability IDs not mapped to governed source headers:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void DocsIndex_ReferencesGovernanceArtifacts()
    {
        var indexPath = Path.Combine(GetRepoRoot(), "docs", "INDEX.md");
        var index = File.ReadAllText(indexPath);

        Assert.Contains("SPECS.md", index, StringComparison.Ordinal);
        Assert.Contains("COMPLIANCE_MATRIX.md", index, StringComparison.Ordinal);
        Assert.Contains("PROCESS_OWNERSHIP.md", index, StringComparison.Ordinal);
    }

    private static bool TryReadHeaderValue(string source, string key, out string value)
    {
        // Keep header scanning strict near file start.
        var head = string.Join('\n', source.Split('\n').Take(30));
        var pattern = @"^//\s*" + Regex.Escape(key) + @":\s*(.+?)\s*$";
        var match = Regex.Match(head, pattern, RegexOptions.Multiline);
        if (match.Success)
        {
            value = match.Groups[1].Value.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static List<string> ParseCapabilityIds(string matrixPath)
    {
        var lines = File.ReadAllLines(matrixPath);
        var ids = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("|", StringComparison.Ordinal) || line.StartsWith("| ---", StringComparison.Ordinal))
            {
                continue;
            }

            var cols = line.Split('|', StringSplitOptions.TrimEntries);
            // Split includes empty first/last items from leading/trailing pipes.
            if (cols.Length < 3)
            {
                continue;
            }

            var first = cols[1];
            if (string.IsNullOrWhiteSpace(first) || first.Equals("Capability ID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Regex.IsMatch(first, "^[A-Z0-9\\-]+$"))
            {
                ids.Add(first);
            }
        }

        return ids;
    }

    private static string GetRepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}
