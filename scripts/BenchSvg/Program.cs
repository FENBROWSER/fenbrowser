using System.Diagnostics;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

// BenchSvg: latency, allocation, and corpus benchmark for the first-party
// ISvgRenderer backend.
// Emits machine-readable JSON lines on stdout; --report also writes a
// markdown summary to Results/svg/perf-report.md. No CSV is generated.
//
// Usage (from repo root):
//   dotnet run --project scripts/BenchSvg/BenchSvg.csproj -c Release [-- --report]
//   dotnet run --project scripts/BenchSvg/BenchSvg.csproj -c Release -- --corpus <directory> [--max-files N] [--gate]

if (args.Length >= 2 && args[0] == "--wpt-documents")
{
    Environment.ExitCode = await WptDocumentRunner.RunAsync(args);
    return;
}
if (args.Length >= 3 && args[0] == "--corpus-worker")
{
    Environment.ExitCode = SvgCorpusRunner.RunWorker(
        args[1], args[2], args.Skip(3).ToArray());
    return;
}
if (args.Length == 4 && args[0] == "--capture-sites" && args[2] == "--capture-output")
{
    try
    {
        Environment.ExitCode = await SvgSiteCorpusCapture.RunAsync(args[1], args[3]);
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or
                               System.Text.Json.JsonException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 2;
    }
    return;
}
if ((args.Length == 2 || args.Length == 4) &&
    args[0] is "--inspect-svg" or "--inspect-inline-svg" or "--inspect-image-svg")
{
    // --inspect-inline-svg treats the file as markup that appeared inline in an HTML
    // page: it goes through the HTML parser (foreign-content namespace and attribute
    // case adjustment) and renders with script inert, as the browser's inline path does.
    // --inspect-image-svg renders the file as an <img> would: script inert and text
    // areas on one line, the policies ImageLoader applies. --inspect-svg keeps the
    // strict renderer contract.
    bool inlineMarkup = args[0] == "--inspect-inline-svg";
    bool browserContext = inlineMarkup || args[0] == "--inspect-image-svg";
    string inspectPath = Path.GetFullPath(args[1]);
    var inspectInfo = new FileInfo(inspectPath);
    if (!inspectInfo.Exists || inspectInfo.Length > SvgRenderLimits.Default.MaxSourceChars)
    {
        Console.Error.WriteLine("inspect SVG is missing or exceeds the source budget");
        Environment.ExitCode = 2;
        return;
    }
    string inspectSource = await File.ReadAllTextAsync(inspectPath);
    var inspectLimits = SvgRenderLimits.Default;
    if (inlineMarkup)
    {
        inspectSource = SerializeInlineSvg(inspectSource) ?? string.Empty;
    }
    if (browserContext)
    {
        inspectLimits.TreatScriptsAsInert = true;
        inspectLimits.LayOutTextAreasOnOneLine = true;
    }
    using var inspectResult = new FenSvgRenderer().Render(inspectSource, inspectLimits);
    bool inspectAdmissible = SvgRenderResult.IsAdmissible(inspectResult);
    if (args.Length == 4)
    {
        if (args[2] != "--output-prefix")
        {
            Console.Error.WriteLine($"{args[0]} optional argument must be --output-prefix <path>");
            Environment.ExitCode = 2;
            return;
        }
        string prefix = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        if (inspectAdmissible && inspectResult.Bitmap != null)
            SaveBitmap(inspectResult.Bitmap, prefix + ".png");
    }
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        probe = "svg-inspect",
        admissible = inspectAdmissible,
        rejection = SvgRenderResult.DescribeRejection(inspectResult),
        warnings = inspectResult.Warnings,
        error = inspectResult.ErrorMessage
    }));
    Environment.ExitCode = inspectAdmissible ? 0 : 1;
    return;
}

static string? SerializeInlineSvg(string markup)
{
    var document = new FenBrowser.Core.Parsing.HtmlParser(
        "<!doctype html><html><body>" + markup + "</body></html>").Parse();
    var pending = new Stack<FenBrowser.Core.Dom.V2.Node>();
    pending.Push(document);
    while (pending.Count > 0)
    {
        var node = pending.Pop();
        if (node is FenBrowser.Core.Dom.V2.Element element &&
            string.Equals(element.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            return element.ToHtml();
        }
        for (var child = node.LastChild; child != null; child = child.PreviousSibling)
        {
            pending.Push(child);
        }
    }
    return null;
}

static void SaveBitmap(SKBitmap bitmap, string path)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, encoded.ToArray());
}

