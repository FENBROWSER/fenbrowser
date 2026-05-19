using System.Text.Json;
using FenBrowser.Js.Test262;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class Test262RunnerTests
{
    [Fact]
    public void Run_DashboardMode_DoesNotLoadExpectations()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var dashboardOut = Path.Combine(tempRoot, "dashboard.json");
        var invalidExpectationsPath = Path.Combine(tempRoot, "missing-expectations.json");

        try
        {
            File.WriteAllText(currentPath, JsonSerializer.Serialize(new
            {
                summary = new
                {
                    total = 1,
                    passed = 1,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new[]
                {
                    new { path = "test/language/foo.js", status = "Passed", category = (string?)null }
                },
                failures = Array.Empty<object>()
            }));

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: false,
                dashboard: true,
                verifyGates: false,
                outputPath: dashboardOut,
                max: 10,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: invalidExpectationsPath,
                inputPath: currentPath,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(dashboardOut));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_VerifyGatesMode_DoesNotLoadExpectations()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var verifyOut = Path.Combine(tempRoot, "verify.json");
        var invalidExpectationsPath = Path.Combine(tempRoot, "missing-expectations.json");

        try
        {
            File.WriteAllText(currentPath, JsonSerializer.Serialize(new
            {
                summary = new
                {
                    total = 1,
                    passed = 1,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new[]
                {
                    new { path = "test/language/foo.js", status = "Passed", category = (string?)null }
                },
                failures = Array.Empty<object>()
            }));

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: true,
                outputPath: verifyOut,
                max: 10,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: invalidExpectationsPath,
                inputPath: currentPath,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(verifyOut));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_ParserSubset_TimeoutCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "timeout.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "parser-timeout.json");

        try
        {
            File.WriteAllText(testFile, "for(;;){}");
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "parser"
              },
              "expectations": [
                {
                  "path": "test/timeout.js",
                  "status": "Timeout",
                  "reason": "known long parse path",
                  "owner": "js",
                  "area": "parser",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            Test262Runner.ParseInvokerForTests = (_source, _isModule) => Task.Delay(100);

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: true,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.ParseInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_TimeoutCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "timeout.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "runtime-timeout.json");

        try
        {
            File.WriteAllText(testFile, "1 + 1;");
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "runtime"
              },
              "expectations": [
                {
                  "path": "test/timeout.js",
                  "status": "Timeout",
                  "reason": "known long runtime path",
                  "owner": "js",
                  "area": "runtime",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) => Task.Delay(100);

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_ParserSubset_TimeoutWithoutExpectationRemainsTimedOut()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "timeout.js");
        var outputPath = Path.Combine(tempRoot, "parser-timeout-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, "for(;;){}");
            Test262Runner.ParseInvokerForTests = (_source, _isModule) => Task.Delay(100);

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: true,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("TimedOut", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.ParseInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_TimeoutWithoutExpectationRemainsTimedOut()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "timeout.js");
        var outputPath = Path.Combine(tempRoot, "runtime-timeout-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) => Task.Delay(100);

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("TimedOut", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_RuntimeErrorCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "runtime-error.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "runtime-error-expected.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "runtime"
              },
              "expectations": [
                {
                  "path": "test/runtime-error.js",
                  "status": "RuntimeError",
                  "reason": "known runtime limitation",
                  "owner": "js",
                  "area": "runtime",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) =>
                throw new InvalidOperationException("forced runtime error");

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_RuntimeErrorWithoutExpectationRemainsFailed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "runtime-error.js");
        var outputPath = Path.Combine(tempRoot, "runtime-error-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) =>
                throw new InvalidOperationException("forced runtime error");

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("Failed", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_ParserSubset_CrashCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "crash.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "parser-crash-expected.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "parser"
              },
              "expectations": [
                {
                  "path": "test/crash.js",
                  "status": "Crash",
                  "reason": "known parser crash",
                  "owner": "js",
                  "area": "parser",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            Test262Runner.ParseInvokerForTests = (_source, _isModule) =>
                throw new Exception("forced parser crash");

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: true,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.ParseInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_ParserSubset_CrashWithoutExpectationRemainsCrashed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "crash.js");
        var outputPath = Path.Combine(tempRoot, "parser-crash-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            Test262Runner.ParseInvokerForTests = (_source, _isModule) =>
                throw new Exception("forced parser crash");

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: true,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("Crashed", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.ParseInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_CrashCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "crash.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "runtime-crash-expected.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "runtime"
              },
              "expectations": [
                {
                  "path": "test/crash.js",
                  "status": "Crash",
                  "reason": "known runtime crash",
                  "owner": "js",
                  "area": "runtime",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) =>
                throw new Exception("forced runtime crash");

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_CrashWithoutExpectationRemainsCrashed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "crash.js");
        var outputPath = Path.Combine(tempRoot, "runtime-crash-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) =>
                throw new Exception("forced runtime crash");

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("Crashed", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_ParserSubset_ParserErrorCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "parser-error.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "parser-error-expected.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "parser"
              },
              "expectations": [
                {
                  "path": "test/parser-error.js",
                  "status": "ParserError",
                  "reason": "known parser limitation",
                  "owner": "js",
                  "area": "parser",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            Test262Runner.ParseInvokerForTests = (_source, _isModule) =>
                throw new JsParserException("forced parser error");

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: true,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.ParseInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_ParserSubset_ParserErrorWithoutExpectationRemainsFailed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "parser-error.js");
        var outputPath = Path.Combine(tempRoot, "parser-error-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            Test262Runner.ParseInvokerForTests = (_source, _isModule) =>
                throw new JsParserException("forced parser error");

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: true,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("Failed", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.ParseInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_ParserSubset_UnsupportedFeatureCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "unsupported.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "parser-unsupported-expected.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "parser"
              },
              "expectations": [
                {
                  "path": "test/unsupported.js",
                  "status": "UnsupportedFeature",
                  "reason": "known unsupported feature",
                  "owner": "js",
                  "area": "parser",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            Test262Runner.ParseInvokerForTests = (_source, _isModule) =>
                throw new UnsupportedFeatureException("feature-x", FeatureSupportLevel.Unsupported, new SourceSpan(0, 1, 1, 1));

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: true,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.ParseInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_ParserSubset_UnsupportedFeatureWithoutExpectationRemainsUnsupportedFeature()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "unsupported.js");
        var outputPath = Path.Combine(tempRoot, "parser-unsupported-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            Test262Runner.ParseInvokerForTests = (_source, _isModule) =>
                throw new UnsupportedFeatureException("feature-x", FeatureSupportLevel.Unsupported, new SourceSpan(0, 1, 1, 1));

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: true,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("UnsupportedFeature", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.ParseInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_UnsupportedFeatureCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "unsupported.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "runtime-unsupported-expected.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "runtime"
              },
              "expectations": [
                {
                  "path": "test/unsupported.js",
                  "status": "UnsupportedFeature",
                  "reason": "known unsupported feature",
                  "owner": "js",
                  "area": "runtime",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) =>
                throw new UnsupportedFeatureException("feature-x", FeatureSupportLevel.Unsupported, new SourceSpan(0, 1, 1, 1));

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_UnsupportedFeatureWithoutExpectationRemainsUnsupportedFeature()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "unsupported.js");
        var outputPath = Path.Combine(tempRoot, "runtime-unsupported-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) =>
                throw new UnsupportedFeatureException("feature-x", FeatureSupportLevel.Unsupported, new SourceSpan(0, 1, 1, 1));

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("UnsupportedFeature", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_ParserSubset_PrecheckUnsupportedFeatureCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "precheck-unsupported.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "parser-precheck-unsupported-expected.json");

        try
        {
            File.WriteAllText(testFile, """
            /*---
            features: [feature-x]
            ---*/
            1+1;
            """);
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "parser"
              },
              "expectations": [
                {
                  "path": "test/precheck-unsupported.js",
                  "status": "UnsupportedFeature",
                  "reason": "known unsupported feature",
                  "owner": "js",
                  "area": "parser",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: true,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: "feature-y");

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_ParserSubset_PrecheckUnsupportedFeatureWithoutExpectationRemainsUnsupportedFeature()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "precheck-unsupported.js");
        var outputPath = Path.Combine(tempRoot, "parser-precheck-unsupported-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, """
            /*---
            features: [feature-x]
            ---*/
            1+1;
            """);

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: true,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: "feature-y");

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("UnsupportedFeature", first.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_PrecheckUnsupportedFeatureCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "precheck-unsupported.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "runtime-precheck-unsupported-expected.json");

        try
        {
            File.WriteAllText(testFile, """
            /*---
            features: [feature-x]
            ---*/
            1+1;
            """);
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "runtime"
              },
              "expectations": [
                {
                  "path": "test/precheck-unsupported.js",
                  "status": "UnsupportedFeature",
                  "reason": "known unsupported feature",
                  "owner": "js",
                  "area": "runtime",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: "feature-y");

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_PrecheckUnsupportedFeatureWithoutExpectationRemainsUnsupportedFeature()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "precheck-unsupported.js");
        var outputPath = Path.Combine(tempRoot, "runtime-precheck-unsupported-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, """
            /*---
            features: [feature-x]
            ---*/
            1+1;
            """);

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: "feature-y");

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("UnsupportedFeature", first.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_ParserErrorCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "runtime-parser-error.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "runtime-parser-error-expected.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "runtime"
              },
              "expectations": [
                {
                  "path": "test/runtime-parser-error.js",
                  "status": "ParserError",
                  "reason": "known runtime parse-phase issue",
                  "owner": "js",
                  "area": "runtime",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) =>
                throw new JsParserException("forced runtime parser error");

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_ParserErrorWithoutExpectationRemainsFailed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "runtime-parser-error.js");
        var outputPath = Path.Combine(tempRoot, "runtime-parser-error-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) =>
                throw new JsParserException("forced runtime parser error");

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("Failed", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_JsThrownExceptionCanBeClassifiedAsExpectedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "runtime-throw.js");
        var expectationsPath = Path.Combine(tempRoot, "expectations.json");
        var outputPath = Path.Combine(tempRoot, "runtime-throw-expected.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            File.WriteAllText(expectationsPath, """
            {
              "metadata": {
                "owner": "js",
                "area": "runtime"
              },
              "expectations": [
                {
                  "path": "test/runtime-throw.js",
                  "status": "RuntimeError",
                  "reason": "known throw path",
                  "owner": "js",
                  "area": "runtime",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) =>
                throw new JsThrownException(JsValue.Undefined);

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: expectationsPath,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("ExpectedFailure", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_RuntimeSubset_JsThrownExceptionWithoutExpectationRemainsFailed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var testDir = Path.Combine(tempRoot, "test");
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "runtime-throw.js");
        var outputPath = Path.Combine(tempRoot, "runtime-throw-no-expectation.json");

        try
        {
            File.WriteAllText(testFile, "1+1;");
            Test262Runner.RuntimeInvokerForTests = (_input, _file, _isModule) =>
                throw new JsThrownException(JsValue.Undefined);

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: true,
                dashboard: false,
                verifyGates: false,
                outputPath: outputPath,
                max: 1,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: null,
                inputPath: null,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("expectedFailures").GetInt32());

            var tests = doc.RootElement.GetProperty("tests");
            var first = tests.EnumerateArray().First();
            Assert.Equal("Failed", first.GetProperty("status").GetString());
        }
        finally
        {
            Test262Runner.RuntimeInvokerForTests = null;
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
