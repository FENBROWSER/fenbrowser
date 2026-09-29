using System.Net;
using System.Text.Json;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

/// <summary>
/// Runs WPT SVG reftests as top-level documents: each test and reference is loaded
/// by the browser engine (XML DOM, script execution, CSS, layout, paint) from a
/// localhost server over the WPT checkout, rendered at the WPT reftest viewport and
/// compared with the manifest's fuzzy tolerances. This is how the scripted reftests,
/// which the static image runner cannot evaluate, are verified.
///
/// Usage: BenchSvg --wpt-documents &lt;wpt svg dir&gt; --tests &lt;file of relative paths&gt;
///        [--output &lt;dir&gt;] [--settle-ms N] [--wait-ms N]
/// </summary>
internal static class WptDocumentRunner
{
    private const int ViewportWidth = 800;
    private const int ViewportHeight = 600;

    public static async Task<int> RunAsync(string[] args)
    {
        string corpus = Path.GetFullPath(args[1]);
        string testsFile = ReadOption(args, "--tests") ?? throw new ArgumentException("--tests <file> is required");
        string output = Path.GetFullPath(ReadOption(args, "--output") ?? Path.Combine("Results", "svg", "wpt-documents"));
        int settleMs = int.TryParse(ReadOption(args, "--settle-ms"), out int s) ? Math.Clamp(s, 100, 10_000) : 800;
        int waitMs = int.TryParse(ReadOption(args, "--wait-ms"), out int w) ? Math.Clamp(w, 100, 30_000) : 5_000;

        var index = WptManifestIndex.LoadRequired(corpus);
        string wptRoot = index.RootDirectory;
        var tests = File.ReadAllLines(testsFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
        Directory.CreateDirectory(output);
        CssEngineConfig.CurrentEngine = CssEngineType.Custom;

        using var server = new WptStaticServer(wptRoot);
        var results = new List<object>();
        int passes = 0;
        foreach (string relative in tests)
        {
            string fullPath = Path.Combine(corpus, relative.Replace('/', Path.DirectorySeparatorChar));
            var classification = index.Classify(fullPath);
            string testUrl = server.UrlFor(fullPath);
            var (testBitmap, testDiagnostics) = await RenderDocumentAsync(testUrl, settleMs, waitMs);
            var outcomes = new List<object>();
            bool comparable = classification.References.Count > 0 && testBitmap != null;
            bool hasMatch = classification.References.Any(reference => reference.IsMatch);
            bool matched = !hasMatch;
            bool mismatchedAll = true;
            foreach (var reference in classification.References)
            {
                if (reference.FullPath == null)
                {
                    comparable = false;
                    outcomes.Add(new { relation = reference.IsMatch ? "==" : "!=", reference.ManifestUrl, error = "unresolved" });
                    continue;
                }

                var (referenceBitmap, _) = await RenderDocumentAsync(server.UrlFor(reference.FullPath), settleMs, waitMs);
                using (referenceBitmap)
                {
                    if (referenceBitmap == null || testBitmap == null)
                    {
                        comparable = false;
                        continue;
                    }

                    SvgCorpusRunner.CompareWptPixels(testBitmap, referenceBitmap, out int maxDifference, out int differing);
                    bool equal = maxDifference <= reference.MaximumChannelDifference &&
                                 differing <= reference.MaximumDifferingPixels;
                    if (reference.IsMatch && equal) matched = true;
                    if (!reference.IsMatch && equal) mismatchedAll = false;
                    outcomes.Add(new
                    {
                        relation = reference.IsMatch ? "==" : "!=", reference.ManifestUrl, equal,
                        maxDifference, differing,
                        allowedMaxDifference = reference.MaximumChannelDifference,
                        allowedDiffering = reference.MaximumDifferingPixels
                    });
                }
            }

            bool pass = comparable && matched && mismatchedAll;
            if (pass) passes++;
            if (!pass && testBitmap != null)
            {
                SavePng(testBitmap, Path.Combine(output, relative.Replace('/', '_') + ".png"));
            }
            testBitmap?.Dispose();
            results.Add(new { path = relative, pass, comparable, references = outcomes, diagnostics = testDiagnostics });
            Console.WriteLine(JsonSerializer.Serialize(new { probe = "wpt-document", path = relative, pass, comparable }));
        }

        File.WriteAllText(Path.Combine(output, "report.json"),
            JsonSerializer.Serialize(new { passes, total = tests.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { probe = "wpt-documents", passes, total = tests.Count, output }));
        return 0;
    }

    /// <summary>
    /// Loads one document, lets its scripts and resources settle, honours the WPT
    /// reftest-wait protocol (the root carries class reftest-wait until the test is
    /// ready), then paints it at the reftest viewport on white.
    /// </summary>
    private static async Task<(SKBitmap? Bitmap, List<string> Diagnostics)> RenderDocumentAsync(
        string url, int settleMs, int waitMs)
    {
        var diagnostics = new List<string>();
        void OnEvent(EngineLogEvent evt)
        {
            if (evt.Header.Subsystem == LogSubsystem.Js &&
                (evt.Header.Marker == LogMarker.Unimplemented || evt.Header.Severity >= LogSeverity.Error))
            {
                string message = evt.Payload?.MessageTemplate ?? string.Empty;
                lock (diagnostics)
                {
                    if (diagnostics.Count < 20) diagnostics.Add(message.Length > 240 ? message[..240] : message);
                }
            }
        }

        EngineLog.EngineEventWritten += OnEvent;
        try
        {
            using var host = new BrowserHost();
            if (!await host.NavigateAsync(url).ConfigureAwait(false))
            {
                diagnostics.Add("navigation failed");
                return (null, diagnostics);
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            await Task.Delay(settleMs).ConfigureAwait(false);
            while (DateTime.UtcNow < deadline && await IsWaitingAsync(host).ConfigureAwait(false))
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
            if (await IsWaitingAsync(host).ConfigureAwait(false))
            {
                diagnostics.Add("reftest-wait still set at timeout");
            }

            Element? root = host.GetDomRoot();
            if (root == null)
            {
                diagnostics.Add("no document element");
                return (null, diagnostics);
            }

            var bitmap = new SKBitmap(ViewportWidth, ViewportHeight);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                new SkiaDomRenderer().Render(root, canvas, host.ComputedStyles,
                    new SKRect(0, 0, ViewportWidth, ViewportHeight), url);
            }
            return (bitmap, diagnostics);
        }
        finally
        {
            EngineLog.EngineEventWritten -= OnEvent;
        }
    }

    private static async Task<bool> IsWaitingAsync(BrowserHost host)
    {
        try
        {
            object result = await host.ExecuteScriptAsync(
                "document.documentElement && document.documentElement.classList && " +
                "document.documentElement.classList.contains('reftest-wait')").ConfigureAwait(false);
            return string.Equals(result?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static void SavePng(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Read-only static file server for the WPT checkout on a loopback port. It
    /// serves GET only, resolves every request strictly inside the root, and never
    /// lists directories.
    /// </summary>
    private sealed class WptStaticServer : IDisposable
    {
        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".svg"] = "image/svg+xml", [".js"] = "text/javascript", [".css"] = "text/css",
            [".html"] = "text/html", [".htm"] = "text/html", [".xhtml"] = "application/xhtml+xml",
            [".xml"] = "application/xml", [".png"] = "image/png", [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
            [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".json"] = "application/json"
        };

        private readonly string _root;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public WptStaticServer(string root)
        {
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            for (int attempt = 0; ; attempt++)
            {
                Port = Random.Shared.Next(20_000, 60_000);
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                try
                {
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 10)
                {
                }
            }
            _ = Task.Run(ServeAsync);
        }

        public int Port { get; private set; }

        public string UrlFor(string fullPath)
        {
            string relative = Path.GetRelativePath(_root, Path.GetFullPath(fullPath)).Replace('\\', '/');
            return $"http://127.0.0.1:{Port}/{string.Join('/', relative.Split('/').Select(Uri.EscapeDataString))}";
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }
                _ = Task.Run(() => Respond(context));
            }
        }

        private void Respond(HttpListenerContext context)
        {
            using var response = context.Response;
            try
            {
                if (context.Request.HttpMethod != "GET")
                {
                    response.StatusCode = 405;
                    return;
                }

                string relative = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath.TrimStart('/'));
                string path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path))
                {
                    response.StatusCode = 404;
                    return;
                }

                byte[] body = File.ReadAllBytes(path);
                response.ContentType = ContentTypes.TryGetValue(Path.GetExtension(path), out string? type)
                    ? type
                    : "application/octet-stream";
                response.ContentLength64 = body.Length;
                response.OutputStream.Write(body, 0, body.Length);
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or UnauthorizedAccessException)
            {
                response.StatusCode = 500;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