var root = Directory.GetCurrentDirectory();
var resultsDir = Path.Combine(root, "Results", "svg");
bool writeReport = args.Contains("--report");
var reportRows = new List<string>();

string? benchmarkFont = Environment.GetEnvironmentVariable("FEN_SVG_BENCH_FONT");
if (!string.IsNullOrWhiteSpace(benchmarkFont) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
        SvgRendererConfiguration.FontFallbackPathEnvironmentVariable)))
{
    Environment.SetEnvironmentVariable(
        SvgRendererConfiguration.FontFallbackPathEnvironmentVariable,
        benchmarkFont);
}

var nativeTextProbe = ProbeNativeTextStack();
Console.WriteLine(
    $"{{\"probe\":\"native-text-stack\",\"ok\":true," +
    $"\"glyphs\":{nativeTextProbe.Glyphs},\"width\":{nativeTextProbe.Width:0.###}}}");

ISvgRenderer fen = new FenSvgRenderer();

int corpusArgument = Array.IndexOf(args, "--corpus");
if (corpusArgument >= 0)
{
    if (corpusArgument + 1 >= args.Length)
    {
        Console.Error.WriteLine("--corpus requires a directory path");
        Environment.ExitCode = 2;
        return;
    }
    try
    {
        var options = SvgCorpusOptions.Parse(args, corpusArgument + 1, resultsDir);
        Environment.ExitCode = SvgCorpusRunner.Run(options);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 2;
    }
    return;
}

string redPng = MakeRedPng(8, 8);
string nestedSvg = "data:image/svg+xml;base64," + Convert.ToBase64String(
    System.Text.Encoding.UTF8.GetBytes(
        "<svg width='8' height='8'><rect width='8' height='8' fill='#9b59b6'/></svg>"));

var cases = new (string Name, string Svg)[]
{
    ("tiny-icon", "<svg width='16' height='16'><circle cx='8' cy='8' r='6' fill='#e74c3c'/></svg>"),
    ("medium-logo", "<svg width='128' height='64'>" +
        "<rect width='128' height='64' rx='12' fill='#2c3e50'/>" +
        "<circle cx='32' cy='32' r='18' fill='#3498db'/>" +
        "<path d='M60 44 L80 20 L100 44 Z' fill='#f1c40f'/>" +
        "<rect x='104' y='24' width='16' height='16' fill='#2ecc71'/></svg>"),
    ("path-heavy", BuildPathHeavy()),
    ("gradient-heavy", BuildGradientHeavy()),
    ("deep-nested", BuildDeepNested(24)),
    ("malformed-adversarial", "<svg width='50' height='50'><rect width='30' height='50' fill='red'>" +
        "</nope><g transform='scale(1e9)'><circle cx='10' cy='10' r='5' fill='blue'/></g>" +
        "<path d='M0 0 L1e30 1e30' stroke='green' stroke-width='2'/></svg>"),
    ("embedded-image", $"<svg width='48' height='48'><image href='{redPng}' x='4' y='4' width='40' height='40'/></svg>"),
    ("embedded-svg", $"<svg width='48' height='48'><image href='{nestedSvg}' x='4' y='4' width='40' height='40'/></svg>"),
    ("basic-text", "<svg width='160' height='40'><text x='4' y='28' font-size='24'>FenBrowser</text></svg>"),
};

