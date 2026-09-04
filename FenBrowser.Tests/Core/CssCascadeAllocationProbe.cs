using System;
using System.Collections.Generic;
using System.IO;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Core;

/// <summary>
/// Allocation attribution for the selector-matching hot path. Not an assertion
/// suite - it prints bytes-per-operation so the remaining per-(element, rule)
/// allocation can be located instead of guessed at.
///
/// Run with:
///   dotnet test --filter "FullyQualifiedName~CssCascadeAllocationProbe" -l "console;verbosity=detailed"
/// </summary>
public sealed class CssCascadeAllocationProbe
{
    private readonly ITestOutputHelper _out;

    public CssCascadeAllocationProbe(ITestOutputHelper output) => _out = output;

    private static Element BuildTree(int leaves)
    {
        var root = new Element("html");
        var body = new Element("body");
        root.AppendChild(body);
        var mid = new Element("div");
        mid.SetAttribute("class", "Box color-fg-default d-flex");
        body.AppendChild(mid);
        for (var i = 0; i < leaves; i++)
        {
            var leaf = new Element("span");
            leaf.SetAttribute("class", "text-bold px-3 py-2");
            leaf.SetAttribute("id", "n" + i);
            mid.AppendChild(leaf);
        }

        return root;
    }

    private static long Measure(Action action, int iterations)
    {
        // Warm up so first-call JIT and lazy caches are not counted.
        action();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
    }

    [Fact]
    public void ReportPerMatchAllocation()
    {
        var root = BuildTree(20);
        var mid = (Element)root.FirstChild.FirstChild;
        var leaf = (Element)mid.FirstChild;

        var cases = new (string Name, string Selector)[]
        {
            ("type only",            "span"),
            ("class only",           ".text-bold"),
            ("id only",              "#n0"),
            ("descendant class",     ".Box .text-bold"),
            ("descendant 3 deep",    "body .Box .text-bold"),
            ("child combinator",     ".Box > .text-bold"),
            ("multi-class segment",  ".Box.color-fg-default .text-bold.px-3"),
            ("no match descendant",  ".absent .text-bold"),
            ("attribute",            "span[id]"),
            ("pseudo not",           ".Box :not(.absent)"),
        };

        _out.WriteLine($"{"case",-22} {"bytes/match",12}");
        _out.WriteLine(new string('-', 36));
        foreach (var (name, selector) in cases)
        {
            var chain = SelectorMatcher.ParseSelectorList(selector)[0];
            var bytes = Measure(() => SelectorMatcher.MatchesChain(leaf, chain), 2000);
            _out.WriteLine($"{name,-22} {bytes,12}");
        }

        // Whole-cascade shape: one element against many distinct chains, which is
        // what a real page does.
        var chains = new List<SelectorChain>();
        for (var i = 0; i < 500; i++)
        {
            chains.Add(SelectorMatcher.ParseSelectorList($".Box .c{i}")[0]);
        }

        var perPair = Measure(
            () =>
            {
                foreach (var c in chains)
                {
                    SelectorMatcher.MatchesChain(leaf, c);
                }
            },
            200) / chains.Count;
        _out.WriteLine(new string('-', 36));
        _out.WriteLine($"{"500 distinct chains",-22} {perPair,12} bytes/pair");

        // Same, but walking every element, so ancestor traversal is included.
        var elements = new List<Element>();
        for (var child = mid.FirstChild; child != null; child = child.NextSibling)
        {
            if (child is Element e) elements.Add(e);
        }

        var full = Measure(
            () =>
            {
                foreach (var el in elements)
                {
                    foreach (var c in chains)
                    {
                        SelectorMatcher.MatchesChain(el, c);
                    }
                }
            },
            20) / (elements.Count * (long)chains.Count);
        _out.WriteLine($"{"full element x chain",-22} {full,12} bytes/pair");
    }

    [Fact]
    public void ReportRealStylesheetCascadeAllocation()
    {
        var dir = Path.Combine("..", "..", "..", "..", "Results", "css-stress");
        if (!Directory.Exists(dir))
        {
            _out.WriteLine($"fixture not built ({dir}); run scripts/build_css_stress_fixture.py");
            return;
        }

        var sheets = Directory.GetFiles(dir, "sheet*.css");
        if (sheets.Length == 0)
        {
            _out.WriteLine("no sheets in fixture");
            return;
        }

        var chains = new List<SelectorChain>();
        foreach (var path in sheets)
        {
            var css = File.ReadAllText(path);
            foreach (var raw in ExtractSelectors(css))
            {
                var parsed = SelectorMatcher.ParseSelectorList(raw);
                if (parsed.Count > 0) chains.Add(parsed[0]);
            }
        }

        _out.WriteLine($"sheets={sheets.Length} chains={chains.Count}");
        if (chains.Count == 0) return;

        var root = BuildTree(5);
        var leaf = (Element)root.FirstChild.FirstChild.FirstChild;

        var perPair = Measure(
            () =>
            {
                foreach (var c in chains)
                {
                    SelectorMatcher.MatchesChain(leaf, c);
                }
            },
            5) / chains.Count;
        _out.WriteLine($"real selectors: {perPair} bytes per (element, rule) pair");
    }

