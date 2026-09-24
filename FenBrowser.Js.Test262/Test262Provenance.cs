using System.Diagnostics;

namespace FenBrowser.Js.Test262;

/// <summary>
/// Which engine revision produced a result: the FenBrowser commit the runner was
/// built from, with "-dirty" when the working tree had uncommitted changes, so a
/// report can refuse to mix results from different revisions.
/// </summary>
internal static class Test262Provenance
{
    private static readonly Lazy<string> CommitValue = new(ResolveCommit);

    public static string Commit => CommitValue.Value;

    private static string ResolveCommit()
    {
        var repo = FindRepositoryRoot(AppContext.BaseDirectory) ?? FindRepositoryRoot(Environment.CurrentDirectory);
        if (repo is null)
        {
            return "unknown";
        }

        var head = RunGit(repo, "rev-parse HEAD");
        if (string.IsNullOrWhiteSpace(head))
        {
            return "unknown";
        }

        var status = RunGit(repo, "status --porcelain --untracked-files=no");
        return head.Trim() + (string.IsNullOrWhiteSpace(status) ? string.Empty : "-dirty");
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static string? RunGit(string repo, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", $"-C \"{repo}\" {arguments}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(10000) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
