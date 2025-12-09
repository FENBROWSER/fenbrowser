using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting
{
    public sealed partial class JavaScriptEngine
    {
        // --------------------------- Missing Methods Restoration ---------------------------

        private System.Net.Http.HttpClient _http;

        public bool RunInline(string code, JsContext ctx = null, string evt = null, string target = null)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            if (!_sandbox.Allows(SandboxFeature.InlineScripts))
            {
                RecordSandboxBlock(SandboxFeature.InlineScripts, "RunInline blocked");
                return false;
            }

            try
            {
                // DEBUG: Log execution attempt
                string snippet = code.Length > 100 ? code.Substring(0, 100) + "..." : code;
                snippet = snippet.Replace("\n", " ").Replace("\r", "");
                FenLogger.Debug($"[JS] RunInline (evt={evt} target={target}): {snippet}", LogCategory.JavaScript);

                // ACTUAL EXECUTION
                // We delegate to the main Evaluate method which uses FenRuntime
                var result = Evaluate(code);
                
                return true;
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[JS] RunInline Error: {ex.Message}", LogCategory.JavaScript, ex);
                return false;
            }
        }

        private void TraceFeatureGap(string category, string feature, string detail)
        {
            lock (_featureTraceLock)
            {
                var key = category + ":" + feature;
                if (_lastFeatureTraceKey == key && (DateTime.UtcNow - _lastFeatureTraceTime).TotalSeconds < 1) return;
                _lastFeatureTraceKey = key;
                _lastFeatureTraceTime = DateTime.UtcNow;
                
                FenLogger.Warn($"[FeatureGap] {category} - {feature}: {detail}", LogCategory.FeatureGaps);
            }
        }

        private void EnqueueMicrotaskInternal(Action a)
        {
            if (a == null) return;
            lock (_microtaskLock)
            {
                _microtasks.Enqueue(a);
                if (!_microtaskPumpScheduled)
                {
                    _microtaskPumpScheduled = true;
                    Task.Run(() => PumpMicrotasks());
                }
            }
        }

        private void PumpMicrotasks()
        {
            while (true)
            {
                Action task = null;
                lock (_microtaskLock)
                {
                    if (_microtasks.Count > 0) task = _microtasks.Dequeue();
                    else
                    {
                        _microtaskPumpScheduled = false;
                        return;
                    }
                }
                try 
                { 
                    task?.Invoke(); 
                } 
                catch (Exception ex)
                {
                    FenLogger.Error($"[JS] Microtask Error: {ex.Message}", LogCategory.JavaScript, ex);
                }
            }
        }

        private static string JsEscape(string s, char quote = '"')
        {
            if (string.IsNullOrEmpty(s)) return "";
            var res = s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");
            if (quote == '"') return res.Replace("\"", "\\\"");
            else return res.Replace("'", "\\'");
        }

        public Uri Resolve(Uri baseUri, string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (Uri.TryCreate(baseUri, url, out var result)) return result;
            if (Uri.TryCreate(url, UriKind.Absolute, out result)) return result;
            return null;
        }
        
        public async Task<string> FetchModuleTextAsync(Uri uri, Uri referer)
        {
            if (uri == null) return null;
            if (FetchOverride != null) return await FetchOverride(uri);
            try
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    return await client.GetStringAsync(uri);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[JS] FetchModuleTextAsync Error: {ex.Message}", LogCategory.Network, ex);
                return null; 
            }
        }

        public void ExecuteScriptBlock(string code, string src = null)
        {
             if (string.IsNullOrWhiteSpace(code)) return;
             FenLogger.Debug($"[JS] ExecuteScriptBlock src={src ?? "inline"}", LogCategory.JavaScript);
             RunInline(code);
        }

        public void RegisterUserFunction(string name, Func<object, object> func)
        {
            // Placeholder
        }

        public string EvalToString(string code)
        {
            try
            {
                var result = Evaluate(code);
                return result?.ToString() ?? "";
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[JS] EvalToString failed: {ex.Message}", LogCategory.JavaScript, ex);
                return "";
            }
        }
        
        public int ScheduleTimeout(string code, int ms)
        {
             return ScheduleInterval(code, ms, false);
        }

        public void ClearTimeout(int id)
        {
            ClearInterval(id);
        }
        
        public string RegisterResponseBody(string body)
        {
             var token = Guid.NewGuid().ToString("N");
             lock (_responseLock)
             {
                 if (_responseRegistry.Count >= _responseCapacity)
                 {
                     var first = _responseLru.First;
                     if (first != null)
                     {
                         _responseRegistry.Remove(first.Value);
                         _responseLru.RemoveFirst();
                     }
                 }
                 _responseRegistry[token] = new ResponseEntry(body, DateTime.UtcNow);
                 _responseLru.AddLast(token);
             }
             return token;
        }

        private void BuildSafeSubresourceHeaders(System.Net.Http.HttpRequestMessage req, Uri uri)
        {
            req.Headers.TryAddWithoutValidation("User-Agent", "FenBrowser/1.0");
        }

        private string DecodeBytes(byte[] bytes, string encoding)
        {
            try 
            {
                 // Ignore encoding for now, assume UTF8
                 return Encoding.UTF8.GetString(bytes);
            } 
            catch { return ""; }
        }
    }
}