if (args.Contains("--stress-only"))
{
    bool stressOk = RunStress(
        fen,
        cases.Select(item => item.Svg).Append(
            "<svg width='80' height='30'><text x='2' y='20'>A<tspan>B</tspan></text></svg>")
            .Concat(new[]
            {
                "<svg width='40' height='30'><defs><filter id='f'><feGaussianBlur stdDeviation='2'/></filter></defs><rect x='8' y='5' width='20' height='20' filter='url(#f)'/></svg>",
                "<svg width='40' height='30'><defs><mask id='m' mask-type='alpha' maskContentUnits='objectBoundingBox'><rect width='.5' height='1'/></mask></defs><rect x='5' y='5' width='30' height='20' mask='url(#m)'/></svg>",
                "<svg width='50' height='30'><defs><marker id='m' markerWidth='4' markerHeight='4' refX='2' refY='2' markerUnits='userSpaceOnUse'><circle cx='2' cy='2' r='2'/></marker></defs><line x1='5' y1='15' x2='40' y2='15' stroke='black' marker-end='url(#m)'/></svg>",
                "<svg width='160' height='40'><defs><path id='p' d='M4 28 H156'/></defs><text font-size='18'><textPath href='#p'>FenBrowser</textPath></text></svg>",
                "<svg width='120' height='40'><path id='p' d='M10 20 H110'/><circle r='4'><animateMotion dur='1s' keyPoints='.5;.5' keyTimes='0;1'><mpath href='#p'/></animateMotion></circle></svg>"
            })
            .ToArray(),
        iterations: 10_000);
    Environment.ExitCode = stressOk ? 0 : 1;
    return;
}

const int WarmRuns = 7;

foreach (var (name, svg) in cases)
{
    var cold = TimeOne(fen, svg);
    Console.WriteLine($"{{\"case\":\"{name}\",\"phase\":\"cold\"," +
                      $"\"ms\":{cold.ElapsedMs},\"admissible\":{Cold(cold.Admissible)}}}");

    long best = long.MaxValue;
    long allocSum = 0;
    bool admissible = true;
    string dims = "-";
    ulong checksum = 0;
    for (int i = 0; i < WarmRuns; i++)
    {
        var m = TimeOne(fen, svg);
        admissible &= m.Admissible;
        best = Math.Min(best, m.ElapsedMs);
        allocSum += m.AllocatedBytes;
        if (i == 0)
        {
            dims = $"{m.Width}x{m.Height}";
            checksum = m.Checksum;
        }
    }
    long avgAlloc = allocSum / WarmRuns;
    Console.WriteLine($"{{\"case\":\"{name}\",\"phase\":\"warm-best\"," +
                      $"\"ms\":{best},\"avgAllocBytes\":{avgAlloc}," +
                      $"\"admissible\":{Cold(admissible)}," +
                      $"\"dims\":\"{dims}\",\"checksum\":{checksum}}}");
    reportRows.Add($"| {name} | {cold.ElapsedMs} | {best} | {avgAlloc} | " +
                   $"{(admissible ? "yes" : "NO")} | {dims} | {checksum} |");
}

if (writeReport)
{
    Directory.CreateDirectory(resultsDir);
    var lines = new List<string>
    {
        "# SVG Renderer Benchmark",
        "",
        $"Generated: {DateTime.UtcNow:u}",
        "Renderer: first-party (FenSvgRenderer)",
        $"Warm runs per case: {WarmRuns} (best-of reported); allocations averaged across warm runs.",
        "A case is admissible only when every run is admissible under SvgRenderResult.IsAdmissible.",
        "Checksums are a sampled ARGB fingerprint of the rendered output; a case is reproducible when repeated runs match.",
        "",
        "| case | cold ms | warm best ms | avg alloc B | admissible | dims | checksum |",
        "|---|---:|---:|---:|---|---|---|"
    };
    lines.AddRange(reportRows);
    File.WriteAllLines(Path.Combine(resultsDir, "perf-report.md"), lines);
    Console.WriteLine("report written: Results/svg/perf-report.md");
}

static string Cold(bool b) => b ? "true" : "false";

