using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Test262;

public sealed class Test262Runner
{
    public int Run(string rootPath, bool list, bool dryRun, bool parserSubset, string outputPath, int max, string? expectationsPath)
    {
        var manifest = new Test262Manifest { RootPath = rootPath };
        var files = manifest.EnumerateTestFiles().OrderBy(p => p, StringComparer.Ordinal).ToList();
        Test262Expectations? expectations = null;
        if (!string.IsNullOrWhiteSpace(expectationsPath))
        {
            expectations = Test262Expectations.Load(expectationsPath);
        }

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
            RunParserSubset(rootPath, outputPath, files, max, expectationsPath, expectations);
        }

        return 0;
    }

    private static void RunParserSubset(string rootPath, string outputPath, IReadOnlyList<string> files, int max, string? expectationsPath, Test262Expectations? expectations)
    {
        var pinPath = Path.Combine(rootPath, "..", "test262.pin");
        var commit = File.Exists(pinPath) ? File.ReadAllText(pinPath).Trim() : "un-pinned";
        var subset = files.Take(Math.Max(1, max)).ToList();

        var passed = 0;
        var unsupported = 0;
        var parserErrors = 0;
        var crashes = 0;
        var expectedFailures = 0;
        var unexpectedPasses = 0;
        var failures = new List<object>();
        var unexpectedPassesList = new List<object>();
        var tests = new List<object>(subset.Count);

        foreach (var file in subset)
        {
            var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            var sourceText = File.ReadAllText(file);
            var frontmatter = Test262Frontmatter.Parse(sourceText);
            try
            {
                var source = new SourceText(sourceText, file);
                JsParser.ParseScript(source);
                passed++;

                if (expectations is not null)
                {
                    var expected = expectations.Entries.FirstOrDefault(e => e.Matches(relativePath, "ParserError") || e.Matches(relativePath, "UnsupportedFeature") || e.Matches(relativePath, "Crash"));
                    if (expected is not null)
                    {
                        unexpectedPasses++;
                        unexpectedPassesList.Add(new
                        {
                            path = file,
                            relativePath,
                            expectedStatus = expected.Status,
                            expectedReason = expected.Reason,
                            expectedOwner = expected.Owner,
                            expectedArea = expected.Area,
                            expiresAtMilestone = expected.ExpiresAtMilestone
                        });
                    }
                }

                tests.Add(new
                {
                    path = relativePath,
                    status = "Passed",
                    durationMs = 0,
                    features = frontmatter.Features,
                    flags = frontmatter.Flags,
                    includes = frontmatter.Includes,
                    negative = frontmatter.Negative,
                    esid = frontmatter.Esid,
                    description = frontmatter.Description,
                    info = frontmatter.Info,
                    locale = frontmatter.Locale,
                    category = (string?)null,
                    message = (string?)null
                });
            }
            catch (UnsupportedFeatureException ex)
            {
                unsupported++;
                var expected = expectations?.Entries.FirstOrDefault(e => e.Matches(relativePath, "UnsupportedFeature"));
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new
                {
                    path = file,
                    relativePath,
                    classification = "unsupported",
                    feature = ex.FeatureName,
                    location = $"{ex.Span.Line}:{ex.Span.Column}",
                    expected = expected is not null,
                    expectedReason = expected?.Reason,
                    expectedOwner = expected?.Owner,
                    expectedArea = expected?.Area,
                    expiresAtMilestone = expected?.ExpiresAtMilestone
                });

                tests.Add(new
                {
                    path = relativePath,
                    status = "UnsupportedFeature",
                    durationMs = 0,
                    features = frontmatter.Features,
                    flags = frontmatter.Flags,
                    includes = frontmatter.Includes,
                    negative = frontmatter.Negative,
                    esid = frontmatter.Esid,
                    description = frontmatter.Description,
                    info = frontmatter.Info,
                    locale = frontmatter.Locale,
                    category = "parser-missing",
                    message = ex.Message
                });
            }
            catch (JsParserException ex)
            {
                parserErrors++;
                var expected = expectations?.Entries.FirstOrDefault(e => e.Matches(relativePath, "ParserError"));
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new
                {
                    path = file,
                    relativePath,
                    classification = "parser-error",
                    message = ex.Message,
                    expected = expected is not null,
                    expectedReason = expected?.Reason,
                    expectedOwner = expected?.Owner,
                    expectedArea = expected?.Area,
                    expiresAtMilestone = expected?.ExpiresAtMilestone
                });

                tests.Add(new
                {
                    path = relativePath,
                    status = "Failed",
                    durationMs = 0,
                    features = frontmatter.Features,
                    flags = frontmatter.Flags,
                    includes = frontmatter.Includes,
                    negative = frontmatter.Negative,
                    esid = frontmatter.Esid,
                    description = frontmatter.Description,
                    info = frontmatter.Info,
                    locale = frontmatter.Locale,
                    category = "parser-bug",
                    message = ex.Message
                });
            }
            catch (Exception ex)
            {
                crashes++;
                var expected = expectations?.Entries.FirstOrDefault(e => e.Matches(relativePath, "Crash"));
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new
                {
                    path = file,
                    relativePath,
                    classification = "crash",
                    message = ex.Message,
                    expected = expected is not null,
                    expectedReason = expected?.Reason,
                    expectedOwner = expected?.Owner,
                    expectedArea = expected?.Area,
                    expiresAtMilestone = expected?.ExpiresAtMilestone
                });

                tests.Add(new
                {
                    path = relativePath,
                    status = "Crashed",
                    durationMs = 0,
                    features = frontmatter.Features,
                    flags = frontmatter.Flags,
                    includes = frontmatter.Includes,
                    negative = frontmatter.Negative,
                    esid = frontmatter.Esid,
                    description = frontmatter.Description,
                    info = frontmatter.Info,
                    locale = frontmatter.Locale,
                    category = "crash",
                    message = ex.Message
                });
            }
        }

        Test262ResultWriter.WriteParserSubset(outputPath, commit, subset.Count, passed, unsupported, parserErrors, crashes, expectedFailures, unexpectedPasses, failures, unexpectedPassesList, tests, expectationsPath);
        Console.WriteLine($"Parser subset result written: {outputPath}");
    }
}
