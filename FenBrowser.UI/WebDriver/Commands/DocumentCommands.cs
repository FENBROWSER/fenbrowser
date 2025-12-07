using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Handles document related WebDriver commands.
    /// Endpoints: Page source, execute script (sync/async)
    /// </summary>
    public class DocumentCommands : IWebDriverCommand
    {
        public bool CanHandle(string method, string path)
        {
            if (!path.Contains("/session/")) return false;

            // GET /session/{id}/source
            if (method == "GET" && Regex.IsMatch(path, @"/session/[^/]+/source/?$")) return true;
            
            // POST /session/{id}/execute/sync
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/execute/sync/?$")) return true;
            
            // POST /session/{id}/execute/async
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/execute/async/?$")) return true;

            // GET /session/{id}/screenshot
            if (method == "GET" && Regex.IsMatch(path, @"/session/[^/]+/screenshot/?$")) return true;

            // POST /session/{id}/print
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/print/?$")) return true;

            return false;
        }

        public async Task<WebDriverResponse> ExecuteAsync(WebDriverContext context)
        {
            if (context.Session == null) return WebDriverResponse.InvalidSession();

            var path = context.Path.TrimEnd('/');

            // GET /session/{id}/source
            if (context.Method == "GET" && path.EndsWith("/source"))
            {
                var source = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.GetPageSourceAsync());
                return WebDriverResponse.Success(source);
            }

            // POST /session/{id}/execute/sync
            if (context.Method == "POST" && path.EndsWith("/execute/sync"))
            {
                if (!context.Body.TryGetProperty("script", out var scriptProp))
                    return WebDriverResponse.Error400("Missing 'script' parameter");

                var script = scriptProp.GetString();
                object[] args = Array.Empty<object>();
                
                if (context.Body.TryGetProperty("args", out var argsProp))
                {
                    // Parse args array
                    var argsArray = new System.Collections.Generic.List<object>();
                    foreach (var arg in argsProp.EnumerateArray())
                    {
                        argsArray.Add(ConvertJsonElement(arg));
                    }
                    args = argsArray.ToArray();
                }

                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\webdriver_debug.txt", $"[execute/sync] Script: {script?.Substring(0, Math.Min(2000, script?.Length ?? 0))}...\r\n"); } catch { }
                var result = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.ExecuteScriptAsync(script, args));
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\webdriver_debug.txt", $"[execute/sync] Result type: {result?.GetType().Name ?? "null"}, Value: {System.Text.Json.JsonSerializer.Serialize(result)}\r\n"); } catch { }
                return WebDriverResponse.Success(result);
            }

            // POST /session/{id}/execute/async
            if (context.Method == "POST" && path.EndsWith("/execute/async"))
            {
                if (!context.Body.TryGetProperty("script", out var scriptProp))
                    return WebDriverResponse.Error400("Missing 'script' parameter");

                var script = scriptProp.GetString();
                object[] args = Array.Empty<object>();
                
                if (context.Body.TryGetProperty("args", out var argsProp))
                {
                    var argsArray = new System.Collections.Generic.List<object>();
                    foreach (var arg in argsProp.EnumerateArray())
                    {
                        argsArray.Add(ConvertJsonElement(arg));
                    }
                    args = argsArray.ToArray();
                }

                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\webdriver_debug.txt", $"[execute/async] Script: {script?.Substring(0, Math.Min(2000, script?.Length ?? 0))}...\r\n"); } catch { }
                var result = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.ExecuteAsyncScriptAsync(script, args, context.Session.ScriptTimeout));
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\webdriver_debug.txt", $"[execute/async] Result type: {result?.GetType().Name ?? "null"}, Value: {System.Text.Json.JsonSerializer.Serialize(result)}\r\n"); } catch { }
                return WebDriverResponse.Success(result);
            }

            // GET /session/{id}/screenshot
            if (context.Method == "GET" && path.EndsWith("/screenshot"))
            {
                var base64 = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.CaptureScreenshotAsync());
                return WebDriverResponse.Success(base64);
            }

            // POST /session/{id}/print
            if (context.Method == "POST" && path.EndsWith("/print"))
            {
                // Parse print options
                double pageWidth = 21.59; // US Letter default (cm)
                double pageHeight = 27.94;
                bool landscape = false;
                double scale = 1.0;

                if (context.Body.TryGetProperty("page", out var pageProp))
                {
                    if (pageProp.TryGetProperty("width", out var w)) pageWidth = w.GetDouble();
                    if (pageProp.TryGetProperty("height", out var h)) pageHeight = h.GetDouble();
                }
                if (context.Body.TryGetProperty("orientation", out var orientProp))
                    landscape = orientProp.GetString() == "landscape";
                if (context.Body.TryGetProperty("scale", out var scaleProp))
                    scale = scaleProp.GetDouble();

                var pdfBase64 = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.PrintToPdfAsync(pageWidth, pageHeight, landscape, scale));
                return WebDriverResponse.Success(pdfBase64);
            }

            return WebDriverResponse.Error404("Document command not found");
        }

        private object ConvertJsonElement(System.Text.Json.JsonElement element)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    return element.GetString();
                case System.Text.Json.JsonValueKind.Number:
                    if (element.TryGetInt32(out var i)) return i;
                    return element.GetDouble();
                case System.Text.Json.JsonValueKind.True:
                    return true;
                case System.Text.Json.JsonValueKind.False:
                    return false;
                case System.Text.Json.JsonValueKind.Null:
                    return null;
                default:
                    return element.ToString();
            }
        }
    }
}
