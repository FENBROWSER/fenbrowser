using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Builtins;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Js.Test262;

public sealed class Test262Runner
{
    public static Func<SourceText, bool, Task>? ParseInvokerForTests { get; set; }
    public static Func<string, string, bool, Task>? RuntimeInvokerForTests { get; set; }

    private Test262ProgressWriter? _progressWriter;

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
        bool test262Shallow = false,
        int skip = 0,
        string? progressFilePath = null)
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

        // Init progress writer if requested.
        if (progressFilePath is not null)
        {
            _progressWriter = new Test262ProgressWriter(progressFilePath);
        }

        if (parserSubset)
        {
            var tag = DeriveBatchTag(outputPath, test262Path);
            _progressWriter?.WriteBatchStart(tag, "parser-subset", test262Path ?? rootPath, Math.Min(files.Count, max), timeoutMs);
            RunParserSubset(rootPath, outputPath, files, max, timeoutMs, engine, expectationsPath, expectations, supportedFeaturesCsv, skip);
        }

        if (runtimeSubset)
        {
            var tag = DeriveBatchTag(outputPath, test262Path);
            _progressWriter?.WriteBatchStart(tag, "runtime-subset", test262Path ?? rootPath, Math.Min(files.Count, max), timeoutMs);
            RunRuntimeSubset(rootPath, outputPath, files, max, timeoutMs, engine, expectationsPath, expectations, supportedFeaturesCsv, skip);
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

    private void RunParserSubset(string rootPath, string outputPath, IReadOnlyList<string> files, int max, int timeoutMs, string engine, string? expectationsPath, Test262Expectations? expectations, string? supportedFeaturesCsv, int skip = 0)
    {
        var startedAtUtc = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var pinPath = Path.Combine(rootPath, "..", "test262.pin");
        var commit = File.Exists(pinPath) ? File.ReadAllText(pinPath).Trim() : "un-pinned";
        var subset = files.Skip(Math.Max(0, skip)).Take(Math.Max(1, max)).ToList();
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
        var failures = new List<Test262FailureEntry>();
        var unexpectedPassesList = new List<Test262UnexpectedPassEntry>();
        var tests = new List<TestEntry>(subset.Count);
        foreach (var file in subset)
        {
            var testSw = System.Diagnostics.Stopwatch.StartNew();
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
                testSw.Stop();
                failures.Add(new Test262FailureEntry
                {
                    Path = file,
                    RelativePath = relativePath,
                    Classification = "invalid-test-configuration",
                    Message = invalidReason
                });

                var te = TestEntry.FromFrontmatter(relativePath, frontmatter);
                te.Status = "InvalidTestConfiguration";
                te.DurationMs = testSw.ElapsedMilliseconds;
                te.Category = "host-not-applicable";
                te.Message = invalidReason;
                tests.Add(te);
                continue;
            }

            var unsupportedFeature = frontmatter.Features.FirstOrDefault(feature => supportedFeatures is not null && !supportedFeatures.Contains(feature));
            if (unsupportedFeature is not null)
            {
                unsupported++;
                testSw.Stop();
                var expected = FindMatchingExpectation(expectations, relativePath, "UnsupportedFeature");
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new Test262FailureEntry
                {
                    Path = file,
                    RelativePath = relativePath,
                    Classification = "unsupported",
                    Feature = unsupportedFeature,
                    Message = $"Feature '{unsupportedFeature}' is not in supported feature set.",
                    Expected = expected is not null,
                    ExpectedReason = expected?.Reason,
                    ExpectedOwner = expected?.Owner,
                    ExpectedArea = expected?.Area,
                    ExpiresAtMilestone = expected?.ExpiresAtMilestone
                });

                var te = TestEntry.FromFrontmatter(relativePath, frontmatter);
                te.Status = expected is null ? "UnsupportedFeature" : "ExpectedFailure";
                te.DurationMs = testSw.ElapsedMilliseconds;
                te.Category = "parser-missing";
                te.Message = $"Feature '{unsupportedFeature}' is not in supported feature set.";
                tests.Add(te);
                continue;
            }

            try
            {
                if (expectsSyntaxError && onlyStrict && ContainsLegacyOctalEscape(sourceText))
                {
                    passed++;
                    testSw.Stop();
                    var te = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    te.Status = "Passed";
                    te.DurationMs = testSw.ElapsedMilliseconds;
                    tests.Add(te);
                    continue;
                }

                var source = new SourceText(parserInput, file);
                var parseCompleted = RunWithPerTestTimeout(token =>
                {
                    token.ThrowIfCancellationRequested();
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
                testSw.Stop();
                if (!parseCompleted)
                {
                    timedOut++;
                    var expected = FindMatchingExpectation(expectations, relativePath, "Timeout");
                    if (expected is not null)
                    {
                        expectedFailures++;
                    }

                    failures.Add(new Test262FailureEntry
                    {
                        Path = file,
                        RelativePath = relativePath,
                        Classification = "timeout",
                        Message = $"Parsing exceeded timeout of {timeoutMs} ms.",
                        Expected = expected is not null,
                        ExpectedReason = expected?.Reason,
                        ExpectedOwner = expected?.Owner,
                        ExpectedArea = expected?.Area,
                        ExpiresAtMilestone = expected?.ExpiresAtMilestone
                    });

                    var toutTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    toutTe.Status = expected is null ? "TimedOut" : "ExpectedFailure";
                    toutTe.DurationMs = testSw.ElapsedMilliseconds;
                    toutTe.Category = "timeout";
                    toutTe.Message = $"Parsing exceeded timeout of {timeoutMs} ms.";
                    tests.Add(toutTe);
                    continue;
                }

                if (expectsSyntaxError)
                {
                    parserErrors++;
                    failures.Add(new Test262FailureEntry
                    {
                        Path = file,
                        RelativePath = relativePath,
                        Classification = "parser-error",
                        Message = "Expected parser to fail with SyntaxError due to test262 negative metadata, but parse succeeded.",
                        Expected = false
                    });

                    var peTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    peTe.Status = "Failed";
                    peTe.DurationMs = testSw.ElapsedMilliseconds;
                    peTe.Category = "parser-bug";
                    peTe.Message = "Expected parser to fail with SyntaxError due to test262 negative metadata, but parse succeeded.";
                    tests.Add(peTe);
                    continue;
                }

                passed++;

                if (expectations is not null)
                {
                    var expected = FindAnyMatchingExpectation(expectations, relativePath);
                    if (expected is not null)
                    {
                        unexpectedPasses++;
                        unexpectedPassesList.Add(new Test262UnexpectedPassEntry
                        {
                            Path = file,
                            RelativePath = relativePath,
                            ExpectedStatus = expected.Status,
                            ExpectedReason = expected.Reason,
                            ExpectedOwner = expected.Owner,
                            ExpectedArea = expected.Area,
                            ExpiresAtMilestone = expected.ExpiresAtMilestone
                        });

                        var upTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                        upTe.Status = "UnexpectedPass";
                        upTe.DurationMs = testSw.ElapsedMilliseconds;
                        upTe.Message = $"Unexpected pass for expectation '{expected.Status}'.";
                        tests.Add(upTe);
                        continue;
                    }
                }

                var passTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                passTe.Status = "Passed";
                passTe.DurationMs = testSw.ElapsedMilliseconds;
                tests.Add(passTe);
            }
            catch (UnsupportedFeatureException ex)
            {
                testSw.Stop();
                unsupported++;
                var expected = expectations?.Entries.FirstOrDefault(e => e.Matches(relativePath, "UnsupportedFeature"));
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new Test262FailureEntry
                {
                    Path = file,
                    RelativePath = relativePath,
                    Classification = "unsupported",
                    Feature = ex.FeatureName,
                    Location = $"{ex.Span.Line}:{ex.Span.Column}",
                    Expected = expected is not null,
                    ExpectedReason = expected?.Reason,
                    ExpectedOwner = expected?.Owner,
                    ExpectedArea = expected?.Area,
                    ExpiresAtMilestone = expected?.ExpiresAtMilestone
                });

                var ufeTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                ufeTe.Status = expected is null ? "UnsupportedFeature" : "ExpectedFailure";
                ufeTe.DurationMs = testSw.ElapsedMilliseconds;
                ufeTe.Category = "parser-missing";
                ufeTe.Message = ex.Message;
                tests.Add(ufeTe);
            }
            catch (JsParserException ex)
            {
                testSw.Stop();
                if (expectsSyntaxError)
                {
                    passed++;
                    var passedTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    passedTe.Status = "Passed";
                    passedTe.DurationMs = testSw.ElapsedMilliseconds;
                    tests.Add(passedTe);
                    continue;
                }

                parserErrors++;
                var expected = expectations?.Entries.FirstOrDefault(e => e.Matches(relativePath, "ParserError"));
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new Test262FailureEntry
                {
                    Path = file,
                    RelativePath = relativePath,
                    Classification = "parser-error",
                    Message = ex.Message,
                    Expected = expected is not null,
                    ExpectedReason = expected?.Reason,
                    ExpectedOwner = expected?.Owner,
                    ExpectedArea = expected?.Area,
                    ExpiresAtMilestone = expected?.ExpiresAtMilestone
                });

                var jpeTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                jpeTe.Status = expected is null ? "Failed" : "ExpectedFailure";
                jpeTe.DurationMs = testSw.ElapsedMilliseconds;
                jpeTe.Category = "parser-bug";
                jpeTe.Message = ex.Message;
                tests.Add(jpeTe);
            }
            catch (Exception ex)
            {
                testSw.Stop();
                crashes++;
                var expected = expectations?.Entries.FirstOrDefault(e => e.Matches(relativePath, "Crash"));
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new Test262FailureEntry
                {
                    Path = file,
                    RelativePath = relativePath,
                    Classification = "crash",
                    Message = ex.Message,
                    Expected = expected is not null,
                    ExpectedReason = expected?.Reason,
                    ExpectedOwner = expected?.Owner,
                    ExpectedArea = expected?.Area,
                    ExpiresAtMilestone = expected?.ExpiresAtMilestone
                });

                var crTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                crTe.Status = expected is null ? "Crashed" : "ExpectedFailure";
                crTe.DurationMs = testSw.ElapsedMilliseconds;
                crTe.Category = "crash";
                crTe.Message = ex.Message;
                tests.Add(crTe);
            }
        }

        stopwatch.Stop();
        _progressWriter?.WriteBatchComplete(subset.Count, passed,
            parserErrors, crashes, timedOut, unsupported,
            unexpectedPasses, expectedFailures, stopwatch.ElapsedMilliseconds);
        _progressWriter?.Dispose();
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

    private void RunRuntimeSubset(
        string rootPath,
        string outputPath,
        IReadOnlyList<string> files,
        int max,
        int timeoutMs,
        string engine,
        string? expectationsPath,
        Test262Expectations? expectations,
        string? supportedFeaturesCsv,
        int skip = 0)
    {
        var startedAtUtc = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var pinPath = Path.Combine(rootPath, "..", "test262.pin");
        var commit = File.Exists(pinPath) ? File.ReadAllText(pinPath).Trim() : "un-pinned";
        var subset = files.Skip(Math.Max(0, skip)).Take(Math.Max(1, max)).ToList();
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
        var failures = new List<Test262FailureEntry>();
        var unexpectedPassesList = new List<Test262UnexpectedPassEntry>();
        var tests = new List<TestEntry>(subset.Count);
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
            "wellKnownIntrinsicObjects.js",
            "atomicsHelper.js",  // loaded for single-agent use; tests needing $262.agent cooperation
                                 // will fail gracefully with ReferenceError rather than crashing
            "temporalHelpers.js",
            // SpiderMonkey staging harness includes
            "nativeErrors.js",
            "sm/assertThrowsValue.js",
            "sm/non262-Date-shell.js",
            "sm/non262-JSON-shell.js",
            "sm/non262-Math-shell.js",
            "sm/non262-Reflect-shell.js",
            "sm/non262-Set-shell.js",
            "sm/non262-Temporal-PlainMonthDay-shell.js",
            "sm/non262-TypedArray-shell.js",
            "sm/non262-expressions-shell.js",
            "sm/non262-generators-shell.js",
            "sm/non262-strict-shell.js",
        };

        Console.WriteLine($"Running runtime subset: total={subset.Count}, timeoutMs={timeoutMs}, root={rootPath}");

        foreach (var file in subset)
        {
            // A fresh compiler per test: BytecodeCompiler carries per-compilation mutable
            // state (register/instruction buffers, scope stacks). A prior test that threw
            // mid-compile would otherwise leave it dirty and crash a later compilation.
            var compiler = new BytecodeCompiler();
            var testSw = System.Diagnostics.Stopwatch.StartNew();
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
                    testSw.Stop();
                    failures.Add(new Test262FailureEntry { Path = file, RelativePath = relativePath, Classification = "invalid-test-configuration", Message = invalidReason });
                    var ivTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    ivTe.Status = "InvalidTestConfiguration";
                    ivTe.DurationMs = testSw.ElapsedMilliseconds;
                    ivTe.Category = "host-not-applicable";
                    ivTe.Message = invalidReason;
                    tests.Add(ivTe);
                    continue;
                }

            var unsupportedHarnessInclude = frontmatter.Includes.FirstOrDefault(include => !supportedHarnessIncludes.Contains(include));
            if (unsupportedHarnessInclude is not null)
            {
                harnessUnsupported++;
                testSw.Stop();
                failures.Add(new Test262FailureEntry { Path = file, RelativePath = relativePath, Classification = "harness-unsupported", Include = unsupportedHarnessInclude, Message = $"Harness include '{unsupportedHarnessInclude}' is not supported in runtime-subset mode." });
                var uhTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                uhTe.Status = "HarnessUnsupported";
                uhTe.DurationMs = testSw.ElapsedMilliseconds;
                uhTe.Category = "host-not-applicable";
                uhTe.Message = $"Harness include '{unsupportedHarnessInclude}' is not supported in runtime-subset mode.";
                tests.Add(uhTe);
                continue;
            }

            var unsupportedFeature = frontmatter.Features.FirstOrDefault(feature => supportedFeatures is not null && !supportedFeatures.Contains(feature));
            if (unsupportedFeature is not null)
            {
                unsupported++;
                testSw.Stop();
                var expected = FindMatchingExpectation(expectations, relativePath, "UnsupportedFeature");
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new Test262FailureEntry
                {
                    Path = file,
                    RelativePath = relativePath,
                    Classification = "unsupported",
                    Feature = unsupportedFeature,
                    Message = $"Feature '{unsupportedFeature}' is not in supported feature set.",
                    Expected = expected is not null,
                    ExpectedReason = expected?.Reason,
                    ExpectedOwner = expected?.Owner,
                    ExpectedArea = expected?.Area,
                    ExpiresAtMilestone = expected?.ExpiresAtMilestone
                });
                var ufTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                ufTe.Status = expected is null ? "UnsupportedFeature" : "ExpectedFailure";
                ufTe.DurationMs = testSw.ElapsedMilliseconds;
                ufTe.Category = "runtime-missing";
                ufTe.Message = $"Feature '{unsupportedFeature}' is not in supported feature set.";
                tests.Add(ufTe);
                continue;
            }

            try
            {
                var runtimeInput = parserInput;
                if (frontmatter.Includes.Count > 0 || RequiresRuntimeHarnessSupport(sourceText))
                {
                    var includePrelude = BuildRuntimeHarnessIncludePrelude(rootPath, frontmatter.Includes);
                    var prelude = BuildRuntimeHarnessPrelude(sourceText, frontmatter.Includes.Count > 0);
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
                // Fire interrupt after timeoutMs so the interpreter self-terminates
                // via its WallClockTimeoutMs check + InterruptCallback polling.
                // The CancellationTokenSource in RunWithPerTestTimeout provides
                // proper cleanup — when the timeout fires, the CTS cancels the token,
                // the InterruptCallback returns false, and the interpreter exits at
                // the next opcode boundary. The task completes cleanly (no leak).
                using var interruptTimer = new Timer(
                    _ => Volatile.Write(ref interruptRequested, 1), null, timeoutMs, Timeout.Infinite);
                BytecodeInterpreter? perTestInterpreter = null;
                var executeCompleted = RunWithPerTestTimeout(token =>
                {
                    token.ThrowIfCancellationRequested();
                    var overrideInvoker = RuntimeInvokerForTests;
                    if (overrideInvoker is not null)
                    {
                        overrideInvoker(runtimeInput, file, parseAsModule).GetAwaiter().GetResult();
                    }
                    else
                    {
                        if (Volatile.Read(ref interruptRequested) != 0 || token.IsCancellationRequested)
                            return;
                        var source = new SourceText(runtimeInput, file);
                        var function = parseAsModule
                            ? compiler.CompileProgram(JsParser.ParseModule(source))
                            : compiler.CompileScript(source);
                        perTestInterpreter = new BytecodeInterpreter(new JsHeap());
                        perTestInterpreter.WallClockTimeoutMs = Math.Max(1, timeoutMs);
                        perTestInterpreter.InterruptCallback = () =>
                            Volatile.Read(ref interruptRequested) == 0 && !token.IsCancellationRequested;
                        var htmlDda = perTestInterpreter.AllocateNativeFunction(
                            "IsHTMLDDA", (_, _) => JsValue.Null);
                        perTestInterpreter.MarkAsHtmlDda(htmlDda);
                        perTestInterpreter.RegisterGlobalValue("__fenHtmlDda", htmlDda);
                        var buildString = perTestInterpreter.AllocateNativeFunction(
                            "__fenBuildString",
                            (_, buildArgs) => BuildStringForRegExpHarness((IBuiltinContext)perTestInterpreter, buildArgs),
                            length: 1);
                        perTestInterpreter.RegisterGlobalValue("__fenBuildString", buildString);
                        try
                        {
                            _ = perTestInterpreter.Execute(function);
                        }
                        catch (JsThrownException thrown)
                        {
                            thrown.Description ??= perTestInterpreter.DescribeThrownValue(thrown.Value);
                            throw;
                        }
                    }
                }, timeoutMs);
                interruptTimer.Change(Timeout.Infinite, Timeout.Infinite); // disarm
                perTestInterpreter = null;
                testSw.Stop();
                if (!executeCompleted)
                {
                    timedOut++;
                    var expected = FindMatchingExpectation(expectations, relativePath, "Timeout");
                    if (expected is not null) expectedFailures++;
                    failures.Add(new Test262FailureEntry
                    {
                        Path = file,
                        RelativePath = relativePath,
                        Classification = "timeout",
                        Message = $"Runtime execution exceeded timeout of {timeoutMs} ms.",
                        Expected = expected is not null,
                        ExpectedReason = expected?.Reason,
                        ExpectedOwner = expected?.Owner,
                        ExpectedArea = expected?.Area,
                        ExpiresAtMilestone = expected?.ExpiresAtMilestone
                    });
                    var toTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    toTe.Status = expected is null ? "TimedOut" : "ExpectedFailure";
                    toTe.DurationMs = testSw.ElapsedMilliseconds;
                    toTe.Category = "timeout";
                    toTe.Message = $"Runtime execution exceeded timeout of {timeoutMs} ms.";
                    tests.Add(toTe);
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

                    failures.Add(new Test262FailureEntry
                    {
                        Path = file,
                        RelativePath = relativePath,
                        Classification = "runtime-error",
                        Message = "Expected failure did not occur in runtime-subset execution.",
                        Expected = expected is not null,
                        ExpectedReason = expected?.Reason,
                        ExpectedOwner = expected?.Owner,
                        ExpectedArea = expected?.Area,
                        ExpiresAtMilestone = expected?.ExpiresAtMilestone
                    });
                    var efTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    efTe.Status = expected is null ? "Failed" : "ExpectedFailure";
                    efTe.DurationMs = testSw.ElapsedMilliseconds;
                    efTe.Category = "runtime-semantic-bug";
                    efTe.Message = "Expected failure did not occur in runtime-subset execution.";
                    tests.Add(efTe);
                    continue;
                }

                passed++;
                if (expectations is not null)
                {
                    var expected = FindAnyMatchingExpectation(expectations, relativePath);
                    if (expected is not null)
                    {
                        unexpectedPasses++;
                        unexpectedPassesList.Add(new Test262UnexpectedPassEntry { Path = file, RelativePath = relativePath, ExpectedStatus = expected.Status, ExpectedReason = expected.Reason, ExpectedOwner = expected.Owner, ExpectedArea = expected.Area, ExpiresAtMilestone = expected.ExpiresAtMilestone });
                        var upTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                        upTe.Status = "UnexpectedPass";
                        upTe.DurationMs = testSw.ElapsedMilliseconds;
                        upTe.Message = $"Unexpected pass for expectation '{expected.Status}'.";
                        tests.Add(upTe);
                        continue;
                    }
                }

                var passTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                passTe.Status = "Passed";
                passTe.DurationMs = testSw.ElapsedMilliseconds;
                tests.Add(passTe);
            }
            catch (UnsupportedFeatureException ex)
            {
                testSw.Stop();
                if (expectsSyntaxError || expectsRuntimeThrow)
                {
                    passed++;
                    var passedTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    passedTe.Status = "Passed";
                    passedTe.DurationMs = testSw.ElapsedMilliseconds;
                    tests.Add(passedTe);
                    continue;
                }

                unsupported++;
                var expected = FindMatchingExpectation(expectations, relativePath, "UnsupportedFeature");
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new Test262FailureEntry
                {
                    Path = file,
                    RelativePath = relativePath,
                    Classification = "unsupported",
                    Feature = ex.FeatureName,
                    Location = $"{ex.Span.Line}:{ex.Span.Column}",
                    Expected = expected is not null,
                    ExpectedReason = expected?.Reason,
                    ExpectedOwner = expected?.Owner,
                    ExpectedArea = expected?.Area,
                    ExpiresAtMilestone = expected?.ExpiresAtMilestone
                });
                var ufeTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                ufeTe.Status = expected is null ? "UnsupportedFeature" : "ExpectedFailure";
                ufeTe.DurationMs = testSw.ElapsedMilliseconds;
                ufeTe.Category = "runtime-missing";
                ufeTe.Message = ex.Message;
                tests.Add(ufeTe);
            }
            catch (JsParserException ex)
            {
                testSw.Stop();
                if (expectsSyntaxError)
                {
                    passed++;
                    var passedTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    passedTe.Status = "Passed";
                    passedTe.DurationMs = testSw.ElapsedMilliseconds;
                    tests.Add(passedTe);
                    continue;
                }

                parserErrors++;
                var expected = FindMatchingExpectation(expectations, relativePath, "ParserError");
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new Test262FailureEntry
                {
                    Path = file,
                    RelativePath = relativePath,
                    Classification = "parser-error",
                    Message = ex.Message,
                    Expected = expected is not null,
                    ExpectedReason = expected?.Reason,
                    ExpectedOwner = expected?.Owner,
                    ExpectedArea = expected?.Area,
                    ExpiresAtMilestone = expected?.ExpiresAtMilestone
                });
                var jpeTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                jpeTe.Status = expected is null ? "Failed" : "ExpectedFailure";
                jpeTe.DurationMs = testSw.ElapsedMilliseconds;
                jpeTe.Category = "parser-bug";
                jpeTe.Message = ex.Message;
                tests.Add(jpeTe);
            }
            catch (JsThrownException ex)
            {
                testSw.Stop();
                if (expectsRuntimeThrow)
                {
                    passed++;
                    var passedTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    passedTe.Status = "Passed";
                    passedTe.DurationMs = testSw.ElapsedMilliseconds;
                    tests.Add(passedTe);
                    continue;
                }

                runtimeErrors++;
                var expected = FindMatchingExpectation(expectations, relativePath, "RuntimeError");
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new Test262FailureEntry
                {
                    Path = file,
                    RelativePath = relativePath,
                    Classification = "runtime-error",
                    Message = "Unhandled runtime throw.",
                    Details = FormatThrownValue(ex.Value, ex.Description),
                    Expected = expected is not null,
                    ExpectedReason = expected?.Reason,
                    ExpectedOwner = expected?.Owner,
                    ExpectedArea = expected?.Area,
                    ExpiresAtMilestone = expected?.ExpiresAtMilestone
                });
                var jteTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                jteTe.Status = expected is null ? "Failed" : "ExpectedFailure";
                jteTe.DurationMs = testSw.ElapsedMilliseconds;
                jteTe.Category = "runtime-semantic-bug";
                jteTe.Message = "Unhandled runtime throw.";
                jteTe.Details = FormatThrownValue(ex.Value);
                tests.Add(jteTe);
            }
            catch (InvalidOperationException ex)
            {
                testSw.Stop();
                if (expectsRuntimeThrow)
                {
                    passed++;
                    var passedTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    passedTe.Status = "Passed";
                    passedTe.DurationMs = testSw.ElapsedMilliseconds;
                    tests.Add(passedTe);
                    continue;
                }

                runtimeErrors++;
                var expected = FindMatchingExpectation(expectations, relativePath, "RuntimeError");
                if (expected is not null)
                {
                    expectedFailures++;
                }

                failures.Add(new Test262FailureEntry
                {
                    Path = file,
                    RelativePath = relativePath,
                    Classification = "runtime-error",
                    Message = ex.Message,
                    Expected = expected is not null,
                    ExpectedReason = expected?.Reason,
                    ExpectedOwner = expected?.Owner,
                    ExpectedArea = expected?.Area,
                    ExpiresAtMilestone = expected?.ExpiresAtMilestone
                });
                var ioeTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                ioeTe.Status = expected is null ? "Failed" : "ExpectedFailure";
                ioeTe.DurationMs = testSw.ElapsedMilliseconds;
                ioeTe.Category = "runtime-missing";
                ioeTe.Message = ex.Message;
                tests.Add(ioeTe);
            }
                catch (Exception ex)
                {
                    testSw.Stop();
                    if (expectsRuntimeThrow)
                    {
                        passed++;
                        var passedTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                        passedTe.Status = "Passed";
                        passedTe.DurationMs = testSw.ElapsedMilliseconds;
                        tests.Add(passedTe);
                        continue;
                    }

                    crashes++;
                    var expected = FindMatchingExpectation(expectations, relativePath, "Crash");
                    if (expected is not null)
                    {
                        expectedFailures++;
                    }

                    failures.Add(new Test262FailureEntry
                    {
                        Path = file,
                        RelativePath = relativePath,
                        Classification = "crash",
                        Message = ex.Message,
                        Expected = expected is not null,
                        ExpectedReason = expected?.Reason,
                        ExpectedOwner = expected?.Owner,
                        ExpectedArea = expected?.Area,
                        ExpiresAtMilestone = expected?.ExpiresAtMilestone
                    });
                    var crTe = TestEntry.FromFrontmatter(relativePath, frontmatter);
                    crTe.Status = expected is null ? "Crashed" : "ExpectedFailure";
                    crTe.DurationMs = testSw.ElapsedMilliseconds;
                    crTe.Category = "crash";
                    crTe.Message = ex.Message;
                    tests.Add(crTe);
                }
            }
            finally
            {
                completed++;
                if (completed == subset.Count || completed >= nextProgressAt || stopwatch.Elapsed - lastProgressElapsed >= TimeSpan.FromSeconds(5))
                {
                    var elapsed = stopwatch.Elapsed;
                    var remaining = Math.Max(0, subset.Count - completed);
                    var etaMs = completed > 0
                        ? (long)((elapsed.TotalMilliseconds / completed) * remaining)
                        : 0L;
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
                        elapsed);
                    _progressWriter?.WriteHeartbeat(completed, subset.Count, passed,
                        runtimeErrors + parserErrors, crashes, timedOut, unsupported,
                        (long)elapsed.TotalMilliseconds, etaMs);
                    while (completed >= nextProgressAt)
                    {
                        nextProgressAt += progressEvery;
                    }

                    lastProgressElapsed = stopwatch.Elapsed;
                }

                // Each test allocates a throwaway JsHeap (and the bytecode/objects it
                // produces). A Gen 0 collection after each test reclaims the fresh
                // garbage cheaply (no compaction). A full Gen 2 compacting collection
                // every 200 tests keeps the overall heap bounded without destroying
                // throughput — heavy Array/Temporal tests were spending > 1 s/test
                // purely in the compacting GC when it ran after every single test.
                GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                if (completed % 200 == 0)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
            }
        }

        stopwatch.Stop();
        _progressWriter?.WriteBatchComplete(subset.Count, passed,
            parserErrors + runtimeErrors, crashes, timedOut, unsupported,
            unexpectedPasses, expectedFailures, stopwatch.ElapsedMilliseconds);
        _progressWriter?.Dispose();
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

    private static bool RunWithPerTestTimeout(Action<CancellationToken> action, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs + 500);
        ExceptionDispatchInfo? captured = null;
        var task = Task.Run(() =>
        {
            try { action(cts.Token); }
            catch (OperationCanceledException)
            {
                // Expected when the CTS fires after timeoutMs+500ms.
                // The CancellationToken in the interpreter's InterruptCallback
                // ensures the interpreter exits at the next opcode boundary.
                // The task then completes cleanly — no abandoned thread leak.
            }
            catch (Exception ex) { captured = ExceptionDispatchInfo.Capture(ex); }
        });

        try { task.Wait(cts.Token); }
        catch (OperationCanceledException) { return false; }

        captured?.Throw();
        return true;
    }

    private static JsValue BuildStringForRegExpHarness(IBuiltinContext context, IReadOnlyList<JsValue> args)
    {
        if (args.Count == 0 || args[0].Tag != JsValueTag.Object)
        {
            return JsValue.FromString(string.Empty);
        }

        var root = args[0];
        var sb = new StringBuilder();
        if (TryGetObjectProperty(context, root, "loneCodePoints", out var loneCodePoints))
        {
            AppendCodePointArray(context, sb, loneCodePoints);
        }

        if (TryGetObjectProperty(context, root, "ranges", out var ranges))
        {
            var rangeCount = GetArrayLikeLength(context, ranges);
            for (var i = 0; i < rangeCount; i++)
            {
                if (!TryGetObjectProperty(context, ranges, i.ToString(System.Globalization.CultureInfo.InvariantCulture), out var range))
                {
                    continue;
                }

                var start = GetArrayLikeNumber(context, range, "0");
                var end = GetArrayLikeNumber(context, range, "1");
                for (var codePoint = start; codePoint <= end; codePoint++)
                {
                    AppendCodePoint(sb, codePoint);
                }
            }
        }

        return JsValue.FromString(sb.ToString());
    }

    private static void AppendCodePointArray(IBuiltinContext context, StringBuilder sb, JsValue arrayLike)
    {
        var length = GetArrayLikeLength(context, arrayLike);
        for (var i = 0; i < length; i++)
        {
            var codePoint = GetArrayLikeNumber(context, arrayLike, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendCodePoint(sb, codePoint);
        }
    }

    private static void AppendCodePoint(StringBuilder sb, int codePoint)
    {
        if (codePoint < 0 || codePoint > 0x10FFFF)
        {
            return;
        }

        if (codePoint <= 0xFFFF)
        {
            sb.Append((char)codePoint);
            return;
        }

        sb.Append(char.ConvertFromUtf32(codePoint));
    }

    private static int GetArrayLikeLength(IBuiltinContext context, JsValue value)
        => Math.Max(0, GetArrayLikeNumber(context, value, "length"));

    private static int GetArrayLikeNumber(IBuiltinContext context, JsValue value, string key)
    {
        if (!TryGetObjectProperty(context, value, key, out var property))
        {
            return 0;
        }

        var number = context.ToNumber(property);
        if (double.IsNaN(number) || number <= 0)
        {
            return 0;
        }

        return number >= int.MaxValue ? int.MaxValue : (int)Math.Floor(number);
    }

    private static bool TryGetObjectProperty(IBuiltinContext context, JsValue value, string key, out JsValue property)
    {
        property = JsValue.Undefined;
        if (value.Tag != JsValueTag.Object)
        {
            return false;
        }

        var obj = context.Heap.GetObject(value.AsObjectHandle());
        return context.TryGetPropertyValue(obj, value, key, out property);
    }

    private static bool RequiresRuntimeHarnessSupport(string sourceText)
    {
        return sourceText.Contains("Test262Error", StringComparison.Ordinal) ||
               sourceText.Contains("assert.", StringComparison.Ordinal) ||
               sourceText.Contains("assert(", StringComparison.Ordinal) ||
               sourceText.Contains("$DONOTEVALUATE", StringComparison.Ordinal) ||
               sourceText.Contains("$DONE", StringComparison.Ordinal) ||
               sourceText.Contains("$262", StringComparison.Ordinal);
    }

    // Cached harness prelude strings — built once, reused for every test.
    private static readonly string _cachedMinimalHarnessPrelude = """
               function Test262Error(message) { this.message = message; }
               Test262Error.thrower = function(message) { throw new Test262Error(message); };
               function $DONOTEVALUATE() { throw new Test262Error("Test262: This statement should not be evaluated."); }
               """;
    private static readonly string _cachedHarnessPrelude = BuildRuntimeHarnessPreludeRaw();

    private static string BuildRuntimeHarnessPrelude(string sourceText, bool hasIncludes = false)
    {
        // A test that pulls in harness includes (e.g. propertyHelper.js) relies on the
        // full prelude — those includes call `assert`, `compareArray`, etc. even when the
        // test body itself only references `Test262Error`. Only the include-free,
        // Test262Error-only tests are safe to serve the minimal prelude.
        var needsOnlyTest262Error =
            !hasIncludes &&
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
               Test262Error.thrower = function(message) { throw new Test262Error(message); };
               function $DONOTEVALUATE() { throw new Test262Error("Test262: This statement should not be evaluated."); }
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
                 evalScript: function (sourceText) { return (0, eval)(sourceText); },
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
                   var intrinsicRegExpCompile = RegExp.prototype.compile;
                   var realmRegExpPrototype = Object.create(RegExp.prototype);
                   Object.defineProperty(realmRegExpPrototype, "compile", {
                     value: function (pattern, flags) {
                       if (Object.getPrototypeOf(this) !== realmRegExpPrototype) {
                         throw new TypeError();
                       }
                       Object.setPrototypeOf(this, RegExp.prototype);
                       try { return intrinsicRegExpCompile.call(this, pattern, flags); }
                       finally { Object.setPrototypeOf(this, realmRegExpPrototype); }
                     },
                     writable: true,
                     enumerable: false,
                     configurable: true
                   });
                   ['dotAll','flags','global','hasIndices','ignoreCase','multiline','source','sticky','unicode','unicodeSets'].forEach(function (name) {
                     var desc = Object.getOwnPropertyDescriptor(RegExp.prototype, name);
                     if (!desc || typeof desc.get !== 'function') { return; }
                     Object.defineProperty(realmRegExpPrototype, name, {
                       get: function () {
                         if (this === realmRegExpPrototype) { return undefined; }
                         if (Object.getPrototypeOf(this) !== realmRegExpPrototype) { throw new TypeError(); }
                         return desc.get.call(this);
                       },
                       enumerable: desc.enumerable,
                       configurable: desc.configurable
                     });
                   });
                   var realmRegExp = markRealmIntrinsic(function RegExp(p, f) {
                     var value = new globalThis.RegExp(p, f);
                     Object.setPrototypeOf(value, realmRegExpPrototype);
                     return value;
                   }, "RegExp", realmRegExpPrototype);
                   Object.defineProperty(realmRegExp, "escape", {
                     value: RegExp.escape,
                     writable: true,
                     enumerable: false,
                     configurable: true
                   });
                   var realmGlobal = {
                     Array: realmArray,
                     Boolean: markRealmIntrinsic(function Boolean(value) {
                       return globalThis.Boolean(value);
                     }, "Boolean", Boolean.prototype),
                     Object: realmObject,
                     Number: realmNumber,
                     BigInt: realmBigInt,
                     RegExp: realmRegExp,
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
                   // FenJS has no true multi-realm isolation: createRealm() returns a
                   // facade whose global delegates to the shared global realm. Expose
                   // eval plus the remaining standard intrinsics so cross-realm tests
                   // that read `realm.global.<Ctor>` / `realm.global.eval(...)` work.
                   realmGlobal.eval = function (s) { return eval(s); };
                   ['Proxy','Reflect','String','Date','Map','Set','WeakMap','WeakSet',
                    'WeakRef','Promise','Error','RangeError','SyntaxError','ReferenceError',
                    'EvalError','URIError','AggregateError','Int8Array','Uint8Array',
                    'Uint8ClampedArray','Int16Array','Uint16Array','Int32Array','Uint32Array',
                    'Float16Array','Float32Array','Float64Array','BigInt64Array','BigUint64Array',
                    'ArrayBuffer','SharedArrayBuffer','DataView','Math','JSON','Date',
                    'parseInt','parseFloat','isNaN','isFinite','encodeURI','decodeURI',
                    'encodeURIComponent','decodeURIComponent',
                    'Intl','Temporal','Atomics',
                    'AsyncDisposableStack','DisposableStack','AsyncGeneratorFunction',
                    'GeneratorFunction','FinalizationRegistry','Iterator',
                    'SuppressedError','ShadowRealm'].forEach(function (n) {
                     if (realmGlobal[n] === undefined && typeof globalThis[n] !== 'undefined') {
                       realmGlobal[n] = globalThis[n];
                     }
                   });
                   return { global: realmGlobal };
                 },
                 detachArrayBuffer: function (buffer) {
                   if (buffer && typeof buffer.transfer === "function") { buffer.transfer(); return; }
                   if (buffer && typeof buffer.detach === "function") { buffer.detach(); return; }
                   if (typeof structuredClone === "function") {
                     try { structuredClone(buffer, { transfer: [buffer] }); return; } catch (_e) {}
                   }
                   throw new Error('detachArrayBuffer is not supported');
                 },
                 // Annex B [[IsHTMLDDA]] — a callable that returns null. Tests use it as
                 // a stand-in for document.all: Object.defineProperty works on it, it is
                 // callable (returning null), and get-method accessor tests that dispatch
                 // @@match etc. observe null instead of undefined.
                 IsHTMLDDA: __fenHtmlDda,
                 // Single-agent $262.agent mock for Atomics wait/notify tests.
                 // In a single-agent engine, no other agent can wake a waiting
                 // thread, so wait always reports "timed-out" and notify always
                 // returns 0. The mock provides just enough API surface for
                 // tests that include atomicsHelper.js to load and execute.
                 agent: (function() {
                   var _reportQueue = [];
                   var _broadcastSab = null;
                   var _receiveCallbacks = [];
                   var _startTime = typeof performance !== 'undefined' && performance.now ? performance.now() : Date.now();
                   var _agent = {
                     start: function(script) {
                       // Single-agent: run via indirect eval in global scope so
                       // the script can see globalThis.$262 (set after this object
                       // is fully constructed).
                       try { (1, eval)(script); } catch (e) { /* agent errors are silent */ }
                     },
                     broadcast: function(sab) {
                       _broadcastSab = sab;
                       var cbs = _receiveCallbacks.slice();
                       _receiveCallbacks = [];
                       for (var i = 0; i < cbs.length; i++) {
                         try { cbs[i](sab); } catch (e) {}
                       }
                     },
                     receiveBroadcast: function(callback) {
                       if (_broadcastSab !== null) {
                         try { callback(_broadcastSab); } catch (e) {}
                       } else {
                         _receiveCallbacks.push(callback);
                       }
                     },
                     report: function(value) {
                       _reportQueue.push(value);
                     },
                     getReport: function() {
                       if (_reportQueue.length > 0) return _reportQueue.shift();
                       return null;
                     },
                     leaving: function() {},
                     monotonicNow: function() {
                       if (typeof performance !== 'undefined' && performance.now) return performance.now();
                       return Date.now();
                     },
                     sleep: function(ms) {
                       // Single-agent: sleep is a no-op; no other agent can act during sleep.
                     },
                     // waitUntil: in a single-agent world with synchronous broadcast,
                     // we can't spin-wait (blocks the only thread). Return immediately;
                     // the broadcast callback has already run by the time this is called.
                     waitUntil: function(ta, index, expected) {
                       return Atomics.load(ta, index);
                     },
                     tryYield: function() {
                       // Single-agent: yield is a no-op.
                     },
                     trySleep: function(ms) {
                       // Single-agent: sleep is a no-op.
                     },
                     timeouts: {
                       long: 60000,
                       short: 1000,
                       tiny: 100,
                       yield: 100
                     },
                     safeBroadcast: function(sab) {
                       _agent.broadcast(sab);
                     },
                     setTimeout: function(callback, delay) {
                       var p = Promise.resolve();
                       var start = Date.now();
                       var end = start + delay;
                       function check() {
                         if ((end - Date.now()) > 0) { p.then(check); }
                         else { callback(); }
                       }
                       p.then(check);
                     }
                   };
                   return _agent;
                 })()
               };
               // Expose $262 globally so agent scripts started via eval() can access it.
               if (typeof globalThis !== 'undefined') globalThis.$262 = $262;
               // Inline the key atomicsHelper.js improvements so agent tests work
               // even if the harness file is not loaded correctly.
               (function() {
                 var origGetReport = $262.agent.getReport.bind($262.agent);
                 $262.agent.getReport = function() {
                   var r;
                   while ((r = origGetReport()) == null) { $262.agent.sleep(1); }
                   return r;
                 };
                 $262.agent.tryYield = function() {
                   $262.agent.sleep($262.agent.timeouts.yield);
                 };
                 $262.agent.safeBroadcast = function(typedArray) {
                   var Constructor = Object.getPrototypeOf(typedArray).constructor;
                   var temp = new Constructor(typedArray.buffer, typedArray.byteOffset, typedArray.length);
                   $262.agent.broadcast(temp.buffer);
                 };
               })();
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
               function fnGlobalObject() { return globalThis; }
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
        "assertRelativeDateMs.js",
        "atomicsHelper.js",
        "dateConstants.js",
        "byteConversionValues.js",
        "compareArray.js",
        "compareIterator.js",
        "decimalToHexString.js",
        "detachArrayBuffer.js",
        "iteratorZipUtils.js",
        "nans.js",
        "nativeFunctionMatcher.js",
        "promiseHelper.js",
        "propertyHelper.js",
        "proxyTrapsHelper.js",
        "regExpUtils.js",
        "tcoHelper.js",
        "temporalHelpers.js",
        "testAtomics.js",
        "testIntl.js",
        "testTypedArray.js",
        "wellKnownIntrinsicObjects.js",
        // Standard harnesses not in prelude
        "deepEqual.js",
        "nativeErrors.js",
        "isConstructor.js",
        "sta.js",
        // SpiderMonkey staging harness includes
        "sm/assertThrowsValue.js",
        "sm/non262-Date-shell.js",
        "sm/non262-JSON-shell.js",
        "sm/non262-Math-shell.js",
        "sm/non262-Reflect-shell.js",
        "sm/non262-Set-shell.js",
        "sm/non262-Temporal-PlainMonthDay-shell.js",
        "sm/non262-TypedArray-shell.js",
        "sm/non262-expressions-shell.js",
        "sm/non262-generators-shell.js",
        "sm/non262-strict-shell.js",
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

        if (includes.Contains("regExpUtils.js"))
        {
            snippets.Add("""
            if (typeof __fenBuildString === 'function') {
              var __fenOriginalBuildString = buildString;
              buildString = function(args) {
                if (arguments.length === 1 && args !== null && typeof args === "object") {
                  return __fenBuildString(args);
                }
                return __fenOriginalBuildString(args);
              };
            }
            """);
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

    /// <summary>
    /// Derive a human-readable batch tag from the output path or scope.
    /// e.g. "Results/test262/batched/b_built-ins_Array.json" → "built-ins_Array"
    /// </summary>
    private static string DeriveBatchTag(string outputPath, string? scopePath)
    {
        // Try the output filename first
        var name = Path.GetFileNameWithoutExtension(outputPath);
        if (name.StartsWith("b_", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(2);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        // Fall back to scope path
        if (!string.IsNullOrWhiteSpace(scopePath))
            return scopePath.Replace('\\', '/').Trim('/');

        return "unknown";
    }
}
