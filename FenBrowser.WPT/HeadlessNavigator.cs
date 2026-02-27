// =============================================================================
// HeadlessNavigator.cs
// Lightweight headless navigator for WPT test execution
//
// PURPOSE: Simulates page navigation using FenEngine's HTML parser, CSS engine,
//          DOM, and JS runtime — without rendering to a window. Satisfies the
//          Func<string, Task> delegate required by WPTTestRunner.
// =============================================================================

using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.HTML;
using FenBrowser.FenEngine.WebAPIs;

namespace FenBrowser.WPT;

/// <summary>
/// Headless navigator that parses HTML, computes CSS, builds DOM,
/// and executes JavaScript without any visual rendering.
/// Used by WPTTestRunner to satisfy its navigator delegate.
/// </summary>
public sealed class HeadlessNavigator
{
    private readonly int _timeoutMs;

    public HeadlessNavigator(int timeoutMs = 30_000)
    {
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Navigate to a URL (file:// URI or absolute path) and execute the page.
    /// This parses HTML, loads CSS, builds the DOM tree, and executes inline scripts.
    /// </summary>
    public async Task NavigateAsync(string url)
    {
        // Resolve URI to file path
        string filePath;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            filePath = new Uri(url).LocalPath;
        }
        else if (File.Exists(url))
        {
            filePath = url;
        }
        else
        {
            throw new FileNotFoundException($"Test file not found: {url}");
        }

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Test file not found: {filePath}");

        var html = await File.ReadAllTextAsync(filePath);

        // 1. Parse HTML into DOM
        var tokenizer = new HtmlTokenizer(html);
        var builder = new HtmlTreeBuilder(tokenizer);
        var document = builder.Build();

        // 2. Compute styles (needed by many WPT tests that check computed values)
        var baseUri = new Uri("file:///" + filePath.Replace("\\", "/"));
        float viewportW = 800;
        float viewportH = 600;

        try
        {
            await CssLoader.ComputeWithResultAsync(
                document.DocumentElement, baseUri, null, viewportW, viewportH);
        }
        catch
        {
            // CSS computation failures should not block JS test execution
        }

        // 3. Execute inline scripts
        var runtime = new FenRuntime();

        // Register test harness APIs (testRunner, etc.)
        TestHarnessAPI.Register(runtime);

        // Inject console capture
        runtime.OnConsoleMessage = (msg) => { /* Captured by TestConsoleCapture */ };

        // Extract and execute <script> blocks in order
        var scripts = ExtractScripts(document, filePath);
        foreach (var (src, scriptContent, isExternal) in scripts)
        {
            string code;
            if (isExternal && !string.IsNullOrEmpty(src))
            {
                // Resolve external script path relative to test file
                var scriptPath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", src.Replace("/", "\\"));
                if (File.Exists(scriptPath))
                {
                    code = await File.ReadAllTextAsync(scriptPath);
                }
                else
                {
                    // Try relative to WPT root
                    continue;
                }
            }
            else
            {
                code = scriptContent;
            }

            if (string.IsNullOrWhiteSpace(code)) continue;

            try
            {
                using var cts = new CancellationTokenSource(_timeoutMs);
                runtime.ExecuteSimple(code, allowReturn: true, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Script timed out — this is expected for some WPT tests
                break;
            }
            catch
            {
                // Individual script errors should not block subsequent scripts
                // (matches browser behavior)
            }
        }
    }

    /// <summary>
    /// Get the navigator delegate for WPTTestRunner.
    /// </summary>
    public Func<string, Task> GetNavigatorDelegate()
    {
        return NavigateAsync;
    }

    /// <summary>
    /// Extract script elements from the DOM in document order.
    /// Returns (src, inlineContent, isExternal) tuples.
    /// </summary>
    private static List<(string? Src, string Content, bool IsExternal)> ExtractScripts(
        Document document, string testFilePath)
    {
        var scripts = new List<(string? Src, string Content, bool IsExternal)>();
        CollectScriptElements(document.DocumentElement, scripts);
        return scripts;
    }

    private static void CollectScriptElements(
        Node? node,
        List<(string? Src, string Content, bool IsExternal)> scripts)
    {
        if (node == null) return;

        if (node is Element el && string.Equals(el.TagName, "script", StringComparison.OrdinalIgnoreCase))
        {
            var src = el.GetAttribute("src");
            bool isExternal = !string.IsNullOrEmpty(src);
            var content = el.TextContent ?? string.Empty;
            scripts.Add((src, content, isExternal));
        }

        if (node.ChildNodes != null)
        {
            foreach (var child in node.ChildNodes)
            {
                CollectScriptElements(child, scripts);
            }
        }
    }
}
