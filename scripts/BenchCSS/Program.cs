using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;

internal static partial class Program
{
    private const int DefaultIterations = 5;
    private const int DefaultWarmupIterations = 1;

    public static int Main(string[] args)
    {
        try
        {
            var options = ParseOptions(args);
            var inputPath = Path.GetFullPath(options.InputPath);
            var html = File.ReadAllText(inputPath);
            var styleBlocks = StyleElementRegex()
                .Matches(html)
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Where(css => css.Length >= options.MinimumCssBytes)
                .ToArray();

            if (styleBlocks.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No <style> blocks met the {options.MinimumCssBytes}-byte minimum in '{inputPath}'.");
            }

            var css = string.Join(Environment.NewLine, styleBlocks);
            var fontFaces = ExtractFontFaceBodies(css);

            for (var i = 0; i < options.WarmupIterations; i++)
            {
                _ = Tokenize(css);
                _ = ParseStylesheet(css);
                ParseFontFaces(fontFaces, options.BaseUri);
            }

            var tokenizerSamples = Measure(options.Iterations, () => Tokenize(css), out var tokenCount);
            var parserSamples = Measure(options.Iterations, () => ParseStylesheet(css), out var ruleCount);
            var fontFaceSamples = fontFaces.Count == 0
                ? Array.Empty<double>()
                : Measure(options.Iterations, () => ParseFontFaces(fontFaces, options.BaseUri), out _);

            var report = new
            {
                schema_version = 1,
                input = new
                {
                    path = inputPath,
                    html_bytes = Encoding.UTF8.GetByteCount(html),
                    style_blocks = styleBlocks.Length,
                    css_bytes = Encoding.UTF8.GetByteCount(css),
                    font_faces = fontFaces.Count
                },
                iterations = options.Iterations,
                warmup_iterations = options.WarmupIterations,
                token_count = tokenCount,
                parsed_rule_count = ruleCount,
                tokenizer = Summarize(tokenizerSamples, css),
                parser = Summarize(parserSamples, css),
                font_face_parser = fontFaceSamples.Length == 0
                    ? null
                    : Summarize(fontFaceSamples, string.Join(Environment.NewLine, fontFaces))
            };

            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(
                "Usage: BenchCSS <input-html> [--iterations N] [--warmup N] " +
                "[--min-css-bytes N] [--base-url URL]");
            return 2;
        }
    }

    private static Options ParseOptions(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("An input HTML path is required.");
        }

        var inputPath = args[0];
        var iterations = DefaultIterations;
        var warmupIterations = DefaultWarmupIterations;
        var minimumCssBytes = 0;
        var baseUri = new Uri("https://benchmark.invalid/");

        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{args[index]}'.");
            }

            var name = args[index];
            var value = args[index + 1];
            switch (name)
            {
                case "--iterations":
                    iterations = ParseNonNegativeInteger(name, value, allowZero: false);
                    break;
                case "--warmup":
                    warmupIterations = ParseNonNegativeInteger(name, value, allowZero: true);
                    break;
                case "--min-css-bytes":
                    minimumCssBytes = ParseNonNegativeInteger(name, value, allowZero: true);
                    break;
                case "--base-url":
                    if (!Uri.TryCreate(value, UriKind.Absolute, out baseUri))
                    {
                        throw new ArgumentException($"'{value}' is not an absolute URL.");
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        return new Options(inputPath, iterations, warmupIterations, minimumCssBytes, baseUri);
    }

    private static int ParseNonNegativeInteger(string name, string value, bool allowZero)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 0 || (!allowZero && parsed == 0))
        {
            throw new ArgumentException($"'{name}' requires {(allowZero ? "a non-negative" : "a positive")} integer.");
        }

        return parsed;
    }

    private static int Tokenize(string css)
    {
        var tokenizer = new CssTokenizer(css);
        var count = 0;
        while (tokenizer.Consume().Type != CssTokenType.EOF)
        {
            count++;
        }

        return count;
    }

    private static int ParseStylesheet(string css)
    {
        var parser = new CssSyntaxParser(new CssTokenizer(css));
        return parser.ParseStylesheet().Rules.Count;
    }

    private static int ParseFontFaces(IReadOnlyList<string> fontFaces, Uri baseUri)
    {
        foreach (var fontFace in fontFaces)
        {
            FontRegistry.ParseAndRegister(fontFace, baseUri);
        }

        return fontFaces.Count;
    }

    private static double[] Measure(int iterations, Func<int> action, out int observedCount)
    {
        var samples = new double[iterations];
        observedCount = -1;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var stopwatch = Stopwatch.StartNew();
            var count = action();
            stopwatch.Stop();

            if (observedCount >= 0 && count != observedCount)
            {
                throw new InvalidOperationException(
                    $"Benchmark output changed between iterations: expected {observedCount}, observed {count}.");
            }

            observedCount = count;
            samples[iteration] = stopwatch.Elapsed.TotalMilliseconds;
        }

        return samples;
    }

    private static object Summarize(double[] samples, string input)
    {
        var ordered = samples.Order().ToArray();
        var median = ordered.Length % 2 == 0
            ? (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2d
            : ordered[ordered.Length / 2];
        var inputMiB = Encoding.UTF8.GetByteCount(input) / (1024d * 1024d);

        return new
        {
            median_ms = Math.Round(median, 4),
            min_ms = Math.Round(ordered[0], 4),
            max_ms = Math.Round(ordered[^1], 4),
            throughput_mib_per_second = Math.Round(inputMiB / (median / 1000d), 3)
        };
    }

    private static List<string> ExtractFontFaceBodies(string css)
    {
        var bodies = new List<string>();
        var searchIndex = 0;
        while (searchIndex < css.Length)
        {
            var fontFaceIndex = css.IndexOf("@font-face", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (fontFaceIndex < 0)
            {
                break;
            }

            var openBrace = css.IndexOf('{', fontFaceIndex + "@font-face".Length);
            if (openBrace < 0)
            {
                break;
            }

            var depth = 1;
            var closeBrace = -1;
            for (var index = openBrace + 1; index < css.Length; index++)
            {
                if (css[index] == '{') depth++;
                if (css[index] != '}' || --depth != 0) continue;
                closeBrace = index;
                break;
            }

            if (closeBrace < 0)
            {
                break;
            }

            bodies.Add(css[(openBrace + 1)..closeBrace]);
            searchIndex = closeBrace + 1;
        }

        return bodies;
    }

    [GeneratedRegex(@"<style\b[^>]*>(.*?)</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleElementRegex();

    private sealed record Options(
        string InputPath,
        int Iterations,
        int WarmupIterations,
        int MinimumCssBytes,
        Uri BaseUri);
}
