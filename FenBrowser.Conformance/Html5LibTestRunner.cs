// =============================================================================
// Html5LibTestRunner.cs
// html5lib Tree Construction Test Runner
// =============================================================================

using System.Text;
using System.Linq;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;

namespace FenBrowser.Conformance;

public sealed class Html5LibTestRunner
{
    private readonly string _testDataPath;
    private readonly List<TestResult> _results = new();
    private readonly IHtmlTreeOracle? _oracle;
    private readonly bool _differentialMode;

    public bool DifferentialModeEnabled => _differentialMode;
    public bool OracleAvailable => _oracle?.IsAvailable == true;
    public string OracleAvailabilityError => _oracle?.AvailabilityError ?? string.Empty;

    public sealed class TestResult
    {
        public string TestFile { get; set; } = "";
        public int TestIndex { get; set; }
        public string Input { get; set; } = "";
        public string ExpectedTree { get; set; } = "";
        public string ActualTree { get; set; } = "";
        public bool Passed { get; set; }
        public string? Error { get; set; }
        public string FailureCategory { get; set; } = "";
        public string? FragmentContext { get; set; }
        public bool ScriptingEnabled { get; set; }
        public bool OracleCompared { get; set; }
        public bool OracleMatched { get; set; }
        public string? OracleError { get; set; }
    }

    public Html5LibTestRunner(string testDataPath, bool differentialMode = false, IHtmlTreeOracle? oracle = null)
    {
        _testDataPath = testDataPath;
        _differentialMode = differentialMode;
        _oracle = oracle;
    }

