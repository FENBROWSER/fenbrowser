namespace FenBrowser.Js.Test262;

public sealed class Test262Manifest
{
    public required string RootPath { get; init; }

    public IEnumerable<string> EnumerateTestFiles()
    {
        var testRoot = Path.Combine(RootPath, "test");
        if (!Directory.Exists(testRoot))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(testRoot, "*.js", SearchOption.AllDirectories))
        {
            yield return path;
        }
    }
}
