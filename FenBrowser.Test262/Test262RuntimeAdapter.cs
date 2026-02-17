using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;
using Test262Harness;
using EngineValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.Test262.Generated;

public static partial class State
{
    public static readonly Dictionary<string, string> HarnessSourceByName =
        new(StringComparer.OrdinalIgnoreCase);

    static State()
    {
        Test262StreamLoader = () =>
        {
            var suiteDirectory = ResolveSuiteDirectory();
            var harnessDirectory = NormalizeSuitePathForHarness(suiteDirectory);
            return Task.FromResult(Test262Stream.FromDirectory(harnessDirectory));
        };
    }

    private static string ResolveSuiteDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("FEN_TEST262_DIR");
        if (IsValidSuiteRoot(fromEnv))
        {
            return Path.GetFullPath(fromEnv!);
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "test262");
            if (IsValidSuiteRoot(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the test262 suite. Set FEN_TEST262_DIR or place test262 at repo root.");
    }

    private static bool IsValidSuiteRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var assertPath = Path.Combine(path, "harness", "assert.js");
        return File.Exists(assertPath);
    }

    private static string NormalizeSuitePathForHarness(string suiteDirectory)
    {
        var fullPath = Path.GetFullPath(suiteDirectory);
        if (fullPath.Length >= 2 && fullPath[1] == ':')
        {
            var driveLetter = char.ToLowerInvariant(fullPath[0]);
            var remainder = fullPath.Substring(2).Replace('\\', '/');
            return $"/mnt/{driveLetter}{remainder}";
        }

        return fullPath.Replace('\\', '/');
    }
}

public partial class TestHarness
{
    private static partial Task InitializeCustomState()
    {
        State.HarnessSourceByName.Clear();

        foreach (var harnessFile in State.HarnessFiles)
        {
            var normalized = harnessFile.FileName.Replace('\\', '/');
            var fileName = Path.GetFileName(normalized);

            State.HarnessSourceByName[fileName] = harnessFile.Program;
            State.HarnessSourceByName[normalized] = harnessFile.Program;

            const string harnessPrefix = "harness/";
            if (normalized.StartsWith(harnessPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized.Substring(harnessPrefix.Length);
                State.HarnessSourceByName[relative] = harnessFile.Program;
            }
        }

        return Task.CompletedTask;
    }
}

public abstract partial class Test262Test
{
    private static readonly string[] DefaultHarnessIncludes = ["assert.js", "sta.js"];

    private FenRuntime BuildTestExecutor(Test262File file)
    {
        var runtime = new FenRuntime();
        SetupHostDefinedFunctions(runtime);

        if (HasFlag(file, "raw"))
        {
            return runtime;
        }

        var loadedIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var include in DefaultHarnessIncludes)
        {
            ExecuteHarnessInclude(runtime, file, include, loadedIncludes);
        }

        foreach (var include in file.Includes)
        {
            ExecuteHarnessInclude(runtime, file, include, loadedIncludes);
        }

        return runtime;
    }

    private void ExecuteTest(FenRuntime runtime, Test262File file)
    {
        if (file.Type == ProgramType.Module)
        {
            if (runtime.Context?.ModuleLoader == null)
            {
                throw new InvalidOperationException("Module loader is not available.");
            }

            runtime.Context.ModuleLoader.LoadModuleSrc(file.Program, file.FileName);
            return;
        }

        FenBrowser.FenEngine.Core.Lexer.DebugMode = true;
        var result = runtime.ExecuteSimple(file.Program, file.FileName);
        EnsureExecutionSucceeded(file.FileName, result);
    }

    private partial bool ShouldThrow(Test262File testCase, bool strict)
    {
        _ = strict;
        return testCase.Negative;
    }

    private static void ExecuteHarnessInclude(
        FenRuntime runtime,
        Test262File testFile,
        string include,
        ISet<string> loadedIncludes)
    {
        if (!loadedIncludes.Add(include))
        {
            return;
        }

        if (!TryGetHarnessScript(include, out var script))
        {
            throw new FileNotFoundException(
                $"Harness include '{include}' was not found for test '{testFile.FileName}'.");
        }

        // Check if we are running assert.js to enable debug
        bool enableDebug = true; // include.Contains("assert.js");
        if (enableDebug) FenBrowser.FenEngine.Core.Lexer.DebugMode = true;

        var result = runtime.ExecuteSimple(script, $"harness/{include}");

        // if (enableDebug) FenBrowser.FenEngine.Core.Lexer.DebugMode = false;
        if (result != null && (result.Type == EngineValueType.Error || result.Type == EngineValueType.Throw))
        {
             Console.WriteLine($"[DEBUG-FAIL] Script: harness/{include}");
             Console.WriteLine($"[DEBUG-FAIL] Content Start: {script.Substring(0, Math.Min(100, script.Length))}");
             Console.WriteLine($"[DEBUG-FAIL] Content End: {script.Substring(Math.Max(0, script.Length - 100))}");
             // Find where += is used
             int idx = script.IndexOf("+=");
             if (idx >= 0)
             {
                 Console.WriteLine($"[DEBUG-FAIL] First += at index {idx}: '{script.Substring(Math.Max(0, idx - 20), Math.Min(50, script.Length - idx + 20))}'");
             }
             Console.WriteLine($"[DEBUG-FAIL] Full Content Length: {script.Length}");
        }
        EnsureExecutionSucceeded($"harness/{include}", result);
    }

