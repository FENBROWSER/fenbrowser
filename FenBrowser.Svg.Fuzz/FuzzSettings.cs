namespace FenBrowser.Svg.Fuzz;

/// <summary>
/// Run-size knobs. The default keeps the suite fast enough to block every PR;
/// a campaign raises it, for example <c>FEN_SVG_FUZZ_ITERATIONS=2500</c> runs
/// 10 000 cases per target (four seeds per target).
/// </summary>
internal static class FuzzSettings
{
    public const string IterationsVariable = "FEN_SVG_FUZZ_ITERATIONS";
    public const string CorpusVariable = "FEN_SVG_FUZZ_CORPUS";
    public const string ArtifactsVariable = "FEN_SVG_FUZZ_ARTIFACTS";
    public const int DefaultIterations = 48;
    public const int MaxIterations = 1_000_000;

    public static int Iterations
    {
        get
        {
            string? raw = Environment.GetEnvironmentVariable(IterationsVariable);
            return int.TryParse(raw, out int value)
                ? Math.Clamp(value, 1, MaxIterations)
                : DefaultIterations;
        }
    }

    /// <summary>Optional directory of extra .svg seeds, such as a WPT checkout.</summary>
    public static string? CorpusDirectory
    {
        get
        {
            string? raw = Environment.GetEnvironmentVariable(CorpusVariable);
            return !string.IsNullOrWhiteSpace(raw) && Directory.Exists(raw) ? raw : null;
        }
    }

    /// <summary>Where failing inputs are written: Results/fuzz/svg under the repository root.</summary>
    public static string ArtifactDirectory
    {
        get
        {
            string? raw = Environment.GetEnvironmentVariable(ArtifactsVariable);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FenBrowser.sln")))
                {
                    return Path.Combine(dir.FullName, "Results", "fuzz", "svg");
                }
            }

            return Path.Combine(Path.GetTempPath(), "fenbrowser-svg-fuzz");
        }
    }
}
