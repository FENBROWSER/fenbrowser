using System.Text.Json;
using FenBrowser.Core.Accessibility;
using FenBrowser.Core.Parsing;

namespace FenBrowser.Conformance;

internal static class AccessibilityValidation
{
    private const string DefaultFixtureHtml = """
<!doctype html>
<html lang="en">
<body>
  <header>
    <nav aria-label="Primary">
      <a href="https://example.test/home">Home</a>
      <a href="https://example.test/docs">Docs</a>
    </nav>
  </header>
  <main>
    <h1>FenBrowser Accessibility Fixture</h1>
    <form aria-label="Search form">
      <label for="q">Search</label>
      <input id="q" type="text" value="query text" />
      <button type="submit">Submit</button>
    </form>
    <section aria-label="Status">
      <div role="status" aria-live="polite">Ready</div>
      <div role="alert">Danger</div>
    </section>
    <ul>
      <li>One</li>
      <li>Two</li>
    </ul>
    <img src="https://example.test/logo.png" alt="Fen logo" />
  </main>
</body>
</html>
""";

    public static int Run(string repoRoot, string? outputPath)
    {
        var document = new HtmlParser(DefaultFixtureHtml).Parse();
        var tree = AccessibilityTree.For(document);

        var snapshots = new[]
        {
            tree.ExportPlatformSnapshot(AccessibilityTargetPlatform.WindowsUia),
            tree.ExportPlatformSnapshot(AccessibilityTargetPlatform.LinuxAtSpi),
            tree.ExportPlatformSnapshot(AccessibilityTargetPlatform.MacOsNsAccessibility)
        };

        foreach (var snapshot in snapshots)
        {
            Console.WriteLine($"[A11Y] {snapshot.Platform}: nodes={snapshot.Nodes.Count} valid={snapshot.IsValid}");
            foreach (var error in snapshot.ValidationErrors)
            {
                Console.WriteLine($"  error: {error}");
            }
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var resolvedPath = Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.Combine(repoRoot, outputPath);
            var dir = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(
                resolvedPath,
                JsonSerializer.Serialize(snapshots, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        }

        return snapshots.All(s => s.IsValid) ? 0 : 2;
    }
}
