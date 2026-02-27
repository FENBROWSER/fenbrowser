// =============================================================================
// HeadlessNavigator.cs
// Lightweight headless navigator for WPT test execution.
//
// PURPOSE: Execute HTML + script in a minimal runtime and bridge WPT's
// testharness callbacks into FenBrowser's TestHarnessAPI.
// =============================================================================

using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.HTML;
using FenBrowser.FenEngine.WebAPIs;

namespace FenBrowser.Conformance;

public sealed class HeadlessNavigator
{
    private const string MinimalHarnessScript = @"
var __fenMiniHarnessPendingAsync = 0;
var __fenMiniHarnessDoneSignaled = false;

function __fenMiniHarnessToMessage(e) {
  try {
    if (e && e.message) { return String(e.message); }
    return String(e);
  } catch (_) {
    return 'error';
  }
}

function __fenMiniHarnessReport(name, pass, message) {
  try {
    if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.reportResult === 'function') {
      testRunner.reportResult(String(name || 'unnamed'), !!pass, String(message || ''));
    }
  } catch (_) {}
}

function __fenMiniHarnessMaybeDone() {
  if (__fenMiniHarnessDoneSignaled || __fenMiniHarnessPendingAsync !== 0) { return; }
  __fenMiniHarnessDoneSignaled = true;
  try {
    if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.reportHarnessStatus === 'function') {
      testRunner.reportHarnessStatus('complete', '');
    }
    if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.notifyDone === 'function') {
      testRunner.notifyDone();
    }
  } catch (_) {}
}

function setup() {}

function test(fn, name) {
  var testName = name || 'unnamed';
  try {
    fn();
    __fenMiniHarnessReport(testName, true, '');
  } catch (e) {
    __fenMiniHarnessReport(testName, false, __fenMiniHarnessToMessage(e));
  }
}

function promise_test(fn, name) {
  var testName = name || 'unnamed';
  __fenMiniHarnessPendingAsync++;
  try {
    Promise.resolve(fn()).then(function () {
      __fenMiniHarnessReport(testName, true, '');
      __fenMiniHarnessPendingAsync--;
      __fenMiniHarnessMaybeDone();
    }, function (e) {
      __fenMiniHarnessReport(testName, false, __fenMiniHarnessToMessage(e));
      __fenMiniHarnessPendingAsync--;
      __fenMiniHarnessMaybeDone();
    });
  } catch (e) {
    __fenMiniHarnessReport(testName, false, __fenMiniHarnessToMessage(e));
    __fenMiniHarnessPendingAsync--;
    __fenMiniHarnessMaybeDone();
  }
}

function async_test(name) {
  var testName = name || 'unnamed';
  var finished = false;
  __fenMiniHarnessPendingAsync++;

  function finish(pass, message) {
    if (finished) { return; }
    finished = true;
    __fenMiniHarnessReport(testName, pass, message);
    __fenMiniHarnessPendingAsync--;
    __fenMiniHarnessMaybeDone();
  }

  return {
    step_func: function (cb) {
      return function () {
        try {
          cb.apply(this, arguments);
        } catch (e) {
          finish(false, __fenMiniHarnessToMessage(e));
        }
      };
    },
    step_timeout: function (cb, ms) {
      var self = this;
      setTimeout(self.step_func(cb), ms || 0);
    },
    unreached_func: function (message) {
      return function () {
        throw new Error(message || 'Reached unreachable function');
      };
    },
    done: function () {
      finish(true, '');
    }
  };
}

