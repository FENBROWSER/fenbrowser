using System.Text;
using Xunit.Sdk;

namespace FenBrowser.Svg.Fuzz;

/// <summary>
/// Runs one fuzz case and turns any property violation into a reproducible
/// failure: the input is saved under the artifact directory and the message
/// names the target, seed and iteration that produced it.
/// </summary>
internal static class FuzzCase
{
    private const int MaxInlineInputChars = 512;

    public static void Run(string target, int seed, int iteration, string input, Action<string> body)
    {
        try
        {
            body(input);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string artifact = SaveArtifact(target, seed, iteration, input);
            string preview = input.Length <= MaxInlineInputChars
                ? input
                : input[..MaxInlineInputChars] + "...";
            throw new XunitException(
                $"{target} seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}" +
                Environment.NewLine + $"input saved to {artifact}" +
                Environment.NewLine + $"input preview: {preview}",
                ex);
        }
    }

    public static void Check(bool condition, string property)
    {
        if (!condition)
        {
            throw new FuzzPropertyViolation(property);
        }
    }

    private static string SaveArtifact(string target, int seed, int iteration, string input)
    {
        try
        {
            string directory = FuzzSettings.ArtifactDirectory;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{target}-{seed}-{iteration}.svg");
            File.WriteAllText(path, input, new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "(not saved: " + ex.Message + ")";
        }
    }
}

internal sealed class FuzzPropertyViolation(string property) : Exception(property);