    private static bool TryGetHarnessScript(string include, out string script)
    {
        if (State.HarnessSourceByName.TryGetValue(include, out script!))
        {
            return true;
        }

        var normalized = include.Replace('\\', '/');
        if (State.HarnessSourceByName.TryGetValue(normalized, out script!))
        {
            return true;
        }

        const string harnessPrefix = "harness/";
        if (normalized.StartsWith(harnessPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(harnessPrefix.Length);
            if (State.HarnessSourceByName.TryGetValue(normalized, out script!))
            {
                return true;
            }
        }

        var fileName = Path.GetFileName(normalized);
        return State.HarnessSourceByName.TryGetValue(fileName, out script!);
    }

    private static bool HasFlag(Test262File file, string flag)
    {
        foreach (var candidate in file.Flags)
        {
            if (string.Equals(candidate, flag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureExecutionSucceeded(string sourceName, IValue? result)
    {
        if (result == null)
        {
            return;
        }

        if (result.Type == EngineValueType.Error)
        {
            if (result is FenValue errorValue)
            {
                throw new InvalidOperationException(errorValue.AsError() ?? errorValue.ToString());
            }

            throw new InvalidOperationException($"{sourceName} failed: {result}");
        }

        if (result.Type == EngineValueType.Throw)
        {
            if (result is FenValue thrownValue)
            {
                throw new InvalidOperationException(DescribeThrownValue(thrownValue.GetThrownValue()));
            }

            throw new InvalidOperationException($"{sourceName} threw: {result}");
        }
    }

    private static string DescribeThrownValue(FenValue thrown)
    {
        if (thrown.IsObject || thrown.IsFunction)
        {
            var obj = thrown.AsObject();
            if (obj != null)
            {
                var nameValue = obj.Get("name");
                var messageValue = obj.Get("message");

                var name = nameValue.IsUndefined ? string.Empty : nameValue.ToString();
                var message = messageValue.IsUndefined ? string.Empty : messageValue.ToString();

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(message))
                {
                    return $"{name}: {message}";
                }

                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }

                if (!string.IsNullOrEmpty(message))
                {
                    return message;
                }
            }
        }

        var text = thrown.ToString();
        return string.IsNullOrEmpty(text) ? "Error" : text;
    }

    private static void SetupHostDefinedFunctions(FenRuntime runtime)
    {
        var print = new FenFunction("print", (args, thisVal) =>
        {
            _ = args;
            _ = thisVal;
            return FenValue.Undefined;
        });
        runtime.GlobalEnv.Set("print", FenValue.FromFunction(print));

        var console = new FenObject();
        console.Set("log", FenValue.FromFunction(new FenFunction("log", (args, thisVal) =>
        {
            _ = args;
            _ = thisVal;
            return FenValue.Undefined;
        })));
        runtime.GlobalEnv.Set("console", FenValue.FromObject(console));

        var d262 = Build262Object(runtime);
        runtime.GlobalEnv.Set("$262", FenValue.FromObject(d262));
    }

    private static FenObject Build262Object(FenRuntime runtime)
    {
        var d262 = new FenObject();

        d262.Set("createRealm", FenValue.FromFunction(new FenFunction("createRealm", (args, thisVal) =>
        {
            _ = args;
            _ = thisVal;

            var nestedRuntime = new FenRuntime();
            SetupHostDefinedFunctions(nestedRuntime);

            return FenValue.FromObject(Build262Object(nestedRuntime));
        })));

        var globalThis = runtime.GlobalEnv.Get("globalThis");
        d262.Set("global", globalThis);

        d262.Set("evalScript", FenValue.FromFunction(new FenFunction("evalScript", (args, thisVal) =>
        {
            _ = thisVal;
            if (args.Length == 0)
            {
                return FenValue.Undefined;
            }

            var script = args[0].ToString();
            var result = runtime.ExecuteSimple(script, "evalScript");
            EnsureExecutionSucceeded("evalScript", result);

            return result is FenValue fenValue ? fenValue : FenValue.Undefined;
        })));

        d262.Set("gc", FenValue.FromFunction(new FenFunction("gc", (args, thisVal) =>
        {
            _ = args;
            _ = thisVal;
            GC.Collect();
            return FenValue.Undefined;
        })));

        d262.Set("detachArrayBuffer", FenValue.FromFunction(new FenFunction("detachArrayBuffer", (args, thisVal) =>
        {
            _ = args;
            _ = thisVal;
            return FenValue.Undefined;
        })));

        return d262;
    }
}