    public List<string> DiscoverTests()
    {
        if (!Directory.Exists(_testDataPath))
        {
            return new List<string>();
        }

        var treeDir = Path.Combine(_testDataPath, "tree-construction");
        if (Directory.Exists(treeDir))
        {
            return Directory.GetFiles(treeDir, "*.dat", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f)
                .ToList();
        }

        return Directory.GetFiles(_testDataPath, "*.dat", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();
    }

    public async Task<IReadOnlyList<TestResult>> RunAllAsync(Action<string, int>? onProgress = null, int maxTests = int.MaxValue)
    {
        _results.Clear();
        var datFiles = DiscoverTests();
        var globalCount = 0;

        foreach (var datFile in datFiles)
        {
            var tests = await ParseDatFileAsync(datFile);
            foreach (var test in tests)
            {
                if (globalCount >= maxTests)
                {
                    break;
                }

                globalCount++;
                var result = RunSingleTest(datFile, test.Index, test.Input, test.ExpectedTree, test.ScriptingEnabled, test.FragmentContext);
                _results.Add(result);
                onProgress?.Invoke(Path.GetFileName(datFile), globalCount);
            }

            if (globalCount >= maxTests)
            {
                break;
            }
        }

        return _results.AsReadOnly();
    }

    public async Task<IReadOnlyList<TestResult>> RunFileAsync(string datFilePath)
    {
        var fileResults = new List<TestResult>();
        var tests = await ParseDatFileAsync(datFilePath);
        foreach (var test in tests)
        {
            var result = RunSingleTest(datFilePath, test.Index, test.Input, test.ExpectedTree, test.ScriptingEnabled, test.FragmentContext);
            fileResults.Add(result);
            _results.Add(result);
        }

        return fileResults.AsReadOnly();
    }

    private TestResult RunSingleTest(string datFile, int index, string input, string expectedTree, bool scriptingEnabled, string? fragmentContext)
    {
        var result = new TestResult
        {
            TestFile = datFile,
            TestIndex = index,
            Input = input,
            ExpectedTree = expectedTree.TrimEnd(),
            FragmentContext = fragmentContext,
            ScriptingEnabled = scriptingEnabled
        };

        try
        {
            HtmlParsingOutcome outcome;
            if (!string.IsNullOrWhiteSpace(fragmentContext))
            {
                var doc = Document.CreateHtmlDocument();
                var context = doc.CreateElement(fragmentContext);
                var fragment = HtmlParser.ParseFragment(context, input, options: null, out outcome);
                result.ActualTree = SerializeFragmentTree(context, fragment).TrimEnd();
            }
            else
            {
                var document = HtmlParser.ParseDocument(input, out outcome);
                result.ActualTree = SerializeTree(document).TrimEnd();
            }

            result.Passed = NormalizeTree(result.ExpectedTree) == NormalizeTree(result.ActualTree);
            if (!result.Passed)
            {
                result.FailureCategory = ClassifyFailureCategory(outcome, fragmentContext);
            }

            if (_differentialMode && _oracle != null && _oracle.IsAvailable)
            {
                result.OracleCompared = true;
                var oracle = _oracle.Parse(input, fragmentContext);
                if (oracle.Success)
                {
                    result.OracleMatched = NormalizeTree(result.ActualTree) == NormalizeTree(oracle.Tree);
                }
                else
                {
                    result.OracleError = oracle.Error;
                }
            }
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            result.ActualTree = $"ERROR: {ex.Message}";
            result.FailureCategory = "exception";
        }

        return result;
    }

    private static async Task<List<(int Index, string Input, string ExpectedTree, bool ScriptingEnabled, string? FragmentContext)>> ParseDatFileAsync(string datFilePath)
    {
        var tests = new List<(int, string, string, bool, string?)>();
        var lines = await File.ReadAllLinesAsync(datFilePath);
        var testIndex = 0;
        var i = 0;

        while (i < lines.Length)
        {
            while (i < lines.Length && string.IsNullOrEmpty(lines[i]))
            {
                i++;
            }

            if (i >= lines.Length)
            {
                break;
            }

            if (lines[i] != "#data")
            {
                i++;
                continue;
            }

            i++;
            var dataLines = new List<string>();
            while (i < lines.Length && !lines[i].StartsWith("#"))
            {
                dataLines.Add(lines[i]);
                i++;
            }

            var input = string.Join("\n", dataLines);

            if (i < lines.Length && lines[i] == "#errors")
            {
                i++;
                while (i < lines.Length && !lines[i].StartsWith("#"))
                {
                    i++;
                }
            }

            if (i < lines.Length && lines[i] == "#new-errors")
            {
                i++;
                while (i < lines.Length && !lines[i].StartsWith("#"))
                {
                    i++;
                }
            }

            var scripting = false;
            if (i < lines.Length && lines[i] == "#script-on")
            {
                scripting = true;
                i++;
                while (i < lines.Length && !lines[i].StartsWith("#"))
                {
                    i++;
                }
            }
            else if (i < lines.Length && lines[i] == "#script-off")
            {
                scripting = false;
                i++;
                while (i < lines.Length && !lines[i].StartsWith("#"))
                {
                    i++;
                }
            }

            string? fragmentContext = null;
            if (i < lines.Length && lines[i] == "#document-fragment")
            {
                i++;
                if (i < lines.Length && !lines[i].StartsWith("#"))
                {
                    fragmentContext = lines[i].Trim();
                    i++;
                }

                while (i < lines.Length && !lines[i].StartsWith("#"))
                {
                    i++;
                }
            }

            if (i < lines.Length && lines[i] == "#document")
            {
                i++;
                var treeLines = new List<string>();
                while (i < lines.Length && !string.IsNullOrEmpty(lines[i]) && lines[i] != "#data")
                {
                    treeLines.Add(lines[i]);
                    i++;
                }

                var expectedTree = string.Join("\n", treeLines);
                testIndex++;
                tests.Add((testIndex, input, expectedTree, scripting, fragmentContext));
            }
        }

        return tests;
    }

    private static string SerializeTree(Document document)
    {
        var sb = new StringBuilder();
        SerializeNode(document, sb, 0);
        return sb.ToString();
    }

    private static string SerializeFragmentTree(Element contextElement, DocumentFragment fragment)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"| <{contextElement.LocalName}>");
        for (var child = fragment.FirstChild; child != null; child = child.NextSibling)
        {
            SerializeNode(child, sb, 1);
        }

