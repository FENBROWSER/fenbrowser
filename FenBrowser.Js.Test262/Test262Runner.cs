namespace FenBrowser.Js.Test262;

public sealed class Test262Runner
{
    public int Run(string rootPath, bool list, bool dryRun, string outputPath)
    {
        var manifest = new Test262Manifest { RootPath = rootPath };
        var files = manifest.EnumerateTestFiles().OrderBy(p => p, StringComparer.Ordinal).ToList();

        if (list)
        {
            foreach (var file in files.Take(200))
            {
                Console.WriteLine(file);
            }

            Console.WriteLine($"Total: {files.Count}");
        }

        if (dryRun)
        {
            var pinPath = Path.Combine(rootPath, "..", "test262.pin");
            var commit = File.Exists(pinPath) ? File.ReadAllText(pinPath).Trim() : "un-pinned";
            Test262ResultWriter.WriteDryRun(outputPath, commit, files);
            Console.WriteLine($"Dry-run result written: {outputPath}");
        }

        return 0;
    }
}
