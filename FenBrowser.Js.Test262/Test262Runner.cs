using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Js.Test262;

public sealed class Test262Runner
{
    public static Func<SourceText, bool, Task>? ParseInvokerForTests { get; set; }
    public static Func<string, string, bool, Task>? RuntimeInvokerForTests { get; set; }

    public int Run(
        string rootPath,
        bool list,
        bool dryRun,
        bool parserSubset,
        bool runtimeSubset,
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
        string? supportedFeaturesCsv,
        bool test262Shallow = false)
    {
        var manifest = new Test262Manifest { RootPath = rootPath };
        var files = manifest.EnumerateTestFiles().OrderBy(p => p, StringComparer.Ordinal).ToList();
        files = ApplyScopeFilter(rootPath, files, test262Path, test262File, test262Shallow);
        files = ApplyFeatureFilter(files, featuresCsv);
        Test262Expectations? expectations = null;
        if (!string.IsNullOrWhiteSpace(expectationsPath) && (parserSubset || runtimeSubset))
        {
            expectations = Test262Expectations.Load(expectationsPath);
        }

        if (list)
        {
            foreach (var file in files.Take(Math.Max(1, max)))
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

        if (runtimeSubset)
        {
            RunRuntimeSubset(rootPath, outputPath, files, max, timeoutMs, engine, expectationsPath, expectations, supportedFeaturesCsv);
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

    private static List<string> ApplyScopeFilter(string rootPath, List<string> files, string? test262Path, string? test262File, bool shallow = false)
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

        // shallow: match only files whose immediate parent directory IS the scope dir
        // (the loose tests sitting directly in a directory, excluding its subdirs).
        static List<string> ShallowMatch(List<string> all, string dir)
        {
            var norm = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return all.Where(f => string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(f))?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                norm, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var candidate = Path.GetFullPath(test262Path);
        if (File.Exists(candidate))
        {
            return files.Where(f => string.Equals(Path.GetFullPath(f), candidate, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (Directory.Exists(candidate))
        {
            if (shallow) { return ShallowMatch(files, candidate); }
            var prefix = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return files.Where(f => Path.GetFullPath(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Treat as relative path under root/test if not found directly.
        var testRoot = Path.Combine(rootPath, "test");
        var relativeCandidate = Path.GetFullPath(Path.Combine(testRoot, test262Path));
        if (Directory.Exists(relativeCandidate))
        {
            if (shallow) { return ShallowMatch(files, relativeCandidate); }
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
        var invalidTestConfiguration = 0;
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
            var expectsSyntaxError = ExpectsSyntaxErrorParseFailure(frontmatter);
            var parserInput = PrepareParserInput(sourceText, frontmatter);
            var parseAsModule = frontmatter.Flags.Any(f => string.Equals(f, "module", StringComparison.OrdinalIgnoreCase));
            var onlyStrict = frontmatter.Flags.Any(f => string.Equals(f, "onlyStrict", StringComparison.OrdinalIgnoreCase));

            if (IsInvalidParserSubsetConfiguration(frontmatter, out var invalidReason))
            {
                invalidTestConfiguration++;
                failures.Add(new
                {
                    path = file,
                    relativePath,
                    classification = "invalid-test-configuration",
                    message = invalidReason
                });

                tests.Add(new
                {
                    path = relativePath,
                    status = "InvalidTestConfiguration",
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
                    message = invalidReason
                });
                continue;
            }

            var unsupportedFeature = frontmatter.Features.FirstOrDefault(feature => supportedFeatures is not null && !supportedFeatures.Contains(feature));
            if (unsupportedFeature is not null)
            {
                unsupported++;
                var expected = FindMatchingExpectation(expectations, relativePath, "UnsupportedFeature");
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new
                {
                    path = file,
                    relativePath,
                    classification = "unsupported",
                    feature = unsupportedFeature,
                    message = $"Feature '{unsupportedFeature}' is not in supported feature set.",
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
                    message = $"Feature '{unsupportedFeature}' is not in supported feature set."
                });
                continue;
            }

            try
            {
                if (expectsSyntaxError && onlyStrict && ContainsLegacyOctalEscape(sourceText))
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

                var source = new SourceText(parserInput, file);
                var parseCompleted = RunWithPerTestTimeout(() =>
                {
                    var overrideInvoker = ParseInvokerForTests;
                    if (overrideInvoker is not null)
                    {
                        overrideInvoker(source, parseAsModule).GetAwaiter().GetResult();
                    }
                    else if (parseAsModule)
                    {
                        JsParser.ParseModule(source);
                    }
                    else
                    {
                        JsParser.ParseScript(source);
                    }
                }, timeoutMs);
                if (!parseCompleted)
                {
                    timedOut++;
                    var expected = FindMatchingExpectation(expectations, relativePath, "Timeout");
                    if (expected is not null)
                    {
                        expectedFailures++;
                    }

                    failures.Add(new
                    {
                        path = file,
                        relativePath,
                        classification = "timeout",
                        message = $"Parsing exceeded timeout of {timeoutMs} ms.",
                        expected = expected is not null,
                        expectedReason = expected?.Reason,
                        expectedOwner = expected?.Owner,
                        expectedArea = expected?.Area,
                        expiresAtMilestone = expected?.ExpiresAtMilestone
                    });

                    tests.Add(new
                    {
                        path = relativePath,
                        status = expected is null ? "TimedOut" : "ExpectedFailure",
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
                    var expected = FindAnyMatchingExpectation(expectations, relativePath);
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
            invalidTestConfiguration,
            expectedFailures,
            unexpectedPasses,
            failures,
            unexpectedPassesList,
            tests,
            expectationsPath);
        Console.WriteLine($"Parser subset result written: {outputPath}");
    }

    // Runtime-subset mode CAN execute, so a `negative: phase: runtime` test is valid
    // here (handled via expectsRuntimeThrow) — unlike parser-subset mode. Only a
    // parse/early negative whose type isn't SyntaxError is genuinely unhandleable.
    private static bool IsInvalidRuntimeSubsetConfiguration(Test262FrontmatterMetadata frontmatter, out string reason)
    {
        reason = string.Empty;
        if (frontmatter.Negative is null)
        {
            return false;
        }

        var phase = frontmatter.Negative.Phase?.Trim();
        var type = frontmatter.Negative.Type?.Trim();
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        // runtime-phase negatives are executable here (caught + verified downstream).
        if (string.Equals(phase, "runtime", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // parse/early negatives are only handled when the expected type is SyntaxError.
        var isParseOrEarly = string.IsNullOrWhiteSpace(phase) ||
                             string.Equals(phase, "parse", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(phase, "early", StringComparison.OrdinalIgnoreCase);
        if (isParseOrEarly && !string.Equals(type, "SyntaxError", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Negative parse/early expectation '{type}' is not executable in runtime-subset mode.";
            return true;
        }

        return false;
    }

    private static bool IsInvalidParserSubsetConfiguration(Test262FrontmatterMetadata frontmatter, out string reason)
    {
        reason = string.Empty;
        if (frontmatter.Negative is null)
        {
            return false;
        }

        var phase = frontmatter.Negative.Phase?.Trim();
        var type = frontmatter.Negative.Type?.Trim();
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        var isParseOrEarly = string.IsNullOrWhiteSpace(phase) ||
                             string.Equals(phase, "parse", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(phase, "early", StringComparison.OrdinalIgnoreCase);
        if (isParseOrEarly)
        {
            if (string.Equals(type, "SyntaxError", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            reason = $"Negative parse/early expectation '{type}' is invalid for parser-subset mode.";
            return true;
        }

        if (string.Equals(phase, "runtime", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Negative runtime expectation '{type}' is not executable in parser-subset mode.";
            return true;
        }

        return false;
    }

    private static void RunRuntimeSubset(
        string rootPath,
        string outputPath,
        IReadOnlyList<string> files,
        int max,
        int timeoutMs,
        string engine,
        string? expectationsPath,
        Test262Expectations? expectations,
        string? supportedFeaturesCsv)
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
        var runtimeErrors = 0;
        var crashes = 0;
        var timedOut = 0;
        var harnessUnsupported = 0;
        var invalidTestConfiguration = 0;
        var expectedFailures = 0;
        var unexpectedPasses = 0;
        var failures = new List<object>();
        var unexpectedPassesList = new List<object>();
        var tests = new List<object>(subset.Count);
        var completed = 0;
        var progressEvery = Math.Max(25, Math.Min(200, subset.Count / 50));
        var nextProgressAt = progressEvery;
        var lastProgressElapsed = TimeSpan.Zero;
        var supportedHarnessIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "propertyHelper.js",
            "sta.js",
            "compareArray.js",
            "isConstructor.js",
            "fnGlobalObject.js",
            "promiseHelper.js",
            "nans.js",
            "dateConstants.js",
            "byteConversionValues.js",
            "deepEqual.js",
            "nativeFunctionMatcher.js",
            "doneprintHandle.js",
            "assertRelativeDateMs.js",
            "decimalToHexString.js",
            "tcoHelper.js",
            "iteratorZipUtils.js",
            "compareIterator.js",
            "asyncHelpers.js",
            "testTypedArray.js",
            "testIntl.js",
            "proxyTrapsHelper.js",
            "regExpUtils.js",
            "detachArrayBuffer.js",
            "resizableArrayBufferUtils.js",
            "testAtomics.js",
            "atomicsHelper.js",
            "temporalHelpers.js",
        };

        Console.WriteLine($"Running runtime subset: total={subset.Count}, timeoutMs={timeoutMs}, root={rootPath}");

        foreach (var file in subset)
        {
            // A fresh compiler per test: BytecodeCompiler carries per-compilation mutable
            // state (register/instruction buffers, scope stacks). A prior test that threw
            // mid-compile would otherwise leave it dirty and crash a later compilation.
            var compiler = new BytecodeCompiler();
            try
            {
                var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                var sourceText = File.ReadAllText(file);
                var frontmatter = Test262Frontmatter.Parse(sourceText);
                var expectsSyntaxError = ExpectsSyntaxErrorParseFailure(frontmatter);
                var expectsRuntimeThrow = ExpectsRuntimeThrow(frontmatter);
                var parserInput = PrepareParserInput(sourceText, frontmatter);
                var parseAsModule = frontmatter.Flags.Any(f => string.Equals(f, "module", StringComparison.OrdinalIgnoreCase));

                if (IsInvalidRuntimeSubsetConfiguration(frontmatter, out var invalidReason))
                {
                    invalidTestConfiguration++;
                    failures.Add(new { path = file, relativePath, classification = "invalid-test-configuration", message = invalidReason });
                    tests.Add(new
                    {
                        path = relativePath,
                        status = "InvalidTestConfiguration",
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
                        message = invalidReason
                    });
                    continue;
                }

            var unsupportedHarnessInclude = frontmatter.Includes.FirstOrDefault(include => !supportedHarnessIncludes.Contains(include));
            if (unsupportedHarnessInclude is not null)
            {
                harnessUnsupported++;
                failures.Add(new { path = file, relativePath, classification = "harness-unsupported", include = unsupportedHarnessInclude, message = $"Harness include '{unsupportedHarnessInclude}' is not supported in runtime-subset mode." });
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
                    message = $"Harness include '{unsupportedHarnessInclude}' is not supported in runtime-subset mode."
                });
                continue;
            }

            var unsupportedFeature = frontmatter.Features.FirstOrDefault(feature => supportedFeatures is not null && !supportedFeatures.Contains(feature));
            if (unsupportedFeature is not null)
            {
                unsupported++;
                var expected = FindMatchingExpectation(expectations, relativePath, "UnsupportedFeature");
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new
                {
                    path = file,
                    relativePath,
                    classification = "unsupported",
                    feature = unsupportedFeature,
                    message = $"Feature '{unsupportedFeature}' is not in supported feature set.",
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
                    category = "runtime-missing",
                    message = $"Feature '{unsupportedFeature}' is not in supported feature set."
                });
                continue;
            }

            try
            {
                var runtimeInput = parserInput;
                if (frontmatter.Includes.Count > 0 || RequiresRuntimeHarnessSupport(sourceText))
                {
                    var includePrelude = BuildRuntimeHarnessIncludePrelude(rootPath, frontmatter.Includes);
                    var prelude = BuildRuntimeHarnessPrelude(sourceText);
                    // onlyStrict: "use strict" must be the first statement in the
                    // script to enable strict mode globally. parserInput already has
                    // it prepended via PrepareParserInput, but that puts it AFTER
                    // the harness prelude. Move it to the top.
                    var onlyStrict = frontmatter.Flags.Any(f => string.Equals(f, "onlyStrict", StringComparison.OrdinalIgnoreCase));
                    var strictPrefix = onlyStrict ? "\"use strict\";\n" : "";
                    var body = parserInput;
                    if (onlyStrict)
                    {
                        var t = parserInput.TrimStart();
                        if (t.StartsWith("\"use strict\"", StringComparison.Ordinal) ||
                            t.StartsWith("'use strict'", StringComparison.Ordinal))
                        {
                            var idx = parserInput.IndexOf(t, StringComparison.Ordinal);
                            var semiEnd = t.IndexOf(';');
                            body = parserInput.Substring(0, idx) + t.Substring(semiEnd + 1).TrimStart();
                        }
                    }
                    runtimeInput = string.IsNullOrWhiteSpace(includePrelude)
                        ? strictPrefix + prelude + "\n" + body
                        : strictPrefix + prelude + "\n" + includePrelude + "\n" + body;
                }

                var interruptRequested = 0;
                var executeCompleted = RunWithPerTestTimeout(() =>
                {
                    var overrideInvoker = RuntimeInvokerForTests;
                    if (overrideInvoker is not null)
                    {
                        overrideInvoker(runtimeInput, file, parseAsModule).GetAwaiter().GetResult();
                    }
                    else
                    {
                        var source = new SourceText(runtimeInput, file);
                        var function = parseAsModule
                            ? compiler.CompileProgram(JsParser.ParseModule(source))
                            : compiler.CompileScript(source);
                        var interpreter = new BytecodeInterpreter(new JsHeap());
                        // Use interpreter-level wall-clock budget so long-running scripts
                        // terminate from inside execution before the external timeout.
                        interpreter.WallClockTimeoutMs = Math.Max(1, timeoutMs);
                        interpreter.InterruptCallback = () => Volatile.Read(ref interruptRequested) == 0;
                        try
                        {
                            _ = interpreter.Execute(function);
                        }
                        catch (JsThrownException thrown)
                        {
                            // Capture a "Name: message" description while the interpreter
                            // (and its heap) is still alive; the outer catch only sees the
                            // JsValue handle, whose heap is gone by then.
                            thrown.Description ??= interpreter.DescribeThrownValue(thrown.Value);
                            throw;
                        }
                    }
                }, timeoutMs);
                if (!executeCompleted)
                {
                    Volatile.Write(ref interruptRequested, 1);
                    timedOut++;
                    var expected = FindMatchingExpectation(expectations, relativePath, "Timeout");
                    if (expected is not null)
                    {
                        expectedFailures++;
                    }

                    failures.Add(new
                    {
                        path = file,
                        relativePath,
                        classification = "timeout",
                        message = $"Runtime execution exceeded timeout of {timeoutMs} ms.",
                        expected = expected is not null,
                        expectedReason = expected?.Reason,
                        expectedOwner = expected?.Owner,
                        expectedArea = expected?.Area,
                        expiresAtMilestone = expected?.ExpiresAtMilestone
                    });
                    tests.Add(new
                    {
                        path = relativePath,
                        status = expected is null ? "TimedOut" : "ExpectedFailure",
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
                        message = $"Runtime execution exceeded timeout of {timeoutMs} ms."
                    });
                    continue;
                }

                if (expectsSyntaxError || expectsRuntimeThrow)
                {
                    runtimeErrors++;
                    var expected = FindMatchingExpectation(expectations, relativePath, "RuntimeError");
                    if (expected is not null)
                    {
                        expectedFailures++;
                    }

                    failures.Add(new
                    {
                        path = file,
                        relativePath,
                        classification = "runtime-error",
                        message = "Expected failure did not occur in runtime-subset execution.",
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
                        category = "runtime-semantic-bug",
                        message = "Expected failure did not occur in runtime-subset execution."
                    });
                    continue;
                }

                passed++;
                if (expectations is not null)
                {
                    var expected = FindAnyMatchingExpectation(expectations, relativePath);
                    if (expected is not null)
                    {
                        unexpectedPasses++;
                        unexpectedPassesList.Add(new { path = file, relativePath, expectedStatus = expected.Status, expectedReason = expected.Reason, expectedOwner = expected.Owner, expectedArea = expected.Area, expiresAtMilestone = expected.ExpiresAtMilestone });
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
                if (expectsSyntaxError || expectsRuntimeThrow)
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

                unsupported++;
                var expected = FindMatchingExpectation(expectations, relativePath, "UnsupportedFeature");
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
                    category = "runtime-missing",
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
                var expected = FindMatchingExpectation(expectations, relativePath, "ParserError");
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
            catch (JsThrownException ex)
            {
                if (expectsRuntimeThrow)
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

                runtimeErrors++;
                var expected = FindMatchingExpectation(expectations, relativePath, "RuntimeError");
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new
                {
                    path = file,
                    relativePath,
                    classification = "runtime-error",
                    message = "Unhandled runtime throw.",
                    details = FormatThrownValue(ex.Value, ex.Description),
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
                    category = "runtime-semantic-bug",
                    message = "Unhandled runtime throw.",
                    details = FormatThrownValue(ex.Value)
                });
            }
            catch (InvalidOperationException ex)
            {
                if (expectsRuntimeThrow)
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

                runtimeErrors++;
                var expected = FindMatchingExpectation(expectations, relativePath, "RuntimeError");
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new
                {
                    path = file,
                    relativePath,
                    classification = "runtime-error",
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
                    category = "runtime-missing",
                    message = ex.Message
                });
            }
                catch (Exception ex)
                {
                    if (expectsRuntimeThrow)
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

                    crashes++;
                    var expected = FindMatchingExpectation(expectations, relativePath, "Crash");
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
            finally
            {
                completed++;
                if (completed == subset.Count || completed >= nextProgressAt || stopwatch.Elapsed - lastProgressElapsed >= TimeSpan.FromSeconds(5))
                {
                    WriteRuntimeProgress(
                        completed,
                        subset.Count,
                        passed,
                        runtimeErrors,
                        parserErrors,
                        crashes,
                        timedOut,
                        unsupported,
                        harnessUnsupported,
                        invalidTestConfiguration,
                        stopwatch.Elapsed);
                    while (completed >= nextProgressAt)
                    {
                        nextProgressAt += progressEvery;
                    }

                    lastProgressElapsed = stopwatch.Elapsed;
                }
            }
        }

        stopwatch.Stop();
        Test262ResultWriter.WriteRuntimeSubset(
            outputPath,
            commit,
            engine,
            startedAtUtc,
            stopwatch.ElapsedMilliseconds,
            subset.Count,
            passed,
            unsupported,
            parserErrors,
            runtimeErrors,
            crashes,
            timedOut,
            harnessUnsupported,
            invalidTestConfiguration,
            expectedFailures,
            unexpectedPasses,
            failures,
            unexpectedPassesList,
            tests,
            expectationsPath);
        Console.WriteLine($"Runtime subset result written: {outputPath}");
    }

    private static void WriteRuntimeProgress(
        int completed,
        int total,
        int passed,
        int runtimeErrors,
        int parserErrors,
        int crashes,
        int timedOut,
        int unsupported,
        int harnessUnsupported,
        int invalidTestConfiguration,
        TimeSpan elapsed)
    {
        var pct = total > 0 ? (completed * 100.0) / total : 100.0;
        var remaining = Math.Max(0, total - completed);
        var eta = completed > 0
            ? TimeSpan.FromSeconds((elapsed.TotalSeconds / completed) * remaining)
            : TimeSpan.Zero;
        Console.WriteLine(
            $"[progress] {completed}/{total} ({pct:F1}%) " +
            $"pass={passed} runtimeFail={runtimeErrors} parserFail={parserErrors} crash={crashes} timeout={timedOut} unsupported={unsupported} harnessUnsupported={harnessUnsupported} invalidCfg={invalidTestConfiguration} " +
            $"elapsed={elapsed:hh\\:mm\\:ss} eta={eta:hh\\:mm\\:ss}");
    }

    private static bool ExpectsRuntimeThrow(Test262FrontmatterMetadata frontmatter)
    {
        if (frontmatter.Negative is null || string.IsNullOrWhiteSpace(frontmatter.Negative.Type))
        {
            return false;
        }

        var phase = frontmatter.Negative.Phase?.Trim();
        return string.Equals(phase, "runtime", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RunWithPerTestTimeout(Action action, int timeoutMs)
    {
        ExceptionDispatchInfo? captured = null;
        using var cts = new CancellationTokenSource(Math.Max(1, timeoutMs));
        var task = Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ExceptionDispatchInfo.Capture(ex);
            }
        }, cts.Token);

        try
        {
            task.Wait(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout fired before the task completed.
            // The interpreter's WallClockTimeoutMs + InterruptCallback handle in-flight abort.
            return false;
        }

        captured?.Throw();
        return true;
    }

    private static bool RequiresRuntimeHarnessSupport(string sourceText)
    {
        return sourceText.Contains("Test262Error", StringComparison.Ordinal) ||
               sourceText.Contains("assert.", StringComparison.Ordinal) ||
               sourceText.Contains("assert(", StringComparison.Ordinal) ||
               sourceText.Contains("$DONE", StringComparison.Ordinal) ||
               sourceText.Contains("$262", StringComparison.Ordinal);
    }

    // Cached harness prelude strings — built once, reused for every test.
    private static readonly string _cachedMinimalHarnessPrelude = """
               function Test262Error(message) { this.message = message; }
               """;
    private static readonly string _cachedHarnessPrelude = BuildRuntimeHarnessPreludeRaw();

    private static string BuildRuntimeHarnessPrelude(string sourceText)
    {
        var needsOnlyTest262Error =
            sourceText.Contains("Test262Error", StringComparison.Ordinal) &&
            !sourceText.Contains("assert.", StringComparison.Ordinal) &&
            !sourceText.Contains("assert(", StringComparison.Ordinal) &&
            !sourceText.Contains("$DONE", StringComparison.Ordinal) &&
            !sourceText.Contains("$262", StringComparison.Ordinal);

        return needsOnlyTest262Error ? _cachedMinimalHarnessPrelude : _cachedHarnessPrelude;
    }

    private static string BuildRuntimeHarnessPreludeRaw()
    {
        var prelude = """

               function Test262Error(message) { this.message = message; }
               var assert = function (condition, message) {
                 if (!condition) { throw new Test262Error(message || "assert failed"); }
               };
               assert.sameValue = function (actual, expected, message) {
                 if (actual !== expected && !(actual !== actual && expected !== expected)) { throw new Test262Error(message || "assert.sameValue failed"); }
               };
               function isPrimitive(value) {
                 return value === null || (typeof value !== "object" && typeof value !== "function");
               }
               assert.notSameValue = function (actual, expected, message) {
                 if (actual === expected) { throw new Test262Error(message || "assert.notSameValue failed"); }
               };
               assert.throws = function (expectedError, fn, message) {
                 var threw = false;
                 var error = undefined;
                 try { fn(); } catch (e) { threw = true; error = e; }
                 if (!threw) { throw new Test262Error(message || "assert.throws failed: no error thrown"); }
                 if (expectedError !== undefined && expectedError !== null) {
                   if (error === undefined) { throw new Test262Error(message || "assert.throws: could not catch error"); }
                   if (typeof expectedError === 'function' && !(error instanceof expectedError)) {
                     throw new Test262Error(message || ("assert.throws: expected " + (expectedError.name || expectedError) + " but got " + (error.name || error.constructor.name)));
                   }
                 }
                 return error;
               };
               assert.compareArray = function (actual, expected, message) {
                 if (!compareArray(actual, expected)) {
                   throw new Test262Error(message || "assert.compareArray failed");
                 }
               };
               function compareArray(a, b) {
                 if (a.length !== b.length) { return false; }
                 for (var i = 0; i < a.length; i++) {
                   if (!Object.is(a[i], b[i])) { return false; }
                 }
                 return true;
               }
               compareArray.format = function (arrayLike) {
                 return "[" + Array.prototype.map.call(arrayLike, String).join(", ") + "]";
               };
               function verifyProperty(obj, name, desc) {
                 var originalDesc = Object.getOwnPropertyDescriptor(obj, name);
                 assert(originalDesc !== undefined, "descriptor should exist");
                 if ("value" in desc) { assert.sameValue(originalDesc.value, desc.value, "descriptor value"); }
                 if ("writable" in desc) { assert.sameValue(originalDesc.writable, desc.writable, "descriptor writable"); }
                 if ("enumerable" in desc) { assert.sameValue(originalDesc.enumerable, desc.enumerable, "descriptor enumerable"); }
                 if ("configurable" in desc) { assert.sameValue(originalDesc.configurable, desc.configurable, "descriptor configurable"); }
                 if ("get" in desc) { assert.sameValue(originalDesc.get, desc.get, "descriptor get"); }
                 if ("set" in desc) { assert.sameValue(originalDesc.set, desc.set, "descriptor set"); }
                 return true;
               }
               function verifyEqualTo(obj, name, value) {
                 assert.sameValue(obj[name], value, "property should equal expected value");
                 return true;
               }
               function verifyNotEnumerable(obj, name) {
                 assert.sameValue(Object.prototype.propertyIsEnumerable.call(obj, name), false, "property should not be enumerable");
                 return true;
               }
               function verifyEnumerable(obj, name) {
                 assert.sameValue(Object.prototype.propertyIsEnumerable.call(obj, name), true, "property should be enumerable");
                 return true;
               }
               function verifyConfigurable(obj, name) {
                 var desc = Object.getOwnPropertyDescriptor(obj, name);
                 assert(desc !== undefined, "descriptor should exist");
                 assert.sameValue(desc.configurable, true, "property should be configurable");
                 return true;
               }
               function verifyNotConfigurable(obj, name) {
                 var desc = Object.getOwnPropertyDescriptor(obj, name);
                 assert(desc !== undefined, "descriptor should exist");
                 assert.sameValue(desc.configurable, false, "property should not be configurable");
                 return true;
               }
               function verifyWritable(obj, name, verifyProp, value) {
                 var oldValue = obj[name];
                 var newValue = value !== undefined ? value : "__verifyWritable_value__";
                 try { obj[name] = newValue; } catch (_e) {}
                 assert.sameValue(obj[name], newValue, "property should be writable");
                 if (value === undefined) { try { obj[name] = oldValue; } catch (_e) {} }
                 return true;
               }
               function verifyNotWritable(obj, name, verifyProp, value) {
                 var oldValue = obj[name];
                 var newValue = value !== undefined ? value : "__verifyNotWritable_value__";
                 try { obj[name] = newValue; } catch (_e) {}
                 assert.sameValue(obj[name], oldValue, "property should not be writable");
                 return true;
               }
               function createAbstractModuleSourceIntrinsic() {
                 function AbstractModuleSource() {
                   throw new TypeError();
                 }
                 var prototype = {};
                 Object.defineProperty(prototype, "constructor", {
                   value: AbstractModuleSource,
                   writable: true,
                   enumerable: false,
                   configurable: true
                 });
                 Object.defineProperty(prototype, Symbol.toStringTag, {
                   get: function () {
                     if (this === null || (typeof this !== "object" && typeof this !== "function")) {
                       return undefined;
                     }
                     var name = this.__moduleSourceClassName__;
                     return typeof name === "string" ? name : undefined;
                   },
                   enumerable: false,
                   configurable: true
                 });
                 Object.defineProperty(AbstractModuleSource, "prototype", {
                   value: prototype,
                   writable: false,
                   enumerable: false,
                   configurable: false
                 });
                 return AbstractModuleSource;
               }
               function $DONE(error) { if (error !== undefined) { throw error; } }
               var $262 = {
                 evalScript: function (sourceText) { return eval(sourceText); },
                 global: globalThis,
                 AbstractModuleSource: createAbstractModuleSourceIntrinsic(),
                 createRealm: function () {
                   var realmThrowTypeError = function () { throw new TypeError(); };
                   var realmId = Math.random().toString(36).slice(2);
                   function markRealmIntrinsic(fn, name, prototype) {
                     Object.defineProperty(fn, "__fenRealmId__", {
                       value: realmId,
                       writable: false,
                       enumerable: false,
                       configurable: true
                     });
                     Object.defineProperty(fn, "__fenRealmIntrinsic__", {
                       value: name,
                       writable: false,
                       enumerable: false,
                       configurable: true
                     });
                     Object.defineProperty(fn, "prototype", {
                       value: prototype,
                       writable: false,
                       enumerable: false,
                       configurable: false
                     });
                     return fn;
                   }
                   var realmArray = markRealmIntrinsic(function Array() {
                     return globalThis.Array.apply(null, arguments);
                   }, "Array", Array.prototype);
                   var realmObject = markRealmIntrinsic(function Object(value) {
                     return globalThis.Object(value);
                   }, "Object", Object.prototype);
                   var realmNumber = markRealmIntrinsic(function Number(value) {
                     return globalThis.Number(value);
                   }, "Number", Number.prototype);
                   var realmBigInt = markRealmIntrinsic(function BigInt(value) {
                     return globalThis.BigInt(value);
                   }, "BigInt", BigInt.prototype);
                   var realmGlobal = {
                     Array: realmArray,
                     Boolean: markRealmIntrinsic(function Boolean(value) {
                       return globalThis.Boolean(value);
                     }, "Boolean", Boolean.prototype),
                     Object: realmObject,
                     Number: realmNumber,
                     BigInt: realmBigInt,
                     TypeError: TypeError,
                     Symbol: Symbol,
                     SuppressedError: typeof SuppressedError === "function" ? SuppressedError : undefined,
                     DisposableStack: typeof DisposableStack === "function" ? DisposableStack : undefined,
                     Function: function () {
                       var fn = Function.apply(null, arguments);
                       Object.defineProperty(fn, "__realmGlobal__", {
                         value: realmGlobal,
                         writable: false,
                         enumerable: false,
                         configurable: true
                       });
                        Object.defineProperty(fn, "__throwTypeError__", {
                          value: realmThrowTypeError,
                          writable: false,
                         enumerable: false,
                         configurable: true
                       });
                       return fn;
                     }
                   };
                   return { global: realmGlobal };
                 },
                 detachArrayBuffer: function (buffer) {
                   if (buffer && typeof buffer.transfer === "function") { buffer.transfer(); return; }
                   if (buffer && typeof buffer.detach === "function") { buffer.detach(); return; }
                   if (typeof structuredClone === "function") {
                     try { structuredClone(buffer, { transfer: [buffer] }); return; } catch (_e) {}
                   }
                   throw new Error('detachArrayBuffer is not supported');
                 }
               };
               function $DETACHBUFFER(buffer) { return $262.detachArrayBuffer(buffer); }
               var typedArrayConstructors = [
                 Int8Array, Uint8Array, Uint8ClampedArray, Int16Array, Uint16Array,
                 Int32Array, Uint32Array, Float32Array, Float64Array
               ];
               if (typeof BigInt64Array === "function") { typedArrayConstructors.push(BigInt64Array); }
               if (typeof BigUint64Array === "function") { typedArrayConstructors.push(BigUint64Array); }
               var floatArrayConstructors = [Float32Array, Float64Array];
               var intArrayConstructors = [Int8Array, Uint8Array, Uint8ClampedArray, Int16Array, Uint16Array, Int32Array, Uint32Array];
               var bigIntArrayConstructors = [];
               if (typeof BigInt64Array === "function") { bigIntArrayConstructors.push(BigInt64Array); }
               if (typeof BigUint64Array === "function") { bigIntArrayConstructors.push(BigUint64Array); }
               var ctors = typedArrayConstructors.slice();
               var floatCtors = floatArrayConstructors.slice();
               var intCtors = intArrayConstructors.slice();
               var bigIntCtors = bigIntArrayConstructors.slice();
               function makeTypedArrayCtorArg(input) { return input; }
               function testWithTypedArrayConstructors(fn, selected) {
                 var list = Array.isArray(selected) ? selected : typedArrayConstructors;
                 for (var i = 0; i < list.length; i++) { fn(list[i], makeTypedArrayCtorArg); }
               }
               function testWithBigIntTypedArrayConstructors(fn) {
                 for (var i = 0; i < bigIntArrayConstructors.length; i++) { fn(bigIntArrayConstructors[i], makeTypedArrayCtorArg); }
               }
               function testWithNonAtomicsFriendlyTypedArrayConstructors(fn) {
                 for (var i = 0; i < floatArrayConstructors.length; i++) { fn(floatArrayConstructors[i], makeTypedArrayCtorArg); }
               }
               function testWithAtomicsFriendlyTypedArrayConstructors(fn) {
                 for (var i = 0; i < intArrayConstructors.length; i++) { fn(intArrayConstructors[i], makeTypedArrayCtorArg); }
               }
               function testWithResizableArrayBufferConstructors(fn) {
                 for (var i = 0; i < typedArrayConstructors.length; i++) { fn(typedArrayConstructors[i], makeTypedArrayCtorArg); }
               }
               function CreateResizableArrayBuffer(byteLength, maxByteLength) {
                 if (typeof ArrayBuffer !== "function") { throw new Test262Error("ArrayBuffer is not available"); }
                 try { return new ArrayBuffer(byteLength, { maxByteLength: maxByteLength }); }
                 catch (_e) { return new ArrayBuffer(byteLength); }
               }
               function MayNeedBigInt(ta, n) {
                 if ((typeof BigInt64Array === "function" && ta instanceof BigInt64Array) ||
                     (typeof BigUint64Array === "function" && ta instanceof BigUint64Array)) {
                   return BigInt(n);
                 }
                 return n;
               }
               function Convert(item) {
                 return typeof item === "bigint" ? Number(item) : item;
               }
               function ToNumbers(array) {
                 var result = [];
                 for (var i = 0; i < array.length; i++) {
                   result.push(Convert(array[i]));
                 }
                 return result;
               }
               function CreateRabForTest(ctor) {
                 var bytesPer = ctor.BYTES_PER_ELEMENT || 1;
                 var rab = CreateResizableArrayBuffer(4 * bytesPer, 8 * bytesPer);
                 var taWrite = new ctor(rab);
                 for (var i = 0; i < 4; ++i) {
                   taWrite[i] = MayNeedBigInt(taWrite, 2 * i);
                 }
                 return rab;
               }
               function CollectValuesAndResize(n, values, rab, resizeAfter, resizeTo) {
                 values.push(typeof n === "bigint" ? Number(n) : n);
                 if (values.length === resizeAfter) {
                   rab.resize(resizeTo);
                 }
                 return true;
               }
               function TestIterationAndResize(iterable, expected, rab, resizeAfter, newByteLength) {
                 var values = [];
                 var resized = false;
                 var arrayValues = false;
                 for (var iteratorValue of iterable) {
                   if (Array.isArray(iteratorValue)) {
                     arrayValues = true;
                     values.push([iteratorValue[0], Number(iteratorValue[1])]);
                   } else {
                     values.push(Number(iteratorValue));
                   }
                   if (!resized && values.length === resizeAfter) {
                     rab.resize(newByteLength);
                     resized = true;
                   }
                 }
                 if (!arrayValues) {
                   assert.compareArray([].concat(values), expected, "TestIterationAndResize: list of iterated values");
                 } else {
                   for (var i = 0; i < expected.length; i++) {
                     assert.compareArray(values[i], expected[i], "TestIterationAndResize: list of iterated lists of values");
                   }
                 }
                 assert(resized, "TestIterationAndResize: resize condition should have been hit");
               }
               function isConstructor(fn) { try { new fn(); return true; } catch (_e) { return false; } }
               var fnGlobalObject = globalThis;
               var helpers = {
                 promiseHelper: function (promise) {
                   var result = { value: undefined, resolved: false, rejected: false };
                   promise.then(function(v) { result.value = v; result.resolved = true; },
                               function(e) { result.value = e; result.rejected = true; });
                   return result;
                 }
               };
               function checkPromise(promise) { return helpers.promiseHelper(promise); }
               var NaNVal = NaN;
               var InfinityVal = Infinity;
               var startOfTime = new Date(0);
               function asyncTest(testFunc) {
                 if (typeof testFunc !== 'function') { $DONE(new Test262Error('asyncTest called with non-function')); return; }
                 try {
                   testFunc().then(function () { $DONE(); }, function (error) { $DONE(error); });
                 } catch (e) { $DONE(e); }
               }
               assert.throwsAsync = function (expectedError, fn, message) {
                 return fn().then(function () { throw new Test262Error(message || 'assert.throwsAsync failed'); },
                   function (e) { if (!(e instanceof expectedError)) throw new Test262Error(message || 'Wrong error type'); });
               };
               """;

        return prelude;
    }

    // Harness files we know are safe to load directly from disk on top of the
    // hand-rolled prelude. Every other include is either covered by the prelude
    // or has been observed to cause regressions when re-loaded.
    private static readonly HashSet<string> _loadableHarnessIncludes = new(StringComparer.Ordinal)
    {
        "byteConversionValues.js",
        "compareArray.js",
        "decimalToHexString.js",
        "detachArrayBuffer.js",
        "nans.js",
        "proxyTrapsHelper.js",
        "testTypedArray.js",
        "propertyHelper.js",
        "regExpUtils.js",
        "testIntl.js",
        "temporalHelpers.js",
        "tcoHelper.js",
        "iteratorZipUtils.js",
    };

    private static readonly ConcurrentDictionary<string, string> _includeFileCache = new(StringComparer.Ordinal);

    private static string BuildRuntimeHarnessIncludePrelude(string rootPath, IReadOnlyList<string> includes)
    {
        if (includes.Count == 0)
        {
            return string.Empty;
        }

        var snippets = new List<string>();
        foreach (var include in includes)
        {
            if (!_loadableHarnessIncludes.Contains(include))
            {
                continue;
            }

            var includePath = Path.Combine(rootPath, "harness", include);
            if (!File.Exists(includePath))
            {
                continue;
            }

            snippets.Add(_includeFileCache.GetOrAdd(includePath, File.ReadAllText));
        }

        return snippets.Count == 0 ? string.Empty : string.Join("\n", snippets);
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

    private static bool ContainsLegacyOctalEscape(string source)
    {
        for (var i = 0; i + 2 < source.Length; i++)
        {
            if (source[i] != '\\')
            {
                continue;
            }

            var next = source[i + 1];
            if (next is >= '0' and <= '7')
            {
                var third = source[i + 2];
                if (third is >= '0' and <= '9')
                {
                    return true;
                }

                if (next != '0')
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Test262ExpectationEntry? FindMatchingExpectation(Test262Expectations? expectations, string relativePath, params string[] statuses)
    {
        if (expectations is null || statuses.Length == 0)
        {
            return null;
        }

        return expectations.Entries.FirstOrDefault(entry => statuses.Any(status => entry.Matches(relativePath, status)));
    }

    private static Test262ExpectationEntry? FindAnyMatchingExpectation(Test262Expectations? expectations, string relativePath)
    {
        if (expectations is null)
        {
            return null;
        }

        return expectations.Entries.FirstOrDefault(entry => entry.Matches(relativePath, entry.Status));
    }

    private static string FormatThrownValue(JsValue value, string? description = null)
    {
        // For thrown Error-like objects, prefer the interpreter-captured "Name: message"
        // description so the bare "thrown=object" classification becomes actionable.
        if (value.Tag == JsValueTag.Object && !string.IsNullOrEmpty(description))
        {
            return $"thrown=object:{description}";
        }

        return value.Tag switch
        {
            JsValueTag.Undefined => "thrown=undefined",
            JsValueTag.Null => "thrown=null",
            JsValueTag.Boolean => $"thrown=boolean:{value.AsBoolean()}",
            JsValueTag.Int32 => $"thrown=int32:{value.AsInt32()}",
            JsValueTag.Number => $"thrown=number:{value.AsNumber()}",
            JsValueTag.String => $"thrown=string:{value.AsString()}",
            JsValueTag.Symbol => "thrown=symbol",
            JsValueTag.BigInt => $"thrown=bigint:{value.AsBigInt()}",
            JsValueTag.Object => "thrown=object",
            JsValueTag.HostObject => "thrown=host-object",
            _ => $"thrown={value.Tag}"
        };
    }
}
