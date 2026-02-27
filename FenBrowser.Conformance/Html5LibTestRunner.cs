// =============================================================================
// Html5LibTestRunner.cs
// html5lib Tree Construction Test Runner
//
// PURPOSE: Run html5lib-tests tree construction tests against FenBrowser's
//          HTML parser to validate spec compliance. Tests parse HTML fragments
//          and verify the resulting DOM tree structure.
//
// SPEC: https://github.com/html5lib/html5lib-tests
// =============================================================================

using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.HTML;

namespace FenBrowser.Conformance;

/// <summary>
/// Runner for html5lib tree construction tests (.dat files).
/// Parses HTML input, builds a DOM tree using FenBrowser's parser,
/// then compares against expected tree structure from the test file.
/// </summary>
public sealed class Html5LibTestRunner
{
    private readonly string _testDataPath;
    private readonly List<TestResult> _results = new();

    public class TestResult
    {
        public string TestFile { get; set; } = "";
        public int TestIndex { get; set; }
        public string Input { get; set; } = "";
        public string ExpectedTree { get; set; } = "";
        public string ActualTree { get; set; } = "";
        public bool Passed { get; set; }
        public string? Error { get; set; }
    }

    public Html5LibTestRunner(string testDataPath)
    {
        _testDataPath = testDataPath;
    }

