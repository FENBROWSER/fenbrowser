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
            // test262 INTERPRETING.md: files ending in "_FIXTURE.js" are not tests —
            // they exist only to be imported by module tests.
            if (path.EndsWith("_FIXTURE.js", StringComparison.Ordinal))
            {
                continue;
            }

            yield return path;
        }
    }
}
