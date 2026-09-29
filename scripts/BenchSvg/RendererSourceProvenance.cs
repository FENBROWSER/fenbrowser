using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

internal sealed record RendererSourceProvenance(
    string SourceHash,
    IReadOnlyList<string> SourceRoots,
    IReadOnlyList<string> SourceFiles,
    string GitRevision,
    bool? GitTreeDirty,
    bool? SourcesMatchGitRevision)
{
    private const int ShortRevisionLength = 10;

    public string ShortRevision =>
        GitRevision.Length > ShortRevisionLength ? GitRevision[..ShortRevisionLength] : GitRevision;

    public static RendererSourceProvenance Unavailable { get; } = new(
        string.Empty, Array.Empty<string>(), Array.Empty<string>(), string.Empty, null, null);
}

internal static class RendererSourceProvenanceReader
{
    private const string FingerprintFormat = "fen-svg-source-fingerprint/1";
    private const int GitTimeoutMs = 10_000;
    private static readonly string[] SourceRoots =
    {
        "FenBrowser.FenEngine/Svg",
        "FenBrowser.FenEngine/Adapters",
        "scripts/BenchSvg"
    };
    private static readonly string[] RepositoryMarkers =
    {
        "FenBrowser.sln", "FenBrowser.FenEngine", "scripts"
    };
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static RendererSourceProvenance Capture()
    {
        try
        {
            string? root = FindRepositoryRoot();
            if (root == null) return RendererSourceProvenance.Unavailable;
            IReadOnlyList<string> files = EnumerateSourceFiles(root);
            if (files.Count == 0) return RendererSourceProvenance.Unavailable;
            string revision = ReadGitRevision(root);
            bool gitAvailable = revision.Length != 0;
            return new RendererSourceProvenance(
                ComputeHash(root, files),
                SourceRoots,
                files,
                revision,
                gitAvailable ? ReadGitHasChanges(root, untrackedFiles: "normal") : null,
                gitAvailable ? ReadGitHasChanges(root, SourceRoots) is false : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   CryptographicException or ArgumentException or NotSupportedException)
        {
            return RendererSourceProvenance.Unavailable;
        }
    }

    private static string ComputeHash(string root, IReadOnlyList<string> relativePaths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, FingerprintFormat);
        AppendField(hash, string.Join("\n", SourceRoots));
        foreach (string relative in relativePaths)
        {
            AppendField(hash, relative);
            AppendField(hash, NormaliseContent(File.ReadAllBytes(ToFullPath(root, relative))));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendField(IncrementalHash hash, string value) =>
        AppendField(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendField(IncrementalHash hash, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static byte[] NormaliseContent(byte[] content)
    {
        using var reader = new StreamReader(
            new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        if (text.IndexOf('\r') >= 0) text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return Utf8WithoutBom.GetBytes(text);
    }

    private static IReadOnlyList<string> EnumerateSourceFiles(string root)
    {
        var files = new List<string>();
        foreach (string relativeRoot in SourceRoots)
        {
            string directory = ToFullPath(root, relativeRoot);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (!IsBuildOutput(relative)) files.Add(relative);
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split('/').SkipLast(1).Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static string? FindRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrEmpty(start)) continue;
            var current = new DirectoryInfo(start);
            while (current != null)
            {
                if (RepositoryMarkers.All(marker => Exists(Path.Combine(current.FullName, marker))))
                    return current.FullName;
                current = current.Parent;
            }
        }
        return null;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string ReadGitRevision(string workingDirectory)
    {
        string? output = RunGit(workingDirectory, ["rev-parse", "--verify", "HEAD"]);
        if (output == null) return string.Empty;
        string[] tokens = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 1 && tokens[0].All(Uri.IsHexDigit) ? tokens[0] : string.Empty;
    }

    private static bool? ReadGitHasChanges(
        string workingDirectory,
        IReadOnlyList<string>? pathspecs = null,
        string untrackedFiles = "all")
    {
        var arguments = new List<string> { "status", "--porcelain=v1", "-z", "-u" + untrackedFiles };
        if (pathspecs != null)
        {
            arguments.Add("--");
            arguments.AddRange(pathspecs);
        }
        string? status = RunGit(workingDirectory, arguments);
        return status == null ? null : status.Length != 0;
    }

    private static string? RunGit(string workingDirectory, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("--no-optional-locks");
            foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (!Task.WaitAll([standardOutput, standardError], GitTimeoutMs) || process.ExitCode != 0)
                return null;
            return standardOutput.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
                                   InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static string ToFullPath(string root, string relativePath) =>
        Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
