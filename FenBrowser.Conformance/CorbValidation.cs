using System.Text;
using System.Text.Json;
using FenBrowser.Core.Security.Corb;

namespace FenBrowser.Conformance;

internal static class CorbValidation
{
    private sealed record CorbCase(
        string Name,
        string RequestMode,
        string RequestOrigin,
        string ResponseUrl,
        string ContentType,
        string ContentTypeOptions,
        byte[] Prefix,
        CorbVerdict ExpectedVerdict);

    public static int Run(string repoRoot, string? outputPath)
    {
        var filter = new CorbFilter();
        var cases = new[]
        {
            new CorbCase(
                "cross-origin-html-nosniff",
                "no-cors",
                "https://app.example.test",
                "https://cdn.other.test/page",
                "text/html",
                "nosniff",
                Encoding.UTF8.GetBytes("<!doctype html><html><body>x"),
                CorbVerdict.Block),
            new CorbCase(
                "cross-origin-json-xssi",
                "no-cors",
                "https://app.example.test",
                "https://api.other.test/data",
                "text/plain",
                "",
                Encoding.UTF8.GetBytes(")]}'\n{\"ok\":true}"),
                CorbVerdict.Block),
            new CorbCase(
                "cross-origin-svg",
                "no-cors",
                "https://app.example.test",
                "https://img.other.test/logo.svg",
                "image/svg+xml",
                "",
                Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"),
                CorbVerdict.Block),
            new CorbCase(
                "cross-origin-jpeg",
                "no-cors",
                "https://app.example.test",
                "https://img.other.test/photo.jpg",
                "image/jpeg",
                "",
                new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 },
                CorbVerdict.Allow),
            new CorbCase(
                "same-origin-json",
                "no-cors",
                "https://app.example.test",
                "https://app.example.test/data",
                "application/json",
                "",
                Encoding.UTF8.GetBytes("{\"ok\":true}"),
                CorbVerdict.Allow)
        };

        var results = new List<object>();
        var passed = true;
        foreach (var testCase in cases)
        {
            var result = filter.Evaluate(
                testCase.RequestMode,
                testCase.RequestOrigin,
                testCase.ResponseUrl,
                testCase.ContentType,
                testCase.ContentTypeOptions,
                testCase.Prefix);

            var casePassed = result.Verdict == testCase.ExpectedVerdict;
            passed &= casePassed;
            results.Add(new
            {
                testCase.Name,
                expectedVerdict = testCase.ExpectedVerdict.ToString(),
                actualVerdict = result.Verdict.ToString(),
                result.Reason,
                passed = casePassed
            });

            Console.WriteLine($"[CORB] {testCase.Name}: expected={testCase.ExpectedVerdict} actual={result.Verdict} pass={casePassed}");
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
                JsonSerializer.Serialize(results, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        }

        return passed ? 0 : 2;
    }
}
