using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Test262;

public sealed class Test262Runner
{
    public int Run(string rootPath, bool list, bool dryRun, bool parserSubset, string outputPath, int max)
    {
        var manifest = new Test262Manifest { RootPath = rootPath };
        var files = manifest.EnumerateTestFiles().OrderBy(p => p, StringComparer.Ordinal).ToList();

        if (list)
        {
            foreach (var file in files.Take(200))
            {
                Console.WriteLine(file);
            }

            Console.WriteLine($"Total: {files.Count}");
        }

        if (dryRun)
        {
            var pinPath = Path.Combine(rootPath, "..", "test262.pin");
            var commit = File.Exists(pinPath) ? File.ReadAllText(pinPath).Trim() : "un-pinned";
            Test262ResultWriter.WriteDryRun(outputPath, commit, files);
            Console.WriteLine($"Dry-run result written: {outputPath}");
        }

        if (parserSubset)
        {
            RunParserSubset(rootPath, outputPath, files, max);
        }

        return 0;
    }

    private static void RunParserSubset(string rootPath, string outputPath, IReadOnlyList<string> files, int max)
    {
        var pinPath = Path.Combine(rootPath, "..", "test262.pin");
        var commit = File.Exists(pinPath) ? File.ReadAllText(pinPath).Trim() : "un-pinned";
        var subset = files.Take(Math.Max(1, max)).ToList();

        var passed = 0;
        var unsupported = 0;
        var parserErrors = 0;
        var crashes = 0;
        var failures = new List<object>();

        foreach (var file in subset)
        {
            try
            {
                var source = new SourceText(File.ReadAllText(file), file);
                JsParser.ParseScript(source);
                passed++;
            }
            catch (UnsupportedFeatureException ex)
            {
                unsupported++;
                failures.Add(new
                {
                    path = file,
                    classification = "unsupported",
                    feature = ex.FeatureName,
                    location = $"{ex.Span.Line}:{ex.Span.Column}"
                });
            }
            catch (JsParserException ex)
            {
                parserErrors++;
                failures.Add(new
                {
                    path = file,
                    classification = "parser-error",
                    message = ex.Message
                });
            }
            catch (Exception ex)
            {
                crashes++;
                failures.Add(new
                {
                    path = file,
                    classification = "crash",
                    message = ex.Message
                });
            }
        }

        Test262ResultWriter.WriteParserSubset(outputPath, commit, subset.Count, passed, unsupported, parserErrors, crashes, failures);
        Console.WriteLine($"Parser subset result written: {outputPath}");
    }
}