static bool RunStress(ISvgRenderer renderer, string[] documents, int iterations)
{
    const long maxRetainedGrowthBytes = 64L * 1024 * 1024;
    int failures = 0;
    int inadmissible = 0;

    for (int i = 0; i < 256; i++)
    {
        using var warm = renderer.Render(documents[i % documents.Length]);
        CountRender(warm, ref failures, ref inadmissible);
    }
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    long before = process.PrivateMemorySize64;

    for (int i = 0; i < iterations; i++)
    {
        using var result = renderer.Render(documents[i % documents.Length]);
        CountRender(result, ref failures, ref inadmissible);
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    process.Refresh();
    long after = process.PrivateMemorySize64;
    long retainedGrowth = Math.Max(0L, after - before);
    bool ok = failures == 0 && retainedGrowth <= maxRetainedGrowthBytes;
    Console.WriteLine(
        $"{{\"probe\":\"dispose-stress\",\"ok\":{Cold(ok)}," +
        $"\"iterations\":{iterations},\"failures\":{failures}," +
        $"\"inadmissible\":{inadmissible},\"retainedPrivateBytes\":{retainedGrowth}," +
        $"\"limitBytes\":{maxRetainedGrowthBytes}}}");
    return ok;
}

static void CountRender(SvgRenderResult result, ref int failures, ref int inadmissible)
{
    if (!SvgRenderResult.IsAdmissible(result) || result.Bitmap == null) failures++;
    if (!SvgRenderResult.IsAdmissible(result)) inadmissible++;
}

static (int Glyphs, float Width) ProbeNativeTextStack()
{
    string? fontPath = Environment.GetEnvironmentVariable("FEN_SVG_BENCH_FONT");
    using var typeface = string.IsNullOrWhiteSpace(fontPath)
        ? SKTypeface.Default
        : SKTypeface.FromFile(fontPath);
    if (typeface == null)
    {
        throw new InvalidOperationException("SVG benchmark font could not be loaded");
    }
    using var font = new SKFont(typeface, 18f);
    using var shaper = new SKShaper(typeface);
    var result = shaper.Shape("FenBrowser", 0f, 0f, font);
    if (result?.Codepoints == null || result.Codepoints.Length == 0 ||
        !float.IsFinite(result.Width) || result.Width <= 0f)
    {
        throw new InvalidOperationException("Skia/HarfBuzz native text probe failed");
    }
    return (result.Codepoints.Length, result.Width);
}

static (long ElapsedMs, long AllocatedBytes, int Width, int Height, ulong Checksum, bool Admissible)
    TimeOne(ISvgRenderer renderer, string svg)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    long before = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    using var result = renderer.Render(svg);
    sw.Stop();
    long after = GC.GetAllocatedBytesForCurrentThread();

    ulong checksum = 0;
    int w = 0, h = 0;
    if (result.Bitmap != null)
    {
        w = result.Bitmap.Width; h = result.Bitmap.Height;
        for (int y = 0; y < h; y += 3)
        {
            for (int x = 0; x < w; x += 3)
            {
                var p = result.Bitmap.GetPixel(x, y);
                checksum = checksum * 31u ^
                    ((ulong)p.Alpha << 24 | (uint)p.Red << 16 | (uint)p.Green << 8 | p.Blue);
            }
        }
    }
    return (sw.ElapsedMilliseconds, after - before, w, h, checksum,
        SvgRenderResult.IsAdmissible(result));
}

static string MakeRedPng(int w, int h)
{
    using var bmp = new SKBitmap(w, h);
    bmp.Erase(SKColors.Red);
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
}

static string BuildPathHeavy()
{
    var sb = new System.Text.StringBuilder("<svg width='120' height='120'>");
    for (int i = 0; i < 60; i++)
    {
        int x = i % 10 * 12, y = i / 10 * 24;
        sb.Append($"<path d='M{x} {y} q6 -18 12 0 t12 0 v14 h-24 Z' fill='rgb({i * 4},{i * 3},90)'/>");
    }
    sb.Append("</svg>");
    return sb.ToString();
}

static string BuildGradientHeavy()
{
    var sb = new System.Text.StringBuilder("<svg width='120' height='120'><defs>");
    for (int i = 0; i < 12; i++)
    {
        sb.Append($"<linearGradient id='g{i}'><stop offset='0' stop-color='white'/>" +
                  $"<stop offset='1' stop-color='hsl({i * 30},80%,40%)'/></linearGradient>");
    }
    sb.Append("</defs>");
    for (int i = 0; i < 36; i++)
    {
        int x = i % 6 * 20, y = i / 6 * 20;
        sb.Append($"<rect x='{x}' y='{y}' width='20' height='20' fill='url(#g{i % 12})'/>");
    }
    sb.Append("</svg>");
    return sb.ToString();
}

static string BuildDeepNested(int depth)
{
    var sb = new System.Text.StringBuilder("<svg width='100' height='100'>");
    for (int i = 0; i < depth; i++) sb.Append("<g opacity='0.9' transform='translate(1 1)'>");
    sb.Append("<rect width='90' height='90' fill='steelblue'/>");
    for (int i = 0; i < depth; i++) sb.Append("</g>");
    sb.Append("</svg>");
    return sb.ToString();
}
