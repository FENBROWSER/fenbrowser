// SpecRef: WHATWG HTML lifecycle (document ready / load events) — diagnostics only
// CapabilityId: DIAG-NAV-GLOBALS-01
// Determinism: strict
// FallbackPolicy: silent (diagnostics must never throw into the navigation path)
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// End-of-navigation diagnostic probe that captures the presence and shape
    /// of a fixed allowlist of <c>window</c> globals plus the cookie-name set,
    /// then writes a JSON snapshot under <c>logs/</c> and emits one structured
    /// log line summarizing the result.
    ///
    /// Purpose: when a page completes navigation but never reaches its expected
    /// state (e.g. Google's challenge page that never sets the SG_SS cookie),
    /// the snapshot tells us whether the challenge bundles defined their globals,
    /// what cookies the jar actually received, and whether key promise outcomes
    /// landed — without modifying the page or guessing from log scraps.
    ///
    /// Gating:
    ///   - <c>FEN_NAV_GLOBALS_SNAPSHOT=1</c> env var, OR
    ///   - <c>BrowserSettings.Logging.LogNavigationGlobals = true</c>.
    /// Off by default.
    ///
    /// The probe script is deterministic, bounded (no recursion, fixed name list,
    /// truncated key arrays), and pure-read; it does not mutate page state.
    /// </summary>
    public static class NavigationGlobalsProbe
    {
        private const string EnvVar = "FEN_NAV_GLOBALS_SNAPSHOT";
        private static readonly bool _envEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable(EnvVar),
                "1",
                StringComparison.Ordinal);

        // SemVer-style schema tag attached to every snapshot file so older
        // captures remain interpretable when the probe shape evolves.
        public const string SchemaVersion = "1.0";

        public static bool Enabled
        {
            get
            {
                if (_envEnabled)
                {
                    return true;
                }

                try
                {
                    return BrowserSettings.Instance?.Logging?.LogNavigationGlobals == true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Capture a snapshot and write it to <c>logs/nav_globals_*.json</c>.
        /// Safe to call from any thread; all failures are swallowed.
        /// </summary>
        /// <param name="engine">Live JavaScriptEngine for the current document.</param>
        /// <param name="documentUri">Final URL of the navigation.</param>
        /// <param name="navigationId">Navigation id used in the artifact filename and log fields.</param>
        /// <summary>
        /// Hard ceiling for the probe's JS execution. The probe is a small
        /// fixed-allocation script (≈2 KB) and should complete in single-digit
        /// milliseconds against a healthy runtime. If it doesn't, the runtime
        /// is under load or contended — in that case we abort rather than
        /// hold the engine lock long enough to stall the navigation lifecycle
        /// (loading-state, favicon swap, etc.) that the caller is about to
        /// run after us.
        /// </summary>
        public const int ProbeTimeoutMs = 250;

        public static void Capture(JavaScriptEngine engine, Uri documentUri, long navigationId)
        {
            if (!Enabled || engine == null)
            {
                return;
            }

            string rawJson;
            try
            {
                // The probe must NEVER block the navigation lifecycle. Run on a
                // background thread and bound by ProbeTimeoutMs. Engine.Evaluate
                // internally serializes on the runtime lock, so this still
                // executes safely against the JS runtime — we're just bounding
                // how long we'll wait for it.
                var captureCts = new CancellationTokenSource(ProbeTimeoutMs);
                var task = Task.Run(() =>
                {
                    try
                    {
                        return engine.Evaluate(ProbeScript) as string;
                    }
                    catch
                    {
                        return null;
                    }
                });

                if (!task.Wait(ProbeTimeoutMs))
                {
                    EmitFailureLog(documentUri, navigationId, $"probe-timeout-{ProbeTimeoutMs}ms");
                    return;
                }

                rawJson = task.Result;
            }
            catch (Exception ex)
            {
                EmitFailureLog(documentUri, navigationId, $"probe-threw: {ex.GetBaseException().Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                EmitFailureLog(documentUri, navigationId, "probe-returned-empty");
                return;
            }

            string filePath = null;
            try
            {
                var logsDir = DiagnosticPaths.GetLogsDirectory();
                Directory.CreateDirectory(logsDir);
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
                var fileName = $"nav_globals_{navigationId}_{stamp}.json";
                filePath = Path.Combine(logsDir, fileName);

                var wrapped = WrapSnapshot(rawJson, documentUri, navigationId);
                File.WriteAllText(filePath, wrapped, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                EmitFailureLog(documentUri, navigationId, $"write-failed: {ex.GetBaseException().Message}");
                return;
            }

            EmitSuccessLog(documentUri, navigationId, rawJson, filePath);
        }

        private static string WrapSnapshot(string innerJson, Uri documentUri, long navigationId)
        {
            // The probe returns a JSON string; wrap it in a small envelope without
            // re-parsing (avoids pulling System.Text.Json into the hot path and
            // tolerates any non-strict producer output).
            var sb = new StringBuilder(innerJson.Length + 256);
            sb.Append("{\"schema\":\"").Append(SchemaVersion).Append('"');
            sb.Append(",\"capturedAtUtc\":\"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"navigationId\":").Append(navigationId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"documentUri\":");
            AppendJsonString(sb, documentUri?.AbsoluteUri ?? string.Empty);
            sb.Append(",\"threadId\":").Append(Thread.CurrentThread.ManagedThreadId);
            sb.Append(",\"snapshot\":").Append(innerJson);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (!string.IsNullOrEmpty(s))
            {
                foreach (var c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                            {
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        private static void EmitSuccessLog(Uri documentUri, long navigationId, string rawJson, string filePath)
        {
            try
            {
                var entry = new LogEntry
                {
                    Category = LogCategory.JsExecution,
                    Level = LogLevel.Info,
                    Message = $"[NAV-GLOBALS] navigationId={navigationId} host={documentUri?.Host} path={filePath}"
                };
                entry.WithData("event", "nav-globals-snapshot");
                entry.WithData("navigationId", navigationId);
                entry.WithData("documentUri", documentUri?.AbsoluteUri ?? string.Empty);
                entry.WithData("host", documentUri?.Host ?? string.Empty);
                entry.WithData("snapshotPath", filePath ?? string.Empty);
                entry.WithData("snapshotBytes", rawJson?.Length ?? 0);
                entry.WithData("schema", SchemaVersion);
                LogManager.Log(entry);
            }
            catch
            {
                // swallow
            }
        }

        private static void EmitFailureLog(Uri documentUri, long navigationId, string reason)
        {
            try
            {
                var entry = new LogEntry
                {
                    Category = LogCategory.JsExecution,
                    Level = LogLevel.Warn,
                    Message = $"[NAV-GLOBALS] capture failed navigationId={navigationId} host={documentUri?.Host} reason={reason}"
                };
                entry.WithData("event", "nav-globals-snapshot-failed");
                entry.WithData("navigationId", navigationId);
                entry.WithData("documentUri", documentUri?.AbsoluteUri ?? string.Empty);
                entry.WithData("reason", reason);
                LogManager.Log(entry);
            }
            catch
            {
                // swallow
            }
        }

        // Fixed allowlist of globals to probe. Adding a name here is the only
        // supported way to extend the probe — no dynamic discovery, no full
        // window enumeration. Keep this list ordered: Google-challenge globals
        // first, then general site bootstrap globals, then framework markers.
        private const string ProbeScript = @"
(function(){
  function safe(fn){ try { return fn(); } catch(e) { return { __error: String((e && e.message) || e) }; } }
  // Note: we deliberately avoid an own property named `length` on the entry
  // object. Our JSON.stringify implementation currently serializes any plain
  // object that carries a numeric `length` property as an array — using
  // `charCount` sidesteps that without depending on a serializer fix.
  function probe(name){
    return safe(function(){
      var v = window[name];
      var t = typeof v;
      var entry = { exists: t !== 'undefined', type: t };
      if (t === 'function') {
        entry.arity = v.length;
        try { entry.fnName = v.name || null; } catch (e) { entry.fnName = null; }
      } else if (t === 'object' && v !== null) {
        var keys = [];
        try { keys = Object.keys(v).slice(0, 40); } catch (e) {}
        entry.ownKeys = keys;
      } else if (t === 'string') {
        entry.charCount = v.length;
      } else if (t === 'number' || t === 'boolean') {
        entry.value = v;
      }
      return entry;
    });
  }
  // Allowlist of globals to probe. Names with double-underscore prefixes are
  // currently dropped by our JSON serializer, so they are recorded under a
  // stable alias key suffixed with `_alias` instead of their real name.
  var names = [
    'sgs','ussv','sp','prs','st','td','google',
    'challenge_version','cbs','ce','r','ss_cgi','sclm','sctm','eid',
    'fetch','XMLHttpRequest','crypto','navigator','performance',
    'webdriver','chrome','React','jQuery','$'
  ];
  var aliasedNames = [
    { real: '__REACT_DEVTOOLS_GLOBAL_HOOK__', alias: 'reactDevtoolsHook_alias' },
    { real: '__NEXT_DATA__',                  alias: 'nextData_alias' }
  ];
  var result = {
    url: safe(function(){ return location.href; }),
    readyState: safe(function(){ return document.readyState; }),
    cookieNames: [],
    cookieCharCount: 0,
    globals: {}
  };
  for (var i=0; i<names.length; i++) { result.globals[names[i]] = probe(names[i]); }
  for (var k=0; k<aliasedNames.length; k++) {
    result.globals[aliasedNames[k].alias] = probe(aliasedNames[k].real);
  }
  safe(function(){
    var raw = document.cookie || '';
    result.cookieCharCount = raw.length;
    if (raw){
      var parts = raw.split(';');
      for (var j=0; j<parts.length; j++){
        var eq = parts[j].indexOf('=');
        if (eq > 0) result.cookieNames.push(parts[j].substring(0, eq).trim());
      }
    }
  });
  safe(function(){
    if (window.google && window.google.c) {
      var keys = [];
      try { keys = Object.keys(window.google.c).slice(0, 30); } catch (e) {}
      result.googleC = { type: typeof window.google.c, keys: keys };
    }
  });
  safe(function(){
    result.scriptCount = document.scripts ? document.scripts.length : -1;
    result.formCount = document.forms ? document.forms.length : -1;
    var title = '';
    try { title = document.title || ''; } catch (e) {}
    result.title = title.length > 256 ? title.substring(0,256) : title;
  });
  return JSON.stringify(result);
})();
";
    }
}
