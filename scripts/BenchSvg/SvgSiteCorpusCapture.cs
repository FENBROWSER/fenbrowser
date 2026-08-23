using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class SvgSiteCorpusCapture
{
    private const int MaxSites = 32;
    private const int MaxSvgPerSite = 512;
    private const int MaxHtmlBytes = 2 * 1024 * 1024;
    private const int MaxSvgBytes = 2 * 1024 * 1024;
    private const int MaxTotalSvgBytes = 32 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex InlineSvg = new(
        @"<svg\b[^>]*>.*?</svg\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex SvgReference = new(
        """(?:src|href)\s*=\s*['"](?<url>[^'"]+\.svg(?:\?[^'"]*)?)['"]|url\(\s*['"]?(?<css>[^)'"]+\.svg(?:\?[^)'"]*)?)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public static async Task<int> RunAsync(string configPath, string outputDirectory)
    {
        configPath = Path.GetFullPath(configPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (!File.Exists(configPath)) throw new ArgumentException($"capture config does not exist: {configPath}");
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new ArgumentException("capture output directory must not already contain files");

        var config = JsonSerializer.Deserialize<CaptureConfig>(await File.ReadAllTextAsync(configPath)) ??
                     throw new ArgumentException("capture config is empty");
        if (config.SchemaVersion != 1 || config.Sites.Count == 0 || config.Sites.Count > MaxSites)
            throw new ArgumentException("capture config schema or site count is invalid");
        if (config.Sites.Select(site => site.Id).Distinct(StringComparer.Ordinal).Count() != config.Sites.Count)
            throw new ArgumentException("capture config contains duplicate site ids");

        Directory.CreateDirectory(outputDirectory);
        string corpusDirectory = Path.Combine(outputDirectory, "corpus");
        Directory.CreateDirectory(corpusDirectory);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            ConnectCallback = ConnectPublicEndpointAsync
        };
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FenBrowser-SvgCorpusCapture/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/svg+xml,image/svg+xml;q=0.9,*/*;q=0.1");

        var manifest = new CaptureManifest
        {
            SchemaVersion = 1,
            CorpusKind = "captured-site",
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
        int totalBytes = 0;
        bool allSitesFetched = true;
        foreach (var site in config.Sites)
        {
            ValidateSite(site);
            var record = new CaptureSiteRecord { Id = site.Id, SourceUrl = site.Url };
            manifest.Sites.Add(record);
            try
            {
                var pageUri = new Uri(site.Url, UriKind.Absolute);
                var page = await FetchAsync(client, pageUri, MaxHtmlBytes, "text/html", "application/xhtml+xml");
                record.FinalUrl = page.FinalUri.AbsoluteUri;
                record.HttpStatus = (int)page.Status;
                record.Status = "captured";

                var inlineMatches = InlineSvg.Matches(page.Text);
                record.CandidateCount = inlineMatches.Count;
                var candidates = new List<(string Svg, string Source)>(
                    Math.Min(inlineMatches.Count, MaxSvgPerSite));
                foreach (Match match in inlineMatches)
                {
                    if (candidates.Count >= MaxSvgPerSite)
                    {
                        record.SelectionTruncated = true;
                        break;
                    }
                    candidates.Add((match.Value, page.FinalUri.AbsoluteUri + "#inline-svg-" + candidates.Count));
                }

                var referenced = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match match in SvgReference.Matches(page.Text))
                {
                    string raw = match.Groups["url"].Success ? match.Groups["url"].Value : match.Groups["css"].Value;
                    if (!Uri.TryCreate(page.FinalUri, WebUtility.HtmlDecode(raw), out var assetUri) ||
                        assetUri.Scheme != Uri.UriSchemeHttps || !referenced.Add(assetUri.AbsoluteUri)) continue;
                    record.CandidateCount++;
                    if (candidates.Count >= MaxSvgPerSite)
                    {
                        record.SelectionTruncated = true;
                        continue;
                    }
                    try
                    {
                        // href="...svg" may be a human-facing HTML file page rather than an image.
                        // Fetch under the same byte/SSRF limits and admit only content that actually contains SVG.
                        var asset = await FetchAsync(client, assetUri, MaxSvgBytes);
                        if (!asset.Text.Contains("<svg", StringComparison.OrdinalIgnoreCase)) continue;
                        candidates.Add((asset.Text, asset.FinalUri.AbsoluteUri));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
                    {
                        record.AssetFailures++;
                        if (record.AssetErrors.Count < 16)
                            record.AssetErrors.Add(new CaptureAssetError
                            {
                                SourceUrl = assetUri.AbsoluteUri,
                                Error = Bound(ex.Message)
                            });
                    }
                }

                int index = 0;
                foreach (var candidate in candidates)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(candidate.Svg);
                    if (bytes.Length > MaxSvgBytes || totalBytes + bytes.Length > MaxTotalSvgBytes)
                    {
                        record.SelectionTruncated = true;
                        continue;
                    }
                    string relative = $"{site.Id}/{index:D3}.svg";
                    string full = Path.Combine(corpusDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    await File.WriteAllBytesAsync(full, bytes);
                    manifest.Files.Add(new CaptureFileRecord
                    {
                        Path = relative,
                        Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        SourceUrl = candidate.Source,
                        SiteId = site.Id
                    });
                    totalBytes += bytes.Length;
                    index++;
                }
                record.SvgCount = index;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException or SocketException)
            {
                record.Status = "failed";
                record.Error = Bound(ex.Message);
                allSitesFetched = false;
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                probe = "svg-site-capture-progress",
                site = site.Id,
                status = record.Status,
                        svg = record.SvgCount,
                        candidates = record.CandidateCount,
                        truncated = record.SelectionTruncated,
                assetFailures = record.AssetFailures
            }));
        }

        string manifestPath = Path.Combine(outputDirectory, "manifest.json");
        await WriteAllTextAtomicAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        bool complete = allSitesFetched && manifest.Files.Count > 0 &&
                        manifest.Sites.All(site => site.AssetFailures == 0 && !site.SelectionTruncated);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            probe = "svg-site-capture",
            ok = complete,
            sites = manifest.Sites.Count,
            successfulSites = manifest.Sites.Count(site => site.Status == "captured"),
            svg = manifest.Files.Count,
            bytes = totalBytes,
            corpus = corpusDirectory,
            manifest = manifestPath
        }));
        return complete ? 0 : 1;
    }

    private static async Task<FetchResult> FetchAsync(HttpClient client, Uri initial, int maxBytes, params string[] allowedTypes)
    {
        Uri current = initial;
        for (int redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            await ValidatePublicHttpsAsync(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
            {
                current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {current.Host}");
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (allowedTypes.Length > 0 && !allowedTypes.Any(type => mediaType.Equals(type, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"unexpected content type '{mediaType}' from {current.Host}");
            long? declared = response.Content.Headers.ContentLength;
            if (declared > maxBytes) throw new InvalidDataException($"response from {current.Host} exceeds byte budget");
            await using var stream = await response.Content.ReadAsStreamAsync();
            byte[] bytes = await ReadBoundedAsync(stream, maxBytes);
            return new FetchResult(current, response.StatusCode, Encoding.UTF8.GetString(bytes));
        }
        throw new HttpRequestException("redirect budget exceeded");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes)
    {
        using var memory = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0) return memory.ToArray();
            if (memory.Length + read > maxBytes) throw new InvalidDataException("response exceeds byte budget");
            memory.Write(buffer, 0, read);
        }
    }

    private static async Task ValidatePublicHttpsAsync(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0)
            throw new InvalidDataException("capture URL must be credential-free HTTPS");
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost);
        if (addresses.Length == 0 || addresses.Any(IsPrivateOrSpecial))
            throw new InvalidDataException($"capture host does not resolve exclusively to public addresses: {uri.Host}");
    }

    private static async ValueTask<Stream> ConnectPublicEndpointAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsPrivateOrSpecial))
            throw new HttpRequestException(
                $"capture host does not resolve exclusively to public addresses: {context.DnsEndPoint.Host}");

        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (ex is OperationCanceledException) throw;
                lastError = ex;
            }
        }
        throw new HttpRequestException(
            $"capture host connection failed: {context.DnsEndPoint.Host}",
            lastError);
    }

    private static async Task WriteAllTextAtomicAsync(string path, string content)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsPrivateOrSpecial(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] is 0 or 10 or 127 || b[0] >= 224 ||
               b[0] == 169 && b[1] == 254 ||
               b[0] == 172 && b[1] is >= 16 and <= 31 ||
               b[0] == 192 && b[1] == 168 ||
               b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    private static void ValidateSite(CaptureSite site)
    {
        if (string.IsNullOrWhiteSpace(site.Id) || site.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("site id must contain only ASCII letters, digits, or hyphens");
        if (!Uri.TryCreate(site.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"site URL must be absolute HTTPS: {site.Id}");
    }

    private static string Bound(string value) => value[..Math.Min(256, value.Length)];
    private sealed record FetchResult(Uri FinalUri, HttpStatusCode Status, string Text);
    private sealed class CaptureConfig { public int SchemaVersion { get; set; } public List<CaptureSite> Sites { get; set; } = new(); }
    private sealed class CaptureSite { public string Id { get; set; } = string.Empty; public string Url { get; set; } = string.Empty; }
    private sealed class CaptureManifest
    {
        public int SchemaVersion { get; set; }
        public string CorpusKind { get; set; } = string.Empty;
        public DateTimeOffset CapturedAtUtc { get; set; }
        public List<CaptureSiteRecord> Sites { get; set; } = new();
        public List<CaptureFileRecord> Files { get; set; } = new();
    }
    private sealed class CaptureSiteRecord
    {
        public string Id { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public string FinalUrl { get; set; } = string.Empty;
        public int HttpStatus { get; set; }
        public string Status { get; set; } = string.Empty;
        public int SvgCount { get; set; }
        public int CandidateCount { get; set; }
        public bool SelectionTruncated { get; set; }
        public int AssetFailures { get; set; }
        public List<CaptureAssetError> AssetErrors { get; set; } = new();
        public string? Error { get; set; }
    }
    private sealed class CaptureAssetError
    {
        public string SourceUrl { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }
    private sealed class CaptureFileRecord
    {
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public string SiteId { get; set; } = string.Empty;
    }
}
