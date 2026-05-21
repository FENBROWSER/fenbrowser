using System.Diagnostics;
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

    private sealed record ShellResult(int ExitCode, string StandardOutput, string StandardError);
}
