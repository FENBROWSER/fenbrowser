using System.Text.RegularExpressions;

namespace FenBrowser.Tests.Logging;

/// <summary>
/// Image URLs carry paths, query strings and data: payloads that can hold session
/// tokens, personal data or the resource itself. The image loader logs them only
/// through <see cref="FenBrowser.Core.Logging.LogUrl"/>.
/// </summary>
public sealed partial class ImageLoaderUrlLoggingGuardTests
{
    [Fact]
    public void ImageLoaderLogLines_DoNotInterpolateRawUrls()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "FenBrowser.FenEngine", "Rendering", "ImageLoader.cs"));

        var violations = source.Split('\n')
            .Select((line, index) => (line, number: index + 1))
            .Where(entry => entry.line.Contains("EngineLogCompat.", StringComparison.Ordinal))
            .SelectMany(entry => Interpolation().Matches(entry.line)
                .Select(match => match.Groups["expr"].Value)
                .Where(expr => UrlLike().IsMatch(expr) &&
                               !expr.StartsWith("LogUrl.Describe(", StringComparison.Ordinal))
                .Select(expr => $"line {entry.number}: {{{expr}}}"))
            .ToList();

        Assert.True(violations.Count == 0,
            "Log image URLs through LogUrl.Describe: " + string.Join("; ", violations));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FenBrowser.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [GeneratedRegex(@"\{(?<expr>[^{}]+)\}")]
    private static partial Regex Interpolation();

    [GeneratedRegex(@"\b(url|uri|href|src|normalizedUrl|finalUri|absolute|candidate\.Key)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();
}
