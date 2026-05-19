using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Test262;

public sealed class Test262Runner
{
    public int Run(
        string rootPath,
        bool list,
        bool dryRun,
        bool parserSubset,
        bool dashboard,
        bool verifyGates,
        string outputPath,
        int max,
        int timeoutMs,
        string engine,
        string? expectationsPath,
        string? inputPath,
        string? previousPath,
        string? test262Path,
        string? test262File,
        string? featuresCsv,
        string? supportedFeaturesCsv)
    {
        var manifest = new Test262Manifest { RootPath = rootPath };
        var files = manifest.EnumerateTestFiles().OrderBy(p => p, StringComparer.Ordinal).ToList();
        files = ApplyScopeFilter(rootPath, files, test262Path, test262File);
        files = ApplyFeatureFilter(files, featuresCsv);
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
            Test262ResultWriter.WriteDryRun(outputPath, engine, commit, files);
            Console.WriteLine($"Dry-run result written: {outputPath}");
        }

        if (parserSubset)
        {
            RunParserSubset(rootPath, outputPath, files, max, timeoutMs, engine, expectationsPath, expectations, supportedFeaturesCsv);
        }

        if (dashboard)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                Console.Error.WriteLine("Specify --in <result.json> for --dashboard mode.");
                return 4;
            }

            Test262DashboardWriter.WriteDashboard(inputPath, previousPath, outputPath);
            Console.WriteLine($"Dashboard written: {outputPath}");
        }

        if (verifyGates)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                Console.Error.WriteLine("Specify --in <result.json> for --verify-gates mode.");
                return 5;
            }

            var verify = Test262GateVerifier.Verify(inputPath, previousPath);
            Console.WriteLine($"Gate verification written: {outputPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            File.WriteAllText(outputPath, verify.ReportJson);
            if (!verify.Passed)
            {
                Console.Error.WriteLine("CI gates failed:");
                foreach (var violation in verify.Violations)
                {
                    Console.Error.WriteLine($"- {violation}");
                }

                return 6;
            }
        }

        return 0;
    }

    private static List<string> ApplyScopeFilter(string rootPath, List<string> files, string? test262Path, string? test262File)
    {
        if (!string.IsNullOrWhiteSpace(test262File))
        {
            var full = Path.GetFullPath(test262File);
            return files.Where(f => string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (string.IsNullOrWhiteSpace(test262Path))
        {
            return files;
        }

        var candidate = Path.GetFullPath(test262Path);
        if (File.Exists(candidate))
        {
            return files.Where(f => string.Equals(Path.GetFullPath(f), candidate, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (Directory.Exists(candidate))
        {
            var prefix = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return files.Where(f => Path.GetFullPath(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Treat as relative path under root/test if not found directly.
        var testRoot = Path.Combine(rootPath, "test");
        var relativeCandidate = Path.GetFullPath(Path.Combine(testRoot, test262Path));
        if (Directory.Exists(relativeCandidate))
        {
            var prefix = relativeCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return files.Where(f => Path.GetFullPath(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (File.Exists(relativeCandidate))
        {
            return files.Where(f => string.Equals(Path.GetFullPath(f), relativeCandidate, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return files;
    }

    private static List<string> ApplyFeatureFilter(List<string> files, string? featuresCsv)
    {
        if (string.IsNullOrWhiteSpace(featuresCsv))
        {
            return files;
        }

        var requested = featuresCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (requested.Count == 0)
        {
            return files;
        }

        var filtered = new List<string>(files.Count);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var frontmatter = Test262Frontmatter.Parse(source);
            if (frontmatter.Features.Any(feature => requested.Contains(feature)))
            {
                filtered.Add(file);
            }
        }

        return filtered;
    }

    private static void RunParserSubset(string rootPath, string outputPath, IReadOnlyList<string> files, int max, int timeoutMs, string engine, string? expectationsPath, Test262Expectations? expectations, string? supportedFeaturesCsv)
    {
        var startedAtUtc = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var pinPath = Path.Combine(rootPath, "..", "test262.pin");
        var commit = File.Exists(pinPath) ? File.ReadAllText(pinPath).Trim() : "un-pinned";
        var subset = files.Take(Math.Max(1, max)).ToList();
        var supportedFeatures = ParseSupportedFeatures(supportedFeaturesCsv);

        var passed = 0;
        var unsupported = 0;
        var parserErrors = 0;
        var crashes = 0;
        var timedOut = 0;
        var harnessUnsupported = 0;
        var expectedFailures = 0;
        var unexpectedPasses = 0;
        var failures = new List<object>();
        var unexpectedPassesList = new List<object>();
        var tests = new List<object>(subset.Count);
        var supportedHarnessIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "propertyHelper.js",
            "sta.js",
            "compareArray.js"
        };

        foreach (var file in subset)
        {
            var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            var sourceText = File.ReadAllText(file);
            var frontmatter = Test262Frontmatter.Parse(sourceText);
            var expectsSyntaxError = ExpectsSyntaxErrorParseFailure(frontmatter);
            var parserInput = PrepareParserInput(sourceText, frontmatter);
            var parseAsModule = frontmatter.Flags.Any(f => string.Equals(f, "module", StringComparison.OrdinalIgnoreCase));

            var unsupportedHarnessInclude = frontmatter.Includes.FirstOrDefault(include => !supportedHarnessIncludes.Contains(include));
            if (unsupportedHarnessInclude is not null)
            {
                harnessUnsupported++;
                failures.Add(new
                {
                    path = file,
                    relativePath,
                    classification = "harness-unsupported",
                    include = unsupportedHarnessInclude,
                    message = $"Harness include '{unsupportedHarnessInclude}' is not supported in parser-subset mode."
                });

                tests.Add(new
                {
                    path = relativePath,
                    status = "HarnessUnsupported",
                    durationMs = 0,
                    features = frontmatter.Features,
                    flags = frontmatter.Flags,
                    includes = frontmatter.Includes,
                    negative = frontmatter.Negative,
                    esid = frontmatter.Esid,
                    description = frontmatter.Description,
                    info = frontmatter.Info,
                    locale = frontmatter.Locale,
                    category = "host-not-applicable",
                    message = $"Harness include '{unsupportedHarnessInclude}' is not supported in parser-subset mode."
                });
                continue;
            }

            var unsupportedFeature = frontmatter.Features.FirstOrDefault(feature => supportedFeatures is not null && !supportedFeatures.Contains(feature));
            if (unsupportedFeature is not null)
            {
                unsupported++;
                failures.Add(new
                {
                    path = file,
                    relativePath,
                    classification = "unsupported",
                    feature = unsupportedFeature,
                    message = $"Feature '{unsupportedFeature}' is not in supported feature set."
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
                    message = $"Feature '{unsupportedFeature}' is not in supported feature set."
                });
                continue;
            }

            try
            {
                var source = new SourceText(parserInput, file);
                var parseTask = Task.Run(() =>
                {
                    if (parseAsModule)
                    {
                        JsParser.ParseModule(source);
                    }
                    else
                    {
                        JsParser.ParseScript(source);
                    }
                });
                var timeoutTask = Task.Delay(Math.Max(1, timeoutMs));
                var completedTask = Task.WhenAny(parseTask, timeoutTask).GetAwaiter().GetResult();
                if (!ReferenceEquals(completedTask, parseTask))
                {
                    timedOut++;
                    failures.Add(new
                    {
                        path = file,
                        relativePath,
                        classification = "timeout",
                        message = $"Parsing exceeded timeout of {timeoutMs} ms."
                    });

                    tests.Add(new
                    {
                        path = relativePath,
                        status = "TimedOut",
                        durationMs = timeoutMs,
                        features = frontmatter.Features,
                        flags = frontmatter.Flags,
                        includes = frontmatter.Includes,
                        negative = frontmatter.Negative,
                        esid = frontmatter.Esid,
                        description = frontmatter.Description,
                        info = frontmatter.Info,
                        locale = frontmatter.Locale,
                        category = "timeout",
                        message = $"Parsing exceeded timeout of {timeoutMs} ms."
                    });
                    continue;
                }

                // Propagate parser exceptions with original types (not AggregateException).
                parseTask.GetAwaiter().GetResult();

                if (expectsSyntaxError)
                {
                    parserErrors++;
                    failures.Add(new
                    {
                        path = file,
                        relativePath,
                        classification = "parser-error",
                        message = "Expected parser to fail with SyntaxError due to test262 negative metadata, but parse succeeded.",
                        expected = false
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
                        message = "Expected parser to fail with SyntaxError due to test262 negative metadata, but parse succeeded."
                    });
                    continue;
                }

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

                        tests.Add(new
                        {
                            path = relativePath,
                            status = "UnexpectedPass",
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
                            message = $"Unexpected pass for expectation '{expected.Status}'."
                        });
                        continue;
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
                    status = expected is null ? "UnsupportedFeature" : "ExpectedFailure",
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
                if (expectsSyntaxError)
                {
                    passed++;
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
                    continue;
                }

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
                    status = expected is null ? "Failed" : "ExpectedFailure",
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
                    status = expected is null ? "Crashed" : "ExpectedFailure",
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

        stopwatch.Stop();
        Test262ResultWriter.WriteParserSubset(
            outputPath,
            commit,
            engine,
            startedAtUtc,
            stopwatch.ElapsedMilliseconds,
            subset.Count,
            passed,
            unsupported,
            parserErrors,
            crashes,
            timedOut,
            harnessUnsupported,
            expectedFailures,
            unexpectedPasses,
            failures,
            unexpectedPassesList,
            tests,
            expectationsPath);
        Console.WriteLine($"Parser subset result written: {outputPath}");
    }

    private static bool ExpectsSyntaxErrorParseFailure(Test262FrontmatterMetadata frontmatter)
    {
        if (frontmatter.Negative is null || string.IsNullOrWhiteSpace(frontmatter.Negative.Type))
        {
            return false;
        }

        if (!string.Equals(frontmatter.Negative.Type, "SyntaxError", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(frontmatter.Negative.Phase))
        {
            return true;
        }

        return string.Equals(frontmatter.Negative.Phase, "parse", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(frontmatter.Negative.Phase, "early", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string>? ParseSupportedFeatures(string? supportedFeaturesCsv)
    {
        if (string.IsNullOrWhiteSpace(supportedFeaturesCsv))
        {
            return null;
        }

        var set = supportedFeaturesCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return set.Count == 0 ? null : set;
    }

    private static string PrepareParserInput(string sourceText, Test262FrontmatterMetadata frontmatter)
    {
        var onlyStrict = frontmatter.Flags.Any(f => string.Equals(f, "onlyStrict", StringComparison.OrdinalIgnoreCase));
        if (!onlyStrict)
        {
            return sourceText;
        }

        var trimmed = sourceText.TrimStart();
        if (trimmed.StartsWith("\"use strict\"", StringComparison.Ordinal) || trimmed.StartsWith("'use strict'", StringComparison.Ordinal))
        {
            return sourceText;
        }

        return "\"use strict\";\n" + sourceText;
    }
}
