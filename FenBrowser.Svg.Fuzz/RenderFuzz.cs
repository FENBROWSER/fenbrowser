using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FenBrowser.FenEngine.Adapters;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Svg.Fuzz;

// Properties of the ISvgRenderer seam under hostile input:
// - Render never throws.
// - Success means complete, in-budget pixels; failure means no pixels at all
//   (fail closed) and a bounded reason.
// - Diagnostics stay inside their caps.
// - A render that consistently overruns its time budget is a sandbox finding.
//   Overruns are re-measured before failing so one slow machine moment is not
//   reported as an engine bug.
public sealed class RenderFuzz(ITestOutputHelper output)
{
    private const int OverrunSlackMs = 250;
    private const int OverrunConfirmations = 2;
    private int _admitted;
    private int _rejected;

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(304)]
    public void RendererFailsClosedOnMutatedSeeds(int seed)
    {
        var mutator = new SvgMutator(seed);
        var renderer = new FenSvgRenderer();
        for (int iteration = 0; iteration < FuzzSettings.Iterations; iteration++)
        {
            FuzzCase.Run(nameof(RenderFuzz), seed, iteration, mutator.Next(SeedCorpus.Seeds),
                input => RenderOnce(renderer, input, scriptsInert: false));
        }
        ReportCoverage(seed);
    }

    // The image-context policy browser consumers use: script is inert, so scripted
    // documents reach the whole pipeline instead of stopping at admission.
    [Theory]
    [InlineData(305)]
    [InlineData(306)]
    public void RendererFailsClosedWithInertScript(int seed)
    {
        var mutator = new SvgMutator(seed);
        var renderer = new FenSvgRenderer();
        for (int iteration = 0; iteration < FuzzSettings.Iterations; iteration++)
        {
            FuzzCase.Run(nameof(RenderFuzz), seed, iteration, mutator.Next(SeedCorpus.Seeds),
                input => RenderOnce(renderer, input, scriptsInert: true));
        }
        ReportCoverage(seed);
    }

    // External use documents under the worst case: every same-origin URL resolves to
    // the mutated document itself, and the document instantiates its own ids through
    // them, so loads recurse until the resource depth, time and fail-closed rules hold.
    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public void RendererFailsClosedWithSelfReferencingExternalDocuments(int seed)
    {
        var mutator = new SvgMutator(seed);
        var renderer = new FenSvgRenderer();
        for (int iteration = 0; iteration < FuzzSettings.Iterations; iteration++)
        {
            FuzzCase.Run(nameof(RenderFuzz), seed, iteration, WithExternalUses(mutator.Next(SeedCorpus.Seeds)),
                input => RenderOnce(renderer, input, scriptsInert: true, externalDocuments: true));
        }
        ReportCoverage(seed);
    }

    private static readonly Regex IdAttribute = new(@"id=['""]([A-Za-z_][\w-]{0,40})['""]", RegexOptions.CultureInvariant);

    private static string WithExternalUses(string input)
    {
        int close = input.LastIndexOf("</svg>", StringComparison.Ordinal);
        if (close < 0) return input;
        var uses = new StringBuilder();
        foreach (Match match in IdAttribute.Matches(input).Take(3))
        {
            string id = match.Groups[1].Value;
            uses.Append("<use href='sprites.svg#").Append(id).Append("'/>")
                .Append("<use href='other/../sprites.svg#").Append(id).Append("' x='3'/>");
        }
        return input.Insert(close, uses.ToString());
    }

    private sealed class SelfResolver(string document) : ISvgResourceResolver
    {
        public bool TryResolve(Uri absoluteUri, SvgResourceKind kind, out SvgResolvedResource resource, out string error)
        {
            resource = new SvgResolvedResource(absoluteUri, "image/svg+xml", Encoding.UTF8.GetBytes(document));
            error = string.Empty;
            return true;
        }
    }

    [Fact]
    public void RendererFailsClosedOnEverySeed()
    {
        var renderer = new FenSvgRenderer();
        for (int index = 0; index < SeedCorpus.Seeds.Count; index++)
        {
            FuzzCase.Run(nameof(RenderFuzz), -1, index, SeedCorpus.Seeds[index],
                input => RenderOnce(renderer, input, scriptsInert: false));
        }
        ReportCoverage(-1);
    }

    // A mutator that only produces instantly-rejected input would make this
    // target shallow; the split between admitted and rejected renders shows how
    // much of the pipeline a run actually exercised.
    private void ReportCoverage(int seed) =>
        output.WriteLine($"seed={seed} seeds={SeedCorpus.Seeds.Count} admitted={_admitted} rejected={_rejected}");

    private void RenderOnce(FenSvgRenderer renderer, string input, bool scriptsInert, bool externalDocuments = false)
    {
        var limits = SvgRenderLimits.Strict;
        limits.TreatScriptsAsInert = scriptsInert;
        limits.AllowExternalReferences = externalDocuments;
        var resolver = externalDocuments ? new SelfResolver(input) : null;
        long elapsedMs = Measure(renderer, input, limits, resolver, out bool admissible);
        if (admissible) _admitted++; else _rejected++;
        long budgetMs = (long)limits.MaxRenderTimeMs * 2 + OverrunSlackMs;
        if (elapsedMs > budgetMs)
        {
            long fastest = elapsedMs;
            for (int i = 0; i < OverrunConfirmations && fastest > budgetMs; i++)
            {
                fastest = Math.Min(fastest, Measure(renderer, input, limits, resolver, out _));
            }
            FuzzCase.Check(fastest <= budgetMs,
                $"render stays within its time budget (fastest {fastest} ms > {budgetMs} ms, admissible={admissible})");
        }
    }

    private static long Measure(
        FenSvgRenderer renderer, string input, SvgRenderLimits limits, ISvgResourceResolver? resolver, out bool admissible)
    {
        long started = Stopwatch.GetTimestamp();
        using var result = renderer.Render(new SvgRenderRequest(input, limits)
        {
            DiagnosticSource = "fuzz",
            BaseUri = resolver != null ? new Uri("https://fuzz.test/doc/page.svg") : null,
            ResourceResolver = resolver
        });
        long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        FuzzCase.Check(result != null, "render returns a result");
        admissible = SvgRenderResult.IsAdmissible(result!);
        if (result!.Success)
        {
            FuzzCase.Check(admissible, "a successful result is admissible");
            FuzzCase.Check(result.Bitmap != null, "a successful result carries pixels");
            FuzzCase.Check(result.Bitmap!.Width <= limits.MaxRasterWidth &&
                           result.Bitmap.Height <= limits.MaxRasterHeight &&
                           (long)result.Bitmap.Width * result.Bitmap.Height <= limits.MaxRasterPixels,
                "pixels stay inside the raster caps");
        }
        else
        {
            FuzzCase.Check(result.Bitmap == null && result.Picture == null, "a failed result carries no pixels");
            FuzzCase.Check(!string.IsNullOrEmpty(result.ErrorMessage), "a failed result names its reason");
        }

        FuzzCase.Check((result.ErrorMessage?.Length ?? 0) <= FenSvgRenderer.MaxResultDiagnosticChars,
            "the error message is bounded");
        FuzzCase.Check(result.Warnings.Count <= FenSvgRenderer.MaxResultDiagnosticEntries &&
                       result.Warnings.All(w => w.Length <= FenSvgRenderer.MaxResultDiagnosticChars),
            "warnings are bounded");
        return elapsedMs;
    }
}
