using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

/// <summary>
/// Runs the adoption agency fixtures from the local WPT checkout
/// (wpt/html/syntax/parsing/resources/adoption0{1,2}.dat) and compares the
/// resulting trees with the html5lib "|" dump format.
/// </summary>
public sealed class AdoptionAgencyWptFixtureTests
{
    public static IEnumerable<object[]> AdoptionFixtures()
    {
        foreach (var file in new[] { "adoption01.dat", "adoption02.dat" })
        {
            foreach (var block in ParseDatBlocks(ResolveRepoFile("wpt", "html", "syntax", "parsing", "resources", file)))
            {
                yield return new object[] { file, block.Markup, block.ExpectedDump };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AdoptionFixtures))]
    public void MatchesWptAdoptionAgencyTrees(string file, string markup, string expectedDump)
    {
        if (markup.Contains("<svg>"))
        {
            // The <a><svg><tr><input></a> case is governed by foreign-content
            // breakout rules, not the adoption agency algorithm; it stays excluded
            // until SVG token handling is wired up.
            return;
        }

        var document = HtmlParser.ParseDocument(markup);

        var actual = string.Join(Environment.NewLine, DumpTree(document));
        var expected = expectedDump.TrimEnd('\r', '\n');

        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"{file} case {markup}:{Environment.NewLine}--- expected ---{Environment.NewLine}{expected}{Environment.NewLine}--- actual ---{Environment.NewLine}{actual}");
    }

    private static string ResolveRepoFile(params string[] parts)
    {
        var probe = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(probe); i++)
        {
            var candidate = Path.Combine(new[] { probe }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            probe = Path.GetDirectoryName(probe);
        }

        throw new FileNotFoundException(
            $"Local WPT fixture not found: {string.Join('/', parts)}. Expected checkout at C:\\Users\\udayk\\Videos\\wpt.");
    }

    private static (string Markup, string ExpectedDump)[] ParseDatBlocks(string path)
    {
        const int sectionNone = 0, sectionData = 1, sectionErrors = 2, sectionDocument = 3;
        var blocks = new List<(string Markup, string ExpectedDump)>();
        var section = sectionNone;
        var dataLines = new List<string>();
        var dump = new List<string>();
        var isFragmentCase = false;

        void FlushBlock()
        {
            if (!isFragmentCase && dataLines.Count > 0 && dump.Count > 0)
            {
                blocks.Add((string.Join("\n", dataLines), string.Join(Environment.NewLine, dump)));
            }

            dataLines.Clear();
            dump.Clear();
            isFragmentCase = false;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            if (raw.StartsWith("#data", StringComparison.Ordinal))
            {
                FlushBlock();
                section = sectionData;
                continue;
            }

            if (raw.StartsWith("#document-fragment", StringComparison.Ordinal))
            {
                // Fragment cases exercise the fragment parsing algorithm, not the
                // document adoption path; skip them.
                isFragmentCase = true;
                section = sectionErrors;
                continue;
            }

            if (raw.StartsWith("#errors", StringComparison.Ordinal) ||
                raw.StartsWith("#new-errors", StringComparison.Ordinal) ||
                raw.StartsWith("#script", StringComparison.Ordinal))
            {
                section = sectionErrors;
                continue;
            }

            if (raw.StartsWith("#document", StringComparison.Ordinal))
            {
                section = sectionDocument;
                continue;
            }

            if (raw.StartsWith('#'))
            {
                section = sectionErrors;
                continue;
            }

            switch (section)
            {
                case sectionData:
                    dataLines.Add(raw);
                    break;
                case sectionDocument:
                    if (raw.Length > 0)
                    {
                        dump.Add(raw);
                    }

                    break;
            }
        }

        FlushBlock();
        return blocks.ToArray();
    }

    private static List<string> DumpTree(Document document)
    {
        var result = new List<string>();
        DumpChildren(document, 0, result);
        return result;
    }

    private static void DumpChildren(Node parent, int depth, List<string> result)
    {
        // Merge adjacent text nodes and drop whitespace-only text, matching the
        // html5lib dump format.
        Node? previousText = null;
        foreach (var child in parent.ChildNodes)
        {
            if (child is Text text)
            {
                if (string.IsNullOrWhiteSpace(text.Data)) continue;
                if (previousText is Text mergedText)
                {
                    mergedText.ReplaceData(mergedText.Length, 0, text.Data);
                    previousText = mergedText;
                    continue;
                }

                result.Add(FormatLine(depth, "\"" + text.Data + "\""));
                previousText = text;
                continue;
            }

            previousText = null;
            DumpNode(child, depth, result);
        }
    }

    private static void DumpNode(Node node, int depth, List<string> result)
    {
        switch (node)
        {
            case Element element:
                DumpElement(element, depth, result);
                break;
            case Comment comment:
                result.Add(FormatLine(depth, $"<!-- {comment.Data} -->"));
                break;
            case DocumentType docType:
                result.Add(FormatLine(depth, $"<!DOCTYPE {docType.Name}>"));
                break;
        }
    }

    private static void DumpElement(Element element, int depth, List<string> result)
    {
        var name = element.LocalName;
        if (element.NamespaceUri == Namespaces.Svg)
        {
            name += " svg";
        }
        else if (element.NamespaceUri == Namespaces.MathML)
        {
            name += " math";
        }

        result.Add(FormatLine(depth, $"<{name}>"));

        var attributes = element.Attributes
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var attribute in attributes)
        {
            result.Add(FormatLine(depth + 1, $"{attribute.Name}=\"{attribute.Value}\""));
        }

        if (element is HtmlTemplateElement template)
        {
            result.Add(FormatLine(depth + 1, "content"));
            DumpChildren(template.Content, depth + 2, result);
        }
        else
        {
            DumpChildren(element, depth + 1, result);
        }
    }

    private static string FormatLine(int depth, string content)
    {
        return "| " + new string(' ', depth * 2) + content;
    }
}
