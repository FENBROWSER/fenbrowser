using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using Xunit;

namespace FenBrowser.Svg.Fuzz;

// Property: the bounded XML-subset parser either produces a document with an
// <svg> root or rejects with a bounded fatal reason. It never throws, never
// reports more diagnostics than its caps, and never admits more elements than
// the element budget.
public sealed class MarkupParserFuzz
{
    [Theory]
    [InlineData(101)]
    [InlineData(102)]
    [InlineData(103)]
    [InlineData(104)]
    public void ParserSurvivesMutatedSeeds(int seed)
    {
        var mutator = new SvgMutator(seed);
        for (int iteration = 0; iteration < FuzzSettings.Iterations; iteration++)
        {
            FuzzCase.Run(nameof(MarkupParserFuzz), seed, iteration, mutator.Next(SeedCorpus.Seeds), ParseOnce);
        }
    }

    [Theory]
    [InlineData(111)]
    [InlineData(112)]
    public void ParserSurvivesRandomText(int seed)
    {
        var mutator = new SvgMutator(seed);
        for (int iteration = 0; iteration < FuzzSettings.Iterations; iteration++)
        {
            string input = "<svg xmlns='http://www.w3.org/2000/svg'>" + mutator.RandomText(512) + "</svg>";
            FuzzCase.Run(nameof(MarkupParserFuzz), seed, iteration, input, ParseOnce);
        }
    }

    [Fact]
    public void ParserBoundsDeepNesting()
    {
        string input = "<svg xmlns='http://www.w3.org/2000/svg'>" +
                       string.Concat(Enumerable.Repeat("<g>", 100_000)) +
                       string.Concat(Enumerable.Repeat("</g>", 100_000)) + "</svg>";
        FuzzCase.Run(nameof(MarkupParserFuzz), 0, 0, input, ParseOnce);
    }

    [Fact]
    public void ParserBoundsEntityExpansion()
    {
        string input = "<!DOCTYPE svg [<!ENTITY a 'aaaaaaaaaa'><!ENTITY b '&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;'>" +
                       "<!ENTITY c '&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;'><!ENTITY d '&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;'>]>" +
                       "<svg xmlns='http://www.w3.org/2000/svg'><text>&d;&d;&d;&d;&d;&d;&d;&d;</text></svg>";
        FuzzCase.Run(nameof(MarkupParserFuzz), 0, 1, input, ParseOnce);
    }

    private static void ParseOnce(string input)
    {
        var limits = SvgRenderLimits.Strict;
        bool parsed = SvgMarkupParser.TryParse(input, limits, out var document, out string fatalReason);
        if (!parsed)
        {
            FuzzCase.Check(!string.IsNullOrEmpty(fatalReason), "a rejected parse names its fatal reason");
            FuzzCase.Check(fatalReason.Length <= SvgDiagnosticText.MaxFatalChars,
                "fatal reasons are bounded");
            return;
        }

        FuzzCase.Check(document?.Root != null, "an accepted parse has a root");
        FuzzCase.Check(document!.Root.Name == "svg", "the root is <svg>");
        FuzzCase.Check(document.Report.Warnings.Count <= SvgParseReport.MaxWarnings, "warnings are capped");
        FuzzCase.Check(document.Report.Warnings.All(w => w.Length <= SvgParseReport.MaxWarningLength),
            "warnings are bounded");
        FuzzCase.Check(document.Report.ElementCount <= limits.MaxElementCount,
            "the element budget is enforced");
    }
}
