using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Diagnostics
{
    /// <summary>
    /// Regression coverage for <see cref="NavigationGlobalsProbe"/> — the
    /// diagnostic that captures the shape of well-known challenge / page
    /// globals plus the cookie-name set at end-of-navigation.
    ///
    /// Invariants under test:
    ///   - Disabled by default; <c>Capture</c> writes nothing.
    ///   - When enabled, the probe writes a JSON file under the logs dir, with
    ///     a stable envelope: <c>schema</c>, <c>capturedAtUtc</c>,
    ///     <c>navigationId</c>, <c>documentUri</c>, <c>snapshot</c>.
    ///   - The snapshot contains a <c>globals</c> object keyed by every name
    ///     in the probe allowlist (each with a <c>type</c> field).
    ///   - For globals defined by the page, <c>exists</c>/<c>type</c> reflect
    ///     reality: a function declares its arity, an object exposes
    ///     <c>ownKeys</c>.
    ///   - When run against the captured Google-challenge fixture
    ///     <c>logs/raw_source_20260518_095401.html</c>, the snapshot reports
    ///     <c>google</c> as defined (the challenge's first inline script sets
    ///     <c>window.google.c = {cap:0}</c>).
    /// </summary>
    [Collection("Engine Tests")]
    public class NavigationGlobalsProbeTests : IDisposable
    {
        private readonly bool _originalEnabled;

        public NavigationGlobalsProbeTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
            _originalEnabled = BrowserSettings.Instance.Logging.LogNavigationGlobals;
        }

        public void Dispose()
        {
            BrowserSettings.Instance.Logging.LogNavigationGlobals = _originalEnabled;
        }

        [Fact]
        public void Disabled_WritesNoFile()
        {
            BrowserSettings.Instance.Logging.LogNavigationGlobals = false;

            var beforeCount = CountSnapshotFiles();
            NavigationGlobalsProbe.Capture(
                new JavaScriptEngine(CreateHost()),
                new Uri("https://example.com/"),
                navigationId: 999_001);
            var afterCount = CountSnapshotFiles();

            Assert.Equal(beforeCount, afterCount);
        }

        [Fact]
        public async Task Enabled_WritesEnvelopedSnapshotWithAllProbeNames()
        {
            BrowserSettings.Instance.Logging.LogNavigationGlobals = true;

            var baseUri = new Uri("https://example.com/probe.html");
            var html = @"<html><body><script>
                window.sgs = function probeFn(a, b, c){ return a+b+c; };
                window.sp  = 'sp-token-value';
                window.ussv = 42;
                window.google = window.google || { c: { cap: 0 } };
            </script></body></html>";
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());
            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            const long navId = 999_002L;
            NavigationGlobalsProbe.Capture(engine, baseUri, navId);

            var file = FindMostRecentSnapshot(navId);
            Assert.NotNull(file);

            using var stream = File.OpenRead(file);
            using var docJson = JsonDocument.Parse(stream);
            var root = docJson.RootElement;

            Assert.Equal(NavigationGlobalsProbe.SchemaVersion, root.GetProperty("schema").GetString());
            Assert.Equal(navId, root.GetProperty("navigationId").GetInt64());
            Assert.Equal(baseUri.AbsoluteUri, root.GetProperty("documentUri").GetString());
            Assert.True(root.TryGetProperty("capturedAtUtc", out _));

            var snapshot = root.GetProperty("snapshot");
            var globals = snapshot.GetProperty("globals");

            // Function global — must report function + arity.
            var sgs = globals.GetProperty("sgs");
            Assert.True(sgs.GetProperty("exists").GetBoolean());
            Assert.Equal("function", sgs.GetProperty("type").GetString());
            Assert.Equal(3, sgs.GetProperty("arity").GetInt32());

            // String global — charCount reflects the character length without
            // leaking the value text.
            var sp = globals.GetProperty("sp");
            Assert.Equal("string", sp.GetProperty("type").GetString());
            Assert.Equal("sp-token-value".Length, sp.GetProperty("charCount").GetInt32());

            // Number global.
            var ussv = globals.GetProperty("ussv");
            Assert.Equal("number", ussv.GetProperty("type").GetString());
            Assert.Equal(42, ussv.GetProperty("value").GetInt32());

            // Object global — exposes ownKeys.
            var google = globals.GetProperty("google");
            Assert.Equal("object", google.GetProperty("type").GetString());
            Assert.True(google.GetProperty("ownKeys").EnumerateArray().Any(e => e.GetString() == "c"));

            // Every name in the allowlist must be represented (double-underscore
            // names appear under their alias key).
            foreach (var expected in new[] { "sgs", "ussv", "sp", "prs", "st", "td", "google",
                                              "challenge_version", "cbs", "ce", "r", "ss_cgi",
                                              "sclm", "sctm", "eid",
                                              "fetch", "XMLHttpRequest", "crypto", "navigator", "performance",
                                              "webdriver", "chrome", "React", "jQuery", "$",
                                              "reactDevtoolsHook_alias", "nextData_alias" })
            {
                Assert.True(globals.TryGetProperty(expected, out var entry),
                    $"Probe must capture global '{expected}'");
                Assert.True(entry.TryGetProperty("type", out _),
                    $"Probe entry for '{expected}' must include a type field");
            }
        }

        [Fact]
        public async Task Enabled_OnGoogleChallengeFixture_CapturesGoogleGlobal()
        {
            var fixturePath = Path.Combine(
                FindWorkspaceRoot(),
                "logs",
                "raw_source_20260518_095401.html");
            if (!File.Exists(fixturePath))
            {
                // The fixture is a captured run artifact; absent on fresh checkouts.
                // Skip rather than fail — the synthetic test above covers the contract.
                return;
            }

            BrowserSettings.Instance.Logging.LogNavigationGlobals = true;

            var baseUri = new Uri("https://www.google.com/search?q=test");
            var html = File.ReadAllText(fixturePath);
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());
            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            const long navId = 999_003L;
            NavigationGlobalsProbe.Capture(engine, baseUri, navId);

            var file = FindMostRecentSnapshot(navId);
            Assert.NotNull(file);

            using var stream = File.OpenRead(file);
            using var docJson = JsonDocument.Parse(stream);
            var snapshot = docJson.RootElement.GetProperty("snapshot");
            var globals = snapshot.GetProperty("globals");

            // The challenge page's first inline script unconditionally seeds
            // window.google = window.google || {}; so the probe must observe it.
            var google = globals.GetProperty("google");
            Assert.Equal("object", google.GetProperty("type").GetString());
            Assert.True(google.GetProperty("exists").GetBoolean());
        }

        // ── helpers ──

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }

        private static int CountSnapshotFiles()
        {
            var dir = DiagnosticPaths.GetLogsDirectory();
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "nav_globals_*.json").Count();
        }

        private static string FindMostRecentSnapshot(long navigationId)
        {
            var dir = DiagnosticPaths.GetLogsDirectory();
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, $"nav_globals_{navigationId}_*.json")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static string FindWorkspaceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FenBrowser.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
