using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ShellSmokeTests
{
    [Fact]
    public async Task EvalAcceptsEmptySource()
    {
        var result = await RunShellAsync("--eval", string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("undefined", result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError.Trim());
    }

    [Fact]
    public async Task FileExecutesScriptThroughShell()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fenjs-shell-{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(path, "function add(a,b){ return a+b; } add(2,3);");

        try
        {
            var result = await RunShellAsync("--file", path);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("5", result.StandardOutput.Trim());
            Assert.Equal(string.Empty, result.StandardError.Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Test262FileRunsSingleFixtureThroughShell()
    {
        var fixture = await CreateTest262FixtureAsync();
        var output = Path.Combine(fixture.Workspace, "result-file.json");

        try
        {
            var result = await RunShellAsync("--test262-file", fixture.TestFile, "--output", output, "--max", "1", "--timeout-ms", "1000");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Runtime subset result written:", result.StandardOutput, StringComparison.Ordinal);
            AssertTest262Result(output);
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    [Fact]
    public async Task Test262DirectoryRunsSubsetThroughShell()
    {
        var fixture = await CreateTest262FixtureAsync();
        var output = Path.Combine(fixture.Workspace, "result-directory.json");

        try
        {
            var result = await RunShellAsync("--test262", fixture.TestDirectory, "--output", output, "--max", "1", "--timeout-ms", "1000");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Runtime subset result written:", result.StandardOutput, StringComparison.Ordinal);
            AssertTest262Result(output);
        }
        finally
        {
            Directory.Delete(fixture.Workspace, recursive: true);
        }
    }

    private static async Task<ShellResult> RunShellAsync(params string[] arguments)
    {
        var shellPath = Path.Combine(AppContext.BaseDirectory, "FenBrowser.Js.Shell.dll");
        Assert.True(File.Exists(shellPath), $"FenJS shell assembly not found at {shellPath}");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        process.StartInfo.ArgumentList.Add(shellPath);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ShellResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<Test262Fixture> CreateTest262FixtureAsync()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"fenjs-test262-shell-{Guid.NewGuid():N}");
        var root = Path.Combine(workspace, "test262");
        var testDirectory = Path.Combine(root, "test", "language", "expressions", "addition");
        Directory.CreateDirectory(testDirectory);
        Directory.CreateDirectory(Path.Combine(root, "harness"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "test262.pin"), "fixture-pin");

        var testFile = Path.Combine(testDirectory, "shell-fixture.js");
        await File.WriteAllTextAsync(
            testFile,
            """
            /*---
            description: FenJS shell test262 fixture
            ---*/
            1 + 1;
            """);

        return new Test262Fixture(workspace, testDirectory, testFile);
    }

    private static void AssertTest262Result(string output)
    {
        Assert.True(File.Exists(output), $"Expected test262 result at {output}");
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var root = document.RootElement;
        Assert.Equal("runtime-subset", root.GetProperty("mode").GetString());
        Assert.Equal("fixture-pin", root.GetProperty("test262Commit").GetString());
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("crashed").GetInt32());
        Assert.Equal(0, root.GetProperty("timedOut").GetInt32());
    }

    private sealed record ShellResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record Test262Fixture(string Workspace, string TestDirectory, string TestFile);
}