    /// <summary>
    /// Discover all .dat test files.
    /// </summary>
    public List<string> DiscoverTests()
    {
        if (!Directory.Exists(_testDataPath))
            return new List<string>();

        // html5lib-tests structure: tree-construction/*.dat
        var treeDir = Path.Combine(_testDataPath, "tree-construction");
        if (Directory.Exists(treeDir))
        {
            return Directory.GetFiles(treeDir, "*.dat", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f)
                .ToList();
        }

        // Fallback: look for .dat files directly
        return Directory.GetFiles(_testDataPath, "*.dat", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();
    }

    /// <summary>
    /// Run all tree construction tests.
    /// </summary>
    public async Task<IReadOnlyList<TestResult>> RunAllAsync(
        Action<string, int>? onProgress = null,
        int maxTests = int.MaxValue)
    {
        _results.Clear();
        var datFiles = DiscoverTests();
        int globalCount = 0;

        foreach (var datFile in datFiles)
        {
            var tests = await ParseDatFileAsync(datFile);

            foreach (var test in tests)
            {
                if (globalCount >= maxTests) break;
                globalCount++;

                var result = RunSingleTest(datFile, test.Index, test.Input, test.ExpectedTree, test.ScriptingEnabled);
                _results.Add(result);
                onProgress?.Invoke(Path.GetFileName(datFile), globalCount);
            }

            if (globalCount >= maxTests) break;
        }

        return _results.AsReadOnly();
    }

    /// <summary>
    /// Run tests from a single .dat file.
    /// </summary>
    public async Task<IReadOnlyList<TestResult>> RunFileAsync(string datFilePath)
    {
        var fileResults = new List<TestResult>();
        var tests = await ParseDatFileAsync(datFilePath);

        foreach (var test in tests)
        {
            var result = RunSingleTest(datFilePath, test.Index, test.Input, test.ExpectedTree, test.ScriptingEnabled);
            fileResults.Add(result);
            _results.Add(result);
        }

        return fileResults.AsReadOnly();
    }

    /// <summary>
    /// Run a single tree construction test case.
    /// </summary>
    private TestResult RunSingleTest(string datFile, int index, string input, string expectedTree,
        bool scriptingEnabled)
    {
        var result = new TestResult
        {
            TestFile = datFile,
            TestIndex = index,
            Input = input,
            ExpectedTree = expectedTree.TrimEnd()
        };

        try
        {
            // Parse with FenBrowser's parser
            var tokenizer = new HtmlTokenizer(input);
            var builder = new HtmlTreeBuilder(tokenizer);
            var document = builder.Build();

            // Serialize the tree to html5lib test format
            result.ActualTree = SerializeTree(document).TrimEnd();

            // Compare expected vs actual tree
            result.Passed = NormalizeTree(result.ExpectedTree) == NormalizeTree(result.ActualTree);
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            result.ActualTree = $"ERROR: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Parse an html5lib .dat test file into individual test cases.
    /// Format: #data / #errors / #document sections separated by blank lines.
    /// </summary>
    private static async Task<List<(int Index, string Input, string ExpectedTree, bool ScriptingEnabled)>>
        ParseDatFileAsync(
            string datFilePath)
    {
        var tests = new List<(int, string, string, bool)>();
        var lines = await File.ReadAllLinesAsync(datFilePath);

        int testIndex = 0;
        int i = 0;

        while (i < lines.Length)
        {
            // Skip empty lines
            while (i < lines.Length && string.IsNullOrEmpty(lines[i]))
                i++;

            if (i >= lines.Length) break;

            // Expect #data
            if (lines[i] != "#data")
            {
                i++;
                continue;
            }

            i++;

            // Read input data (until next # section)
            var dataLines = new List<string>();
            while (i < lines.Length && !lines[i].StartsWith("#"))
            {
                dataLines.Add(lines[i]);
                i++;
            }

            var input = string.Join("\n", dataLines);

            // Skip #errors section
            if (i < lines.Length && lines[i] == "#errors")
            {
                i++;
                while (i < lines.Length && !lines[i].StartsWith("#"))
                    i++;
            }

            // Skip #new-errors section (if present)
            if (i < lines.Length && lines[i] == "#new-errors")
            {
                i++;
                while (i < lines.Length && !lines[i].StartsWith("#"))
                    i++;
            }

            // Check for #script-on / #script-off
            bool scripting = false;
            if (i < lines.Length && lines[i] == "#script-on")
            {
                scripting = true;
                i++;
                while (i < lines.Length && !lines[i].StartsWith("#"))
                    i++;
            }
            else if (i < lines.Length && lines[i] == "#script-off")
            {
                scripting = false;
                i++;
                while (i < lines.Length && !lines[i].StartsWith("#"))
                    i++;
            }

            // Skip #document-fragment section (if present) — fragment tests need special handling
            if (i < lines.Length && lines[i] == "#document-fragment")
            {
                // Skip fragment tests for now — they require context element
                i++;
                while (i < lines.Length && !lines[i].StartsWith("#"))
                    i++;
            }

            // Expect #document
            if (i < lines.Length && lines[i] == "#document")
            {
                i++;
                var treeLines = new List<string>();
                while (i < lines.Length && !string.IsNullOrEmpty(lines[i]) && lines[i] != "#data")
                {
                    // html5lib tree lines are prefixed with "| "
                    treeLines.Add(lines[i]);
                    i++;
                }

                var expectedTree = string.Join("\n", treeLines);
                testIndex++;
                tests.Add((testIndex, input, expectedTree, scripting));
            }
        }

        return tests;
    }

    /// <summary>
    /// Serialize a DOM tree to html5lib test format.
    /// Each node is displayed with indentation indicating depth.
    /// </summary>
    private static string SerializeTree(Document document)
    {
        var sb = new StringBuilder();
        SerializeNode(document, sb, 0);
        return sb.ToString();
    }

    private static void SerializeNode(Node node, StringBuilder sb, int depth)
    {
        // html5lib format: "| " followed by (depth * 2) spaces, then content
        // depth 0: "| <html>"
        // depth 1: "|   <head>"  (2 spaces)
        // depth 2: "|     \"text\""  (4 spaces)
        var indent = "| " + new string(' ', depth * 2);

        switch (node)
        {
            case Document:
                // Document root — just serialize children
                if (node.ChildNodes != null)
                {
                    foreach (var child in node.ChildNodes)
                        SerializeNode(child, sb, depth);
                }

                break;

            case Element el:
                // Format: | <tagname>
                var tagName = el.TagName?.ToLowerInvariant() ?? "";
                sb.AppendLine($"{indent}<{tagName}>");

                // Attributes in alphabetical order (el.Attributes is NamedNodeMap of Attr)
                if (el.HasAttributes())
                {
                    var attrIndent = "| " + new string(' ', (depth + 1) * 2);
                    var sortedAttrs = new List<(string Name, string Value)>();
                    foreach (var attr in el.Attributes)
                    {
                        sortedAttrs.Add((attr.Name, attr.Value));
                    }

                    sortedAttrs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

                    foreach (var (name, value) in sortedAttrs)
                    {
                        sb.AppendLine($"{attrIndent}{name}=\"{value}\"");
                    }
                }

                // Children
                if (node.ChildNodes != null)
                {
                    foreach (var child in node.ChildNodes)
                        SerializeNode(child, sb, depth + 1);
                }

                break;

            default:
                // Text nodes
                string? text = node.TextContent;
                if (text != null)
                {
                    // Check if this is a comment (node type check)
                    if (node.NodeType == NodeType.Comment)
                    {
                        sb.AppendLine($"{indent}<!-- {text} -->");
                    }
                    else if (node.NodeType == NodeType.DocumentType)
                    {
                        string dtName = "html"; // Default DOCTYPE name
                        try
                        {
                            dtName = ((dynamic)node).Name ?? "html";
                        }
                        catch
                        {
                        }

                        sb.AppendLine($"{indent}<!DOCTYPE {dtName}>");
                    }
                    else // Text
                    {
                        sb.AppendLine($"{indent}\"{text}\"");
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Normalize tree output for comparison (trim whitespace lines, normalize line endings).
    /// </summary>
    private static string NormalizeTree(string tree)
    {
        var lines = tree.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Generate summary report.
    /// </summary>
    public string GenerateSummary()
    {
        int passed = _results.Count(r => r.Passed);
        int failed = _results.Count(r => !r.Passed);
        double passRate = _results.Count > 0 ? (double)passed / _results.Count * 100 : 0;

        var sb = new StringBuilder();
        sb.AppendLine("=== html5lib Tree Construction Tests ===");
        sb.AppendLine();
        sb.AppendLine($"Total:     {_results.Count}");
        sb.AppendLine($"Passed:    {passed}");
        sb.AppendLine($"Failed:    {failed}");
        sb.AppendLine($"Pass Rate: {passRate:F1}%");

        if (failed > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failures (top 20):");
            foreach (var f in _results.Where(r => !r.Passed).Take(20))
            {
                sb.AppendLine($"  [{Path.GetFileName(f.TestFile)}#{f.TestIndex}] input: {Truncate(f.Input, 60)}");
                if (!string.IsNullOrEmpty(f.Error))
                    sb.AppendLine($"    Error: {f.Error}");
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace("\n", "\\n").Replace("\r", "");
        return s.Length <= max ? s : s[..max] + "...";
    }
}