        return sb.ToString();
    }

    private static void SerializeNode(Node node, StringBuilder sb, int depth)
    {
        var indent = "| " + new string(' ', depth * 2);
        switch (node)
        {
            case Document:
                foreach (var child in node.ChildNodes)
                {
                    SerializeNode(child, sb, depth);
                }
                break;
            case Element el:
                var tagName = el.TagName?.ToLowerInvariant() ?? "";
                sb.AppendLine($"{indent}<{tagName}>");
                if (el.HasAttributes())
                {
                    var attrIndent = "| " + new string(' ', (depth + 1) * 2);
                    var attrs = new List<(string Name, string Value)>();
                    foreach (var attr in el.Attributes)
                    {
                        attrs.Add((attr.Name, attr.Value));
                    }

                    attrs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
                    foreach (var (name, value) in attrs)
                    {
                        sb.AppendLine($"{attrIndent}{name}=\"{value}\"");
                    }
                }

                foreach (var child in node.ChildNodes)
                {
                    SerializeNode(child, sb, depth + 1);
                }
                break;
            default:
                var text = node.TextContent;
                if (text == null)
                {
                    break;
                }

                if (node.NodeType == NodeType.Comment)
                {
                    sb.AppendLine($"{indent}<!-- {text} -->");
                }
                else if (node.NodeType == NodeType.DocumentType)
                {
                    sb.AppendLine($"{indent}<!DOCTYPE html>");
                }
                else
                {
                    sb.AppendLine($"{indent}\"{text}\"");
                }
                break;
        }
    }

    private static string NormalizeTree(string tree)
    {
        var lines = tree.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        return string.Join("\n", lines);
    }

    private static string ClassifyFailureCategory(HtmlParsingOutcome outcome, string? fragmentContext)
    {
        if (!string.IsNullOrWhiteSpace(fragmentContext))
        {
            return "fragment";
        }

        return outcome.ReasonCode switch
        {
            HtmlParsingReasonCode.TokenEmissionLimitExceeded => "tokenization",
            HtmlParsingReasonCode.InputSizeLimitExceeded => "tokenization",
            HtmlParsingReasonCode.OpenElementsDepthLimitExceeded => "tree-construction",
            HtmlParsingReasonCode.MalformedInput => "tree-construction",
            HtmlParsingReasonCode.Exception => "exception",
            _ => "tree-construction"
        };
    }

    public string GenerateSummary()
    {
        var total = _results.Count;
        var passed = _results.Count(r => r.Passed);
        var failed = total - passed;
        var sb = new StringBuilder();
        sb.AppendLine($"[html5lib] Total: {total}");
        sb.AppendLine($"[html5lib] Passed: {passed}");
        sb.AppendLine($"[html5lib] Failed: {failed}");

        if (failed > 0)
        {
            var byCategory = _results
                .Where(r => !r.Passed)
                .GroupBy(r => string.IsNullOrWhiteSpace(r.FailureCategory) ? "unknown" : r.FailureCategory)
                .OrderByDescending(g => g.Count());

            sb.AppendLine("[html5lib] Failure categories:");
            foreach (var group in byCategory)
            {
                sb.AppendLine($"  - {group.Key}: {group.Count()}");
            }
        }

        if (_differentialMode)
        {
            if (_oracle == null)
            {
                sb.AppendLine("[html5lib] Differential: disabled (no oracle configured)");
            }
            else if (!_oracle.IsAvailable)
            {
                sb.AppendLine($"[html5lib] Differential: unavailable ({_oracle.AvailabilityError})");
            }
            else
            {
                var compared = _results.Count(r => r.OracleCompared);
                var matched = _results.Count(r => r.OracleCompared && r.OracleMatched);
                var mismatched = _results.Count(r => r.OracleCompared && !r.OracleMatched);
                var errored = _results.Count(r => r.OracleCompared && !string.IsNullOrWhiteSpace(r.OracleError));
                sb.AppendLine($"[html5lib] Differential: compared={compared} matched={matched} mismatched={mismatched} oracle-errors={errored}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}

