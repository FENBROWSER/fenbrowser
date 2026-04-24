// =============================================================================
// HeadlessNavigator.cs
// Lightweight headless navigator for WPT test execution.
//
// PURPOSE: Execute HTML + script in a minimal runtime and bridge WPT's
// testharness callbacks into FenBrowser's TestHarnessAPI.
// =============================================================================

using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.WebAPIs;

namespace FenBrowser.Conformance;

public sealed class HeadlessNavigator
{
    private const string HarnessBridgeScript = @"
(function () {
  if (typeof globalThis === 'undefined') { return; }
  if (globalThis.__fenWptBridgeInstalled) { return; }
  var root = (typeof window !== 'undefined' && window) ? window : globalThis;
  var addResultCallback = (typeof root.add_result_callback === 'function') ? root.add_result_callback
                        : ((typeof globalThis.add_result_callback === 'function') ? globalThis.add_result_callback : null);
  var addCompletionCallback = (typeof root.add_completion_callback === 'function') ? root.add_completion_callback
                           : ((typeof globalThis.add_completion_callback === 'function') ? globalThis.add_completion_callback : null);
  if (typeof addResultCallback !== 'function' || typeof addCompletionCallback !== 'function') { return; }

  globalThis.__fenWptBridgeInstalled = true;

  addResultCallback(function (test) {
    try {
      if (typeof testRunner === 'undefined' || !testRunner || typeof testRunner.reportResult !== 'function') { return; }
      var status = (test && typeof test.status === 'number') ? test.status : 1;
      var pass = status === 0;
      var name = test && test.name ? String(test.name) : 'unnamed';
      var message = test && test.message ? String(test.message) : '';
      testRunner.reportResult(name, pass, message);
    } catch (e) {}
  });

  addCompletionCallback(function (tests, harness_status) {
    try {
      if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.reportHarnessStatus === 'function') {
        var message = harness_status && harness_status.message ? String(harness_status.message) : '';
        testRunner.reportHarnessStatus('complete', message);
      }
      if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.notifyDone === 'function') {
        testRunner.notifyDone();
      }
    } catch (e) {}
  });
})();
";

    private const string DispatchWindowLoadScript = @"
(function () {
  try {
    if (typeof globalThis === 'undefined') { return; }
    if (globalThis.__fenWptLoadDispatched) { return; }
    globalThis.__fenWptLoadDispatched = true;
    if (typeof window !== 'undefined' && window && typeof window.dispatchEvent === 'function' && typeof Event === 'function') {
      window.dispatchEvent(new Event('load'));
    }
  } catch (e) {}
})();
";

    private const string MinimalHarnessFallbackScript = @"