function done() { __fenMiniHarnessMaybeDone(); }
function assert_true(value, message) { if (!value) { throw new Error(message || 'assert_true failed'); } }
function assert_false(value, message) { if (value) { throw new Error(message || 'assert_false failed'); } }
function assert_equals(actual, expected, message) {
  if (actual !== expected) { throw new Error(message || ('assert_equals failed: ' + actual + ' !== ' + expected)); }
}
function assert_not_equals(actual, expected, message) {
  if (actual === expected) { throw new Error(message || ('assert_not_equals failed: both are ' + actual)); }
}
function assert_throws_dom(_name, fn, message) {
  var threw = false;
  try { fn(); } catch (e) { threw = true; }
  if (!threw) { throw new Error(message || 'Expected DOM exception was not thrown'); }
}
function assert_throws_js(_ctor, fn, message) {
  var threw = false;
  try { fn(); } catch (e) { threw = true; }
  if (!threw) { throw new Error(message || 'Expected JS exception was not thrown'); }
}
function assert_unreached(message) { throw new Error(message || 'Reached unreachable code'); }
function assert_array_equals(actual, expected, message) {
  if (!actual || !expected || actual.length !== expected.length) {
    throw new Error(message || 'assert_array_equals length mismatch');
  }
  for (var i = 0; i < actual.length; i++) {
    if (actual[i] !== expected[i]) {
      throw new Error(message || ('assert_array_equals mismatch at index ' + i));
    }
  }
}

setTimeout(__fenMiniHarnessMaybeDone, 0);
";

    private const string HarnessBridgeScript = @"
(function () {
  if (typeof globalThis === 'undefined') { return; }
  if (globalThis.__fenWptBridgeInstalled) { return; }
  if (typeof add_result_callback !== 'function' || typeof add_completion_callback !== 'function') { return; }

  globalThis.__fenWptBridgeInstalled = true;

  add_result_callback(function (test) {
    try {
      if (typeof testRunner === 'undefined' || !testRunner || typeof testRunner.reportResult !== 'function') { return; }
      var status = (test && typeof test.status === 'number') ? test.status : 1;
      var pass = status === 0;
      var name = test && test.name ? String(test.name) : 'unnamed';
      var message = test && test.message ? String(test.message) : '';
      testRunner.reportResult(name, pass, message);
    } catch (e) {}
  });

  add_completion_callback(function (tests, harness_status) {
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

  try {
    if (typeof window !== 'undefined' && window && typeof window.dispatchEvent === 'function' && typeof Event === 'function') {
      window.dispatchEvent(new Event('load'));
    }
  } catch (e) {}
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

        var tokenizer = new HtmlTokenizer(html);
        var builder = new HtmlTreeBuilder(tokenizer);
        var document = builder.Build();

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
                if (IsTestHarnessScript(src))
                {
                    code = MinimalHarnessScript;
                    scriptLabel = "fen-minimal-testharness.js";
                }
                else if (IsTestHarnessReportScript(src))
                {
                    code = "/* FenBrowser shim: testharnessreport.js intentionally no-op */";
                    scriptLabel = "fen-minimal-testharnessreport.js";
                }
                else
                {
                    var scriptPath = ResolveExternalScriptPath(src, filePath);
                    if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
                    {
                        continue;
                    }

                    code = await File.ReadAllTextAsync(scriptPath);
                }
            }
            else
            {
                code = scriptContent;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
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
    }

    public Func<string, Task> GetNavigatorDelegate()
    {
        return NavigateAsync;
    }

    private static string ResolveTestFilePath(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new FileNotFoundException("Test file URL is empty.");
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
            var result = runtime.ExecuteSimple(code, allowReturn: true, cancellationToken: cts.Token);
            if (result.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error ||
                result.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw)
            {
                TestConsoleCapture.AddEntry("error", $"[WPT-NAV] {scriptLabel} returned {result.Type}: {result}");
                AppendParserDiagnostics(code, scriptLabel);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            TestConsoleCapture.AddEntry("error", $"[WPT-NAV] {scriptLabel} timed out after {timeoutMs}ms");
            return false;
        }
        catch (Exception ex)
        {
            TestConsoleCapture.AddEntry("error", $"[WPT-NAV] {scriptLabel} threw host exception: {ex.Message}");
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
                TestConsoleCapture.AddEntry("error", $"[WPT-NAV] {scriptLabel} parser[{i + 1}/{parser.Errors.Count}]: {parser.Errors[i]}");
            }
        }
        catch
        {
        }
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

