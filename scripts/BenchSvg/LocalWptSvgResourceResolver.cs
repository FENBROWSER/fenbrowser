using FenBrowser.FenEngine.Adapters;

/// <summary>
/// Maps a synthetic HTTPS origin to resources in the selected local WPT checkout.
/// It never performs network I/O and refuses traversal through reparse points.
/// The renderer independently enforces per-render byte/count/depth budgets.
/// </summary>
internal sealed class LocalWptSvgResourceResolver : ISvgResourceResolver
{
    private const long HardMaxResourceBytes = 32L * 1024 * 1024;
    private static readonly Uri WptOrigin = new("https://wpt.local/");
    private readonly string _root;
    private readonly string _rootWithSeparator;

    public LocalWptSvgResourceResolver(string rootDirectory)
    {
        string root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("WPT resource root does not exist");
        if (IsReparsePoint(root))
            throw new IOException("WPT resource root cannot be a reparse point");
        _root = Path.TrimEndingDirectorySeparator(root);
        _rootWithSeparator = _root + Path.DirectorySeparatorChar;
    }

    public Uri CreateDocumentUri(string documentPath)
    {
        string fullPath = Path.GetFullPath(documentPath);
        if (!fullPath.StartsWith(_rootWithSeparator, PathComparison) || ContainsReparsePoint(fullPath))
            throw new IOException("WPT document escaped the authorized root");
        string relative = Path.GetRelativePath(_root, fullPath);
        string escaped = string.Join('/', relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri(WptOrigin, escaped);
    }

    public bool TryResolve(
        Uri absoluteUri,
        SvgResourceKind kind,
        out SvgResolvedResource resource,
        out string error)
    {
        resource = default;
        error = string.Empty;
        if (kind is not (SvgResourceKind.Image or SvgResourceKind.SvgDocument))
        {
            error = "local WPT resolver does not authorize this resource type";
            return false;
        }
        if (absoluteUri == null || !absoluteUri.IsAbsoluteUri ||
            !absoluteUri.Scheme.Equals(WptOrigin.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !absoluteUri.Host.Equals(WptOrigin.Host, StringComparison.OrdinalIgnoreCase) ||
            absoluteUri.Port != WptOrigin.Port || !string.IsNullOrEmpty(absoluteUri.UserInfo) ||
            !string.IsNullOrEmpty(absoluteUri.Query) || !string.IsNullOrEmpty(absoluteUri.Fragment))
        {
            error = "local WPT resolver accepts only its synthetic same-origin URLs";
            return false;
        }

        string path;
        try
        {
            string decodedPath = Uri.UnescapeDataString(absoluteUri.AbsolutePath).TrimStart('/');
            if (decodedPath.IndexOf('\\') >= 0 || decodedPath.IndexOf('\0') >= 0)
                throw new ArgumentException("WPT URL path contains a forbidden character");
            path = Path.GetFullPath(Path.Combine(
                _root, decodedPath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or
                                   NotSupportedException or PathTooLongException)
        {
            error = "local WPT resource path is invalid";
            return false;
        }
        if (!path.StartsWith(_rootWithSeparator, PathComparison) ||
            ContainsReparsePoint(path))
        {
            error = "local WPT resource escaped the authorized root";
            return false;
        }

        if (!HasExactUrlPathCase(path) || !File.Exists(path))
        {
            error = "local WPT resource is not present under the authorized root [resolver-miss]";
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > HardMaxResourceBytes)
            {
                error = "local WPT resource is empty or exceeds the hard byte limit";
                return false;
            }
            byte[] content = File.ReadAllBytes(path);
            resource = new SvgResolvedResource(
                absoluteUri,
                GetContentType(path),
                content);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "local WPT resource could not be read";
            return false;
        }
    }

    private bool ContainsReparsePoint(string path)
    {
        string relative = Path.GetRelativePath(_root, path);
        string current = _root;
        foreach (string part in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (IsReparsePoint(current)) return true;
        }
        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }

    private bool HasExactUrlPathCase(string path)
    {
        if (!OperatingSystem.IsWindows()) return true;
        string relative = Path.GetRelativePath(_root, path);
        string current = _root;
        try
        {
            foreach (string part in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string? exact = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(entry => Path.GetFileName(entry).Equals(
                        part, StringComparison.Ordinal));
                if (exact == null) return false;
                current = exact;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