(function () {
  if (typeof globalThis === 'undefined') { return; }
  if (globalThis.__fenMinimalHarnessInstalled) { return; }
  globalThis.__fenMinimalHarnessInstalled = true;

  var resultCallbacks = [];
  var completionCallbacks = [];
  var pending = 0;
  var completed = false;

  function completeIfIdle(message) {
    if (completed || pending !== 0) { return; }
    completed = true;
    for (var i = 0; i < completionCallbacks.length; i++) {
      try { completionCallbacks[i]([], { status: 0, message: message || '' }); } catch (e) {}
    }
  }

  globalThis.add_result_callback = function (cb) {
    if (typeof cb === 'function') { resultCallbacks.push(cb); }
  };

  globalThis.add_completion_callback = function (cb) {
    if (typeof cb === 'function') { completionCallbacks.push(cb); }
  };

  globalThis.assert_true = globalThis.assert_true || function (value, message) {
    if (!value) { throw new Error(message || 'assert_true failed'); }
  };

  globalThis.assert_equals = globalThis.assert_equals || function (actual, expected, message) {
    if (actual !== expected) { throw new Error(message || ('assert_equals failed: ' + actual + ' !== ' + expected)); }
  };

  function emitResult(name, status, message) {
    var result = { name: name || 'unnamed', status: status || 0, message: message || '' };
    for (var i = 0; i < resultCallbacks.length; i++) {
      try { resultCallbacks[i](result); } catch (e) {}
    }
  }

  function runSync(name, fn) {
    pending++;
    try {
      fn();
      emitResult(name, 0, '');
    } catch (e) {
      emitResult(name, 1, e && e.message ? String(e.message) : String(e));
    } finally {
      pending--;
      completeIfIdle('');
    }
  }

  function runAsync(name, fn) {
    pending++;
    Promise.resolve()
      .then(fn)
      .then(function () { emitResult(name, 0, ''); })
      .catch(function (e) { emitResult(name, 1, e && e.message ? String(e.message) : String(e)); })
      .finally(function () {
        pending--;
        completeIfIdle('');
      });
  }

  globalThis.test = function (fn, name) {
    runSync(name, fn);
  };

  globalThis.promise_test = function (fn, name) {
    runAsync(name, fn);
  };

  globalThis.setup = function () {};
  globalThis.done = function () { completeIfIdle('done'); };
})();
";

    private readonly string? _wptRootPath;
    private readonly int _timeoutMs;

    public HeadlessNavigator(string? wptRootPath = null, int timeoutMs = 30_000)
    {
        _wptRootPath = string.IsNullOrWhiteSpace(wptRootPath) ? null : wptRootPath;
        _timeoutMs = timeoutMs;
    }

    public async Task NavigateAsync(string url)
    {
        var filePath = ResolveTestFilePath(url);
        var html = await File.ReadAllTextAsync(filePath);

        var document = new HtmlParser(html).Parse();

        var runtime = new FenRuntime();
        TestHarnessAPI.Register(runtime);
        runtime.OnConsoleMessage = msg =>
        {
            var level = "log";
            if (msg.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase)) level = "error";
            else if (msg.StartsWith("[Warn]", StringComparison.OrdinalIgnoreCase)) level = "warn";
            else if (msg.StartsWith("[Info]", StringComparison.OrdinalIgnoreCase)) level = "info";
            TestConsoleCapture.AddEntry(level, msg);
        };

        var scripts = ExtractScripts(document);
        scripts = OrderScriptsDeterministically(scripts);
        var fallbackHarnessInstalled = false;
        var scriptOrdinal = 0;
        foreach (var (src, scriptContent, isExternal) in scripts)
        {
            scriptOrdinal++;
            string code;
            var scriptLabel = isExternal && !string.IsNullOrWhiteSpace(src)
                ? src
                : $"inline-script-{scriptOrdinal}";

            if (isExternal && !string.IsNullOrWhiteSpace(src))
            {
                var scriptPath = ResolveExternalScriptPath(src, filePath);
                if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
                {
                    TestConsoleCapture.AddEntry("error", $"[WPT-DEP-RUNTIME] missing external script: {src}");
                    continue;
                }

                code = await File.ReadAllTextAsync(scriptPath);
            }
            else
            {
                code = scriptContent;
            }

            // In FenRuntime headless mode, ensure a deterministic minimal harness
            // exists before inline page tests execute.
            if (!isExternal && !fallbackHarnessInstalled)
            {
                TryExecuteScript(runtime, MinimalHarnessFallbackScript, Math.Min(_timeoutMs, 2_000), "fen-minimal-harness.js");
                TryExecuteScript(runtime, HarnessBridgeScript, Math.Min(_timeoutMs, 2_000), "fen-harness-bridge.js");
                fallbackHarnessInstalled = true;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            // In this lightweight runtime, upstream testharness scripts are parse-gated
            // but not executed; assertion plumbing is provided by the fallback harness.
            if (isExternal &&
                (IsTestHarnessScript(scriptLabel) || IsTestHarnessReportScript(scriptLabel)))
            {
                AppendParserDiagnostics(code, scriptLabel);
                continue;
            }

            if (!TryExecuteScript(runtime, code, _timeoutMs, scriptLabel))
            {
                break;
            }

            // Bridge is idempotent and only activates once testharness APIs exist.
            TryExecuteScript(runtime, HarnessBridgeScript, Math.Min(_timeoutMs, 2_000), "fen-harness-bridge.js");
        }

        // Final bridge attempt for tests that load harness late in the script list.
        TryExecuteScript(runtime, HarnessBridgeScript, Math.Min(_timeoutMs, 2_000), "fen-harness-bridge.js");
        TryExecuteScript(runtime, DispatchWindowLoadScript, Math.Min(_timeoutMs, 2_000), "fen-window-load-dispatch.js");
    }

    public Func<string, Task> GetNavigatorDelegate()
    {
        return NavigateAsync;
    }

    private string ResolveTestFilePath(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new FileNotFoundException("Test file URL is empty.");
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
        {
            if ((string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(_wptRootPath) &&
                string.Equals(absoluteUri.Host, "web-platform.test", StringComparison.OrdinalIgnoreCase))
            {
                var relative = absoluteUri.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var mapped = Path.GetFullPath(Path.Combine(_wptRootPath!, relative));
                if (File.Exists(mapped))
                {
                    return mapped;
                }
            }
        }

        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(url).LocalPath;
        }

        if (File.Exists(url))
        {
            return Path.GetFullPath(url);
        }

        throw new FileNotFoundException($"Test file not found: {url}");
    }

    private string? ResolveExternalScriptPath(string scriptSrc, string testFilePath)
    {
        if (string.IsNullOrWhiteSpace(scriptSrc))
        {
            return null;
        }

        var cleaned = scriptSrc;
        var queryIx = cleaned.IndexOf('?');
        if (queryIx >= 0) cleaned = cleaned.Substring(0, queryIx);
        var hashIx = cleaned.IndexOf('#');
        if (hashIx >= 0) cleaned = cleaned.Substring(0, hashIx);

        if (Uri.TryCreate(cleaned, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.IsFile && File.Exists(absoluteUri.LocalPath))
            {
                return absoluteUri.LocalPath;
            }

            return null;
        }

        var normalized = cleaned.Replace('/', Path.DirectorySeparatorChar);
        var testDir = Path.GetDirectoryName(testFilePath) ?? string.Empty;

        // Root-absolute WPT path, e.g. /resources/testharness.js.
        if ((normalized.StartsWith(Path.DirectorySeparatorChar) || normalized.StartsWith(Path.AltDirectorySeparatorChar))
            && !string.IsNullOrWhiteSpace(_wptRootPath))
        {
            var rootRelative = normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootAbsolute = Path.GetFullPath(Path.Combine(_wptRootPath!, rootRelative));
            if (File.Exists(rootAbsolute))
            {
                return rootAbsolute;
            }
        }

        // Relative to the current test file directory.
        var fromTest = Path.GetFullPath(Path.Combine(testDir, normalized));
        if (File.Exists(fromTest))
        {
            return fromTest;
        }

        // Fallback to WPT root.
        if (!string.IsNullOrWhiteSpace(_wptRootPath))
        {
            var fromRoot = Path.GetFullPath(
                Path.Combine(_wptRootPath!, normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            if (File.Exists(fromRoot))
            {
                return fromRoot;
            }
        }

        return null;
    }

    private static bool TryExecuteScript(FenRuntime runtime, string code, int timeoutMs, string scriptLabel)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var result = runtime.ExecuteSimple(code, scriptLabel, allowReturn: true, cancellationToken: cts.Token);
            if (result.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error ||
                result.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw)
            {
                TestConsoleCapture.AddEntry("error", $"[WPT-DEP-RUNTIME] {scriptLabel} returned {result.Type}: {result}");
                AppendParserDiagnostics(code, scriptLabel);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            TestConsoleCapture.AddEntry("error", $"[WPT-DEP-RUNTIME] {scriptLabel} timed out after {timeoutMs}ms");
            return false;
        }
        catch (Exception ex)
        {
            TestConsoleCapture.AddEntry("error", $"[WPT-DEP-RUNTIME] {scriptLabel} threw host exception: {ex.Message}");
            return true;
        }
    }

    private static void AppendParserDiagnostics(string code, string scriptLabel)
    {
        try
        {
            var lexer = new Lexer(code);
            var parser = new Parser(lexer);
            parser.ParseProgram();
            if (parser.Errors.Count == 0)
            {
                return;
            }

            var previewCount = Math.Min(3, parser.Errors.Count);
            for (var i = 0; i < previewCount; i++)
            {
                TestConsoleCapture.AddEntry("error", $"[WPT-DEP-PARSE] {scriptLabel} parser[{i + 1}/{parser.Errors.Count}]: {parser.Errors[i]}");
            }
        }
        catch
        {
        }
    }

    private static List<(string? Src, string Content, bool IsExternal)> OrderScriptsDeterministically(
        List<(string? Src, string Content, bool IsExternal)> scripts)
    {
        static int GetRank(string? src, bool isExternal, int fallbackIndex)
        {
            if (!isExternal)
            {
                return 2000 + fallbackIndex;
            }

            var normalized = NormalizeScriptSrc(src);
            if (normalized.EndsWith("/resources/testharness.js", StringComparison.OrdinalIgnoreCase)) return 0;
            if (normalized.EndsWith("/resources/testharnessreport.js", StringComparison.OrdinalIgnoreCase)) return 1;
            if (normalized.EndsWith("/resources/testdriver.js", StringComparison.OrdinalIgnoreCase)) return 2;
            if (normalized.EndsWith("/resources/testdriver-vendor.js", StringComparison.OrdinalIgnoreCase)) return 3;
            if (normalized.EndsWith("/resources/testdriver-actions.js", StringComparison.OrdinalIgnoreCase)) return 4;
            return 1000 + fallbackIndex;
        }

        return scripts
            .Select((script, index) => (script, index))
            .OrderBy(tuple => GetRank(tuple.script.Src, tuple.script.IsExternal, tuple.index))
            .ThenBy(tuple => tuple.index)
            .Select(tuple => tuple.script)
            .ToList();
    }

    private static string NormalizeScriptSrc(string? src)
    {
        if (string.IsNullOrWhiteSpace(src))
        {
            return string.Empty;
        }

        var normalized = src.Replace('\\', '/');
        var queryIx = normalized.IndexOf('?');
        if (queryIx >= 0)
        {
            normalized = normalized.Substring(0, queryIx);
        }

        var hashIx = normalized.IndexOf('#');
        if (hashIx >= 0)
        {
            normalized = normalized.Substring(0, hashIx);
        }

        return normalized;
    }

    private static bool IsTestHarnessScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        return src.Replace('\\', '/').EndsWith("/resources/testharness.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestHarnessReportScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        return src.Replace('\\', '/').EndsWith("/resources/testharnessreport.js", StringComparison.OrdinalIgnoreCase);
    }

    private static List<(string? Src, string Content, bool IsExternal)> ExtractScripts(Document document)
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
            var isExternal = !string.IsNullOrEmpty(src);
            var content = el.TextContent ?? string.Empty;
            scripts.Add((src, content, isExternal));
        }

        if (node.ChildNodes == null)
        {
            return;
        }

        foreach (var child in node.ChildNodes)
        {
            CollectScriptElements(child, scripts);
        }
    }
}