    [Fact]
    public void ReportParseAndMatchedRulesAllocation()
    {
        var dir = Path.Combine("..", "..", "..", "..", "Results", "css-stress");
        if (!Directory.Exists(dir))
        {
            _out.WriteLine($"fixture not built ({dir})");
            return;
        }

        var sheets = Directory.GetFiles(dir, "sheet*.css");
        if (sheets.Length == 0) { _out.WriteLine("no sheets"); return; }

        long totalCss = 0;
        var sources = new List<CssLoader.CssSource>();
        for (var i = 0; i < sheets.Length; i++)
        {
            var css = File.ReadAllText(sheets[i]);
            totalCss += css.Length;
            sources.Add(new CssLoader.CssSource
            {
                CssText = css,
                Origin = CssLoader.CssOrigin.External,
                SourceOrder = i,
                SequenceOrder = i,
                BaseUri = new Uri("https://fixture.test/"),
            });
        }

        _out.WriteLine($"sheets={sheets.Length} cssChars={totalCss:N0}");

        var root = BuildTree(40);
        var mid = (Element)root.FirstChild.FirstChild;
        var elements = new List<Element>();
        for (var child = mid.FirstChild; child != null; child = child.NextSibling)
        {
            if (child is Element e) elements.Add(e);
        }

        // First call parses every sheet and populates the per-source rule cache.
        var beforeFirst = GC.GetAllocatedBytesForCurrentThread();
        var first = CssLoader.GetMatchedRules(elements[0], sources);
        var firstBytes = GC.GetAllocatedBytesForCurrentThread() - beforeFirst;
        _out.WriteLine($"first element (includes parse): {firstBytes:N0} bytes, {first.Count} matched rules");

        // Subsequent elements reuse the parsed rules, so this is steady-state
        // per-element cascade cost.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var totalMatched = 0;
        for (var i = 1; i < elements.Count; i++)
        {
            totalMatched += CssLoader.GetMatchedRules(elements[i], sources).Count;
        }

        var perElement = (GC.GetAllocatedBytesForCurrentThread() - before) / (elements.Count - 1);
        _out.WriteLine($"steady-state per element      : {perElement:N0} bytes, " +
                       $"avg {totalMatched / (elements.Count - 1)} matched rules");
        _out.WriteLine($"projected for 900 elements    : {perElement * 900 / (1024 * 1024):N0} MB");
    }

    [Fact]
    public void ReportRepeatCascadeAllocation()
    {
        var dir = Path.Combine("..", "..", "..", "..", "Results", "css-stress");
        if (!Directory.Exists(dir)) { _out.WriteLine("fixture not built"); return; }
        var sheets = Directory.GetFiles(dir, "sheet*.css");
        if (sheets.Length == 0) { _out.WriteLine("no sheets"); return; }

        var texts = new List<string>();
        foreach (var f in sheets) texts.Add(File.ReadAllText(f));

        List<CssLoader.CssSource> NewSources()
        {
            var list = new List<CssLoader.CssSource>();
            for (var i = 0; i < texts.Count; i++)
            {
                list.Add(new CssLoader.CssSource
                {
                    CssText = texts[i],
                    Origin = CssLoader.CssOrigin.External,
                    SourceOrder = i,
                    SequenceOrder = i,
                    BaseUri = new Uri("https://fixture.test/"),
                });
            }

            return list;
        }

        var root = BuildTree(3);
        var leaf = (Element)root.FirstChild.FirstChild.FirstChild;

        // Each pass builds fresh CssSource objects, which is what the real
        // cascade does: cssBlobs is rebuilt on every ComputeWithResultAsync.
        for (var pass = 1; pass <= 5; pass++)
        {
            var sources = NewSources();
            var before = GC.GetAllocatedBytesForCurrentThread();
            CssLoader.GetMatchedRules(leaf, sources);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            _out.WriteLine($"cascade pass {pass} (fresh CssSource objects): {bytes / (1024 * 1024):N0} MB");
        }
    }

    // Crude selector extraction: enough to feed the matcher a realistic mix.
    private static IEnumerable<string> ExtractSelectors(string css)
    {
        var i = 0;
        while (i < css.Length && i < 400_000)
        {
            var brace = css.IndexOf('{', i);
            if (brace < 0) yield break;
            var start = css.LastIndexOf('}', brace);
            var prev = css.LastIndexOf('{', brace - 1 < 0 ? 0 : brace - 1);
            var from = Math.Max(Math.Max(start, prev), i - 1) + 1;
            var sel = css.Substring(from, brace - from).Trim();
            if (sel.Length > 0 && sel.Length < 200 && !sel.StartsWith("@", StringComparison.Ordinal)
                && !sel.Contains('}') && !sel.Contains(';'))
            {
                yield return sel;
            }

            var close = css.IndexOf('}', brace);
            if (close < 0) yield break;
            i = close + 1;
        }
    }
}
